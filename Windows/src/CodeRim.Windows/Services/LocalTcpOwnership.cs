using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
namespace CodeRim.Windows.Services;

internal static class LocalTcpOwnership
{
    private const uint Loopback = 0x0100007f;
    internal static int[] ListeningPorts(int processId) => Rows().Where(row => row.ProcessId == processId && row.State == 2
        && row.LocalAddress is 0 or Loopback).Select(row => row.LocalPort).Where(port => port > 0).Distinct().Order().Take(33).ToArray();
    internal static async ValueTask<Stream> ConnectAsync(VerifiedLocalProcess process, int port, CancellationToken token)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (!process.IsCurrent() || port is < 1 or > 65535) throw new IOException("IDE process changed.");
            await socket.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            var clientPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
            // Check the accepted SERVER endpoint, not our own socket's PID. No TLS
            // handshake or HTTP headers are sent before this ownership check.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (!process.IsCurrent()) throw new IOException("IDE process changed.");
                var matches = Rows().Where(row => row.State == 5 && row.LocalAddress == Loopback
                    && row.LocalPort == port && row.RemoteAddress == Loopback && row.RemotePort == clientPort).ToArray();
                if (matches.Length > 0)
                {
                    if (matches.Length != 1 || matches[0].ProcessId != process.Id) throw new IOException("IDE connection ownership changed.");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                await Task.Delay(25, token).ConfigureAwait(false);
            }
            throw new IOException("IDE connection ownership could not be verified.");
        }
        catch { socket.Dispose(); throw; }
    }
    private readonly record struct Row(uint State, uint LocalAddress, int LocalPort, uint RemoteAddress, int RemotePort, int ProcessId);
    private static List<Row> Rows()
    {
        var size = 0;
        var result = GetExtendedTcpTable(nint.Zero, ref size, false, 2, 5, 0);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (result != 122 || size is < 4 or > 16 * 1024 * 1024) throw new IOException("TCP ownership is unavailable.");
            var allocated = size; var buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                result = GetExtendedTcpTable(buffer, ref size, false, 2, 5, 0);
                if (result == 122) continue;
                if (result != 0 || size < 4 || size > allocated) throw new IOException("TCP ownership is unavailable.");
                var count = Marshal.ReadInt32(buffer);
                if (count < 0 || count > (size - 4) / 24) throw new IOException("Invalid TCP ownership table.");
                var rows = new List<Row>(count);
                for (var index = 0; index < count; index++)
                {
                    var pointer = buffer + 4 + index * 24;
                    static uint Read(nint p, int offset) => unchecked((uint)Marshal.ReadInt32(p, offset));
                    static int Port(uint value) => (int)(((value & 255) << 8) | ((value >> 8) & 255));
                    rows.Add(new(Read(pointer, 0), Read(pointer, 4), Port(Read(pointer, 8)), Read(pointer, 12), Port(Read(pointer, 16)), Marshal.ReadInt32(pointer, 20)));
                }
                return rows;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("TCP ownership changed too frequently.");
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("iphlpapi.dll", SetLastError = false)]
    private static extern uint GetExtendedTcpTable(nint table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
