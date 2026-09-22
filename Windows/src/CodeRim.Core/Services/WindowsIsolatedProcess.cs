using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodeRim.Core.Services;

/// <summary>Creates the process inside a kill-on-close job before its first instruction.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsIsolatedProcess : IDisposable
{
    private readonly PipePair output;
    private readonly PipePair error;
    private readonly PipePair input;
    private SafeFileHandle? job;
    private SafeProcessHandle? process;
    internal Stream Output => output.Server;
    internal Stream Error => error.Server;
    private WindowsIsolatedProcess()
    {
        output = new(PipeDirection.In);
        try
        {
            error = new(PipeDirection.In);
            try { input = new(PipeDirection.Out); }
            catch { error.Dispose(); throw; }
        }
        catch { output.Dispose(); throw; }
    }
    internal static WindowsIsolatedProcess Start(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new FileNotFoundException("Select an installed provider executable.");
        lock (BoundedProcess.CreationLock) return StartLocked(executable, arguments, environment);
    }
    private static WindowsIsolatedProcess StartLocked(string executable, IEnumerable<string> arguments, IReadOnlyDictionary<string, string?> environment)
    {
        var child = new WindowsIsolatedProcess();
        nint attributes = 0, handles = 0, jobList = 0, environmentBlock = 0, commandLine = 0;
        var initialized = false;
        try
        {
            child.job = CreateJobObjectW(0, null);
            if (child.job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(child.job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>())) throw new Win32Exception();
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(0, 2, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(attributes, 2, 0, ref size)) throw new Win32Exception();
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * nint.Size);
            Marshal.WriteIntPtr(handles, 0, child.input.Client.SafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, nint.Size, child.output.Client.SafePipeHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * nint.Size, child.error.Client.SafePipeHandle.DangerousGetHandle());
            jobList = Marshal.AllocHGlobal(nint.Size); Marshal.WriteIntPtr(jobList, child.job.DangerousGetHandle());
            // Only the three child-side standard handles are inherited. The job handle
            // stays with CodeRim so neither normal parent exit nor a crash leaks children.
            if (!UpdateProcThreadAttribute(attributes, 0, 0x20002, handles, (nuint)(3 * nint.Size), 0, 0)
                || !UpdateProcThreadAttribute(attributes, 0, 0x2000D, jobList, (nuint)nint.Size, 0, 0)) throw new Win32Exception();
            var env = new StringBuilder();
            foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (pair.Value is null) continue;
                if (pair.Key.Length == 0 || pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value.Contains('\0'))
                    throw new ArgumentException("Invalid isolated environment.", nameof(environment));
                env.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            }
            env.Append('\0');
            environmentBlock = Marshal.StringToHGlobalUni(env.ToString());
            commandLine = Marshal.StringToHGlobalUni(string.Join(" ", new[] { executable }.Concat(arguments).Select(Quote)));
            var startup = new StartupInfoEx {
                Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100,
                    Input = child.input.Client.SafePipeHandle.DangerousGetHandle(), Output = child.output.Client.SafePipeHandle.DangerousGetHandle(),
                    Error = child.error.Client.SafePipeHandle.DangerousGetHandle() },
                Attributes = attributes
            };
            // JOB_LIST assigns containment atomically at creation (Windows 10+).
            // Fail closed if the OS/parent job does not allow this nested job.
            if (!CreateProcessW(executable, commandLine, 0, 0, true, 0x08080400, environmentBlock,
                Path.GetDirectoryName(executable), ref startup, out var information)) throw new Win32Exception();
            child.process = new SafeProcessHandle(information.Process, ownsHandle: true);
            using (var thread = new SafeWaitHandle(information.Thread, ownsHandle: true)) { }
            child.input.Client.Dispose(); child.output.Client.Dispose(); child.error.Client.Dispose();
            child.input.Dispose(); // read-only command receives EOF on stdin
            return child;
        }
        catch { child.Dispose(); throw; }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(handles); Marshal.FreeHGlobal(jobList);
            Marshal.FreeHGlobal(environmentBlock); Marshal.FreeHGlobal(commandLine);
        }
    }
    private static string Quote(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("Invalid process argument.", nameof(argument));
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    internal Task<int> WaitAsync() => Task.Run(() =>
    {
        if (process is null || WaitForSingleObject(process, uint.MaxValue) != 0 || !GetExitCodeProcess(process, out var code)) throw new Win32Exception();
        return unchecked((int)code);
    });
    internal void KillTree() => job?.Dispose();
    public void Dispose()
    {
        KillTree(); input.Dispose(); output.Dispose(); error.Dispose(); process?.Dispose();
    }

    // Overlapped server reads are cancellable even if a foreign launch accidentally
    // inherits a child-side writer. CurrentUserOnly and an unguessable name restrict
    // pipe connections; the client is connected before the provider starts.
    private sealed class PipePair : IDisposable
    {
        internal NamedPipeServerStream Server { get; }
        internal NamedPipeClientStream Client { get; }
        internal PipePair(PipeDirection direction)
        {
            var name = "coderim-cli-" + Guid.NewGuid().ToString("N");
            Server = new(name, direction, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                Client = new(".", name, direction == PipeDirection.In ? PipeDirection.Out : PipeDirection.In,
                    PipeOptions.None, TokenImpersonationLevel.Anonymous, HandleInheritability.Inheritable);
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    var connected = Server.WaitForConnectionAsync(deadline.Token);
                    try { Client.Connect(2000); connected.GetAwaiter().GetResult(); }
                    catch
                    {
                        deadline.Cancel();
                        try { connected.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
                        throw;
                    }
                }
                catch { Client.Dispose(); throw; }
            }
            catch { Server.Dispose(); throw; }
        }
        public void Dispose() { Client.Dispose(); Server.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved, Desktop, Title;
        public uint X, Y, Width, Height, XCharacters, YCharacters, Fill, Flags;
        public ushort Show, ReservedLength;
        public nint ReservedBytes, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(nint attributes, string? name);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint size);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returned);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void DeleteProcThreadAttributeList(nint list);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application, nint command, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation information);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
}
