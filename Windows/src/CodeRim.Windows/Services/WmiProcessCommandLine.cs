using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
namespace CodeRim.Windows.Services;

/// <summary>In-process WMI only: command lines containing CSRF tokens never enter a shell or its transcript.</summary>
internal static class WmiProcessCommandLine
{
    private static readonly SemaphoreSlim SingleQuery = new(1, 1);
    internal static async Task<string?> ReadAsync(int processId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (processId <= 0 || !await SingleQuery.WaitAsync(0, token).ConfigureAwait(false)) throw new IOException("IDE discovery is still busy.");
        // ConnectServer has its own maximum wait (up to two minutes). The caller's
        // deadline abandons a late result, while this gate prevents worker buildup.
        var work = Task.Run(() =>
        {
            try { return Read(processId, token); }
            finally { SingleQuery.Release(); }
        }, CancellationToken.None);
        try { return await work.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            if (!work.IsCompleted) _ = work.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
    private static string? Read(int processId, CancellationToken token)
    {
        IWbemLocator? locator = null; IWbemServices? service = null; IEnumWbemClassObject? rows = null; IWbemClassObject? row = null;
        try
        {
            token.ThrowIfCancellationRequested();
            locator = (IWbemLocator)(object)new WbemLocator();
            Check(locator.ConnectServer(@"ROOT\CIMV2", null, null, null, 0x80, null, nint.Zero, out service), "connect");
            token.ThrowIfCancellationRequested();
            Check(SetServiceSecurity(service, 10, 0, nint.Zero, 6, 3, nint.Zero, 0), "service security");
            var query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE ProcessId = " + processId.ToString(CultureInfo.InvariantCulture);
            Check(service.ExecQuery("WQL", query, 0x30, nint.Zero, out rows), "query");
            Check(SetEnumeratorSecurity(rows, 10, 0, nint.Zero, 6, 3, nint.Zero, 0), "enumerator security");
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(4)) throw new IOException("IDE discovery timed out.");
                var result = rows.Next(100, 1, out row, out var count);
                Check(result, "enumerate");
                if (count == 0) { if (result == 1) return null; continue; }
                if (row is null || count != 1) throw new InvalidDataException();
                Check(row.Get("ProcessId", 0, out var actualId, out _, out _), "process ID");
                if (Convert.ToInt64(actualId, CultureInfo.InvariantCulture) != processId) throw new InvalidDataException();
                Check(row.Get("CommandLine", 0, out var value, out _, out _), "command line");
                token.ThrowIfCancellationRequested();
                return value is string { Length: > 0 and <= 65536 } text && !text.Contains('\0') ? text : null;
            }
        }
        finally
        {
            if (row is not null) Marshal.ReleaseComObject(row);
            if (rows is not null) Marshal.ReleaseComObject(rows);
            if (service is not null) Marshal.ReleaseComObject(service);
            if (locator is not null) Marshal.ReleaseComObject(locator);
        }
    }
    private static void Check(int result, string operation)
    {
        // Fixed operation names only: no command line, PID, path or credential.
        if (result < 0) throw new IOException("IDE discovery failed during " + operation + " (0x" + result.ToString("X8", CultureInfo.InvariantCulture) + ").");
    }
    // Security is attached to a specific interface proxy. Marshaling these as
    // object/IUnknown sets a different proxy and leaves ExecQuery/Next at IDENTIFY.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", EntryPoint = "CoSetProxyBlanket", ExactSpelling = true)]
    private static extern int SetServiceSecurity([MarshalAs(UnmanagedType.Interface)] IWbemServices proxy, uint authentication, uint authorization,
        nint principal, uint authenticationLevel, uint impersonationLevel, nint identity, uint capabilities);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ole32.dll", EntryPoint = "CoSetProxyBlanket", ExactSpelling = true)]
    private static extern int SetEnumeratorSecurity([MarshalAs(UnmanagedType.Interface)] IEnumWbemClassObject proxy, uint authentication, uint authorization,
        nint principal, uint authenticationLevel, uint impersonationLevel, nint identity, uint capabilities);

    [ComImport, Guid("4590F811-1D3A-11D0-891F-00AA004B2E24"), ClassInterface(ClassInterfaceType.None)]
    private sealed class WbemLocator { }
    [ComImport, Guid("DC12A687-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemLocator
    {
        [PreserveSig] int ConnectServer([MarshalAs(UnmanagedType.BStr)] string resource, [MarshalAs(UnmanagedType.BStr)] string? user,
            [MarshalAs(UnmanagedType.BStr)] string? password, [MarshalAs(UnmanagedType.BStr)] string? locale, int securityFlags,
            [MarshalAs(UnmanagedType.BStr)] string? authority, nint context, out IWbemServices service);
    }
    [ComImport, Guid("9556DC99-828C-11CF-A37E-00AA003240C7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemServices
    {
        // Uncalled slots retain the SDK vtable order; only ExecQuery is exposed.
        [PreserveSig] int OpenNamespace(); [PreserveSig] int CancelAsyncCall(); [PreserveSig] int QueryObjectSink();
        [PreserveSig] int GetObject(); [PreserveSig] int GetObjectAsync(); [PreserveSig] int PutClass();
        [PreserveSig] int PutClassAsync(); [PreserveSig] int DeleteClass(); [PreserveSig] int DeleteClassAsync();
        [PreserveSig] int CreateClassEnum(); [PreserveSig] int CreateClassEnumAsync(); [PreserveSig] int PutInstance();
        [PreserveSig] int PutInstanceAsync(); [PreserveSig] int DeleteInstance(); [PreserveSig] int DeleteInstanceAsync();
        [PreserveSig] int CreateInstanceEnum(); [PreserveSig] int CreateInstanceEnumAsync();
        [PreserveSig] int ExecQuery([MarshalAs(UnmanagedType.BStr)] string language, [MarshalAs(UnmanagedType.BStr)] string query,
            int flags, nint context, out IEnumWbemClassObject enumerator);
    }
    [ComImport, Guid("027947E1-D731-11CE-A357-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumWbemClassObject
    {
        [PreserveSig] int Reset();
        [PreserveSig] int Next(int timeout, uint count, out IWbemClassObject? value, out uint returned);
    }
    [ComImport, Guid("DC12A681-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemClassObject
    {
        [PreserveSig] int GetQualifierSet();
        [PreserveSig] int Get([MarshalAs(UnmanagedType.LPWStr)] string name, int flags,
            [MarshalAs(UnmanagedType.Struct)] out object value, out int type, out int flavor);
    }
}
