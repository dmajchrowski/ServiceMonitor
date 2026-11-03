using System.Net;
using System.Runtime.InteropServices;

namespace ServiceMonitor.Core.Windows;

public sealed record ListeningPort(string Address, int Port, int ProcessId)
{
    /// <summary>The address a local health check should connect to.</summary>
    public string ProbeHost => Address switch
    {
        "0.0.0.0" => "127.0.0.1",
        "::" => "::1",
        _ => Address,
    };
}

/// <summary>Lists TCP ports in the LISTEN state together with the owning process (like "netstat -ano").</summary>
public static class ListeningPorts
{
    public static IReadOnlyList<ListeningPort> ForProcess(int processId) =>
        processId <= 0 ? [] : GetAll().Where(p => p.ProcessId == processId).ToList();

    public static IReadOnlyList<ListeningPort> GetAll()
    {
        var result = new List<ListeningPort>();
        Read(AfInet, result);
        Read(AfInet6, result);
        return result.DistinctBy(p => (p.Address, p.Port, p.ProcessId)).OrderBy(p => p.Port).ToList();
    }

    private const int AfInet = 2, AfInet6 = 23, TcpTableOwnerPidListener = 3;

    private static void Read(int family, List<ListeningPort> into)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);
        if (size == 0) return;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidListener, 0) != 0) return;
            var count = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            if (family == AfInet)
            {
                var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                for (var i = 0; i < count; i++, rowPtr += rowSize)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(rowPtr);
                    into.Add(new ListeningPort(new IPAddress(row.LocalAddr).ToString(), PortFromNetworkOrder(row.LocalPort), (int)row.OwningPid));
                }
            }
            else
            {
                var rowSize = Marshal.SizeOf<MibTcp6RowOwnerPid>();
                for (var i = 0; i < count; i++, rowPtr += rowSize)
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(rowPtr);
                    into.Add(new ListeningPort(new IPAddress(row.LocalAddr).ToString(), PortFromNetworkOrder(row.LocalPort), (int)row.OwningPid));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int PortFromNetworkOrder(uint port) => (int)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId;
        public uint LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId;
        public uint RemotePort;
        public uint State;
        public uint OwningPid;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
}
