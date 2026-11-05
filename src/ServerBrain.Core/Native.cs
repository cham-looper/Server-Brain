using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;

namespace ServerBrain;

/// <summary>A TCP row from the OS connection table.</summary>
public readonly record struct TcpRow(int LocalPort, string LocalAddress, int RemotePort, int Pid, int State)
{
    public const int Listen = 2;
    public const int Established = 5;
    public bool IsListen => State == Listen;
}

public static class PortTable
{
    private static readonly object Gate = new();
    private static DateTime _at = DateTime.MinValue;
    private static List<TcpRow> _rows = new();

    /// <summary>Cached for 1 second so several callers in one tick share a single OS query.</summary>
    public static IReadOnlyList<TcpRow> Snapshot(bool fresh = false)
    {
        lock (Gate)
        {
            if (!fresh && (DateTime.UtcNow - _at).TotalMilliseconds < 1000) return _rows;
            _rows = OperatingSystem.IsWindows() ? ReadWindows() : ReadPortable();
            _at = DateTime.UtcNow;
            return _rows;
        }
    }

    public static IEnumerable<TcpRow> Listeners(bool fresh = false) => Snapshot(fresh).Where(r => r.IsListen);

    public static bool IsListening(int port) => Listeners().Any(r => r.LocalPort == port);

    public static TcpRow? ListenerOn(int port) =>
        Listeners().Where(r => r.LocalPort == port).Cast<TcpRow?>().FirstOrDefault();

    /// <summary>Lowest port that is free on this machine, starting at <paramref name="from"/>.</summary>
    public static int FindFree(int from, IEnumerable<int>? reserved = null)
    {
        var used = Listeners(true).Select(r => r.LocalPort).ToHashSet();
        if (reserved != null) foreach (var r in reserved) used.Add(r);
        for (var p = Math.Max(1024, from); p < 65535; p++) if (!used.Contains(p)) return p;
        return from;
    }

    private static List<TcpRow> ReadPortable()
    {
        var list = new List<TcpRow>();
        try
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            foreach (var l in ip.GetActiveTcpListeners())
                list.Add(new TcpRow(l.Port, l.Address.ToString(), 0, -1, TcpRow.Listen));
            foreach (var c in ip.GetActiveTcpConnections().Where(c => c.State == TcpState.Established))
                list.Add(new TcpRow(c.LocalEndPoint.Port, c.LocalEndPoint.Address.ToString(), c.RemoteEndPoint.Port, -1, TcpRow.Established));
        }
        catch { }
        return list;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    private static List<TcpRow> ReadWindows()
    {
        var rows = new List<TcpRow>(256);
        ReadFamily(rows, 2);   // AF_INET
        ReadFamily(rows, 23);  // AF_INET6
        return rows;
    }

    private static void ReadFamily(List<TcpRow> rows, int af)
    {
        var size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, 5, 0);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                var rc = GetExtendedTcpTable(buf, ref size, false, af, 5, 0);
                if (rc == 122) { size += 4096; continue; }   // ERROR_INSUFFICIENT_BUFFER: table grew
                if (rc != 0) return;
                var count = Marshal.ReadInt32(buf);
                var p = buf + 4;
                var rowSize = af == 2 ? 24 : 56;
                for (var i = 0; i < count; i++, p += rowSize)
                {
                    if (af == 2)
                    {
                        var state = Marshal.ReadInt32(p, 0);
                        var laddr = (uint)Marshal.ReadInt32(p, 4);
                        var lport = Swap(Marshal.ReadInt32(p, 8));
                        var rport = Swap(Marshal.ReadInt32(p, 16));
                        var pid = Marshal.ReadInt32(p, 20);
                        rows.Add(new TcpRow(lport, new IPAddress(laddr).ToString(), rport, pid, state));
                    }
                    else
                    {
                        var addr = new byte[16];
                        Marshal.Copy(p, addr, 0, 16);
                        var lport = Swap(Marshal.ReadInt32(p, 20));
                        var rport = Swap(Marshal.ReadInt32(p, 44));
                        var state = Marshal.ReadInt32(p, 48);
                        var pid = Marshal.ReadInt32(p, 52);
                        rows.Add(new TcpRow(lport, new IPAddress(addr).ToString(), rport, pid, state));
                    }
                }
                return;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    private static int Swap(int netOrder) => ((netOrder & 0xFF) << 8) | ((netOrder >> 8) & 0xFF);
}

public static class ProcInfo
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder name, ref int size);

    public static string? Path(int pid)
    {
        if (pid <= 0) return null;
        if (OperatingSystem.IsWindows())
        {
            var h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                var size = sb.Capacity;
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
        try { return Process.GetProcessById(pid).MainModule?.FileName; } catch { return null; }
    }

    public static string Name(int pid)
    {
        if (pid == 4) return "System";
        if (pid == 0) return "Idle";
        var p = Path(pid);
        if (!string.IsNullOrEmpty(p)) return System.IO.Path.GetFileName(p);
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "unknown"; }
    }

    public static bool IsProtected(int pid)
    {
        if (pid <= 4 || pid == Environment.ProcessId) return true;
        var path = Path(pid);
        if (path == null) return true;     // can't identify it (usually an elevated/system service): never offer to kill it
        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win) && path.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { }
        var n = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n is "system" or "svchost" or "lsass" or "wininit" or "services" or "csrss" or "smss" or "winlogon" or "explorer" or "dwm" or "spoolsv" or "msmpeng" or "docker" or "com.docker.backend";
    }
}

public static class SystemStats
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private static long _lastIdle, _lastTotal;

    public static (double cpu, double usedMb, double totalMb) Read()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                GlobalMemoryStatusEx(ref m);
                double cpu = 0;
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    var total = kernel + user;
                    var dIdle = idle - _lastIdle;
                    var dTotal = total - _lastTotal;
                    if (_lastTotal != 0 && dTotal > 0) cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
                    _lastIdle = idle; _lastTotal = total;
                }
                return (cpu, (m.ullTotalPhys - m.ullAvailPhys) / 1048576.0, m.ullTotalPhys / 1048576.0);
            }
            var info = File.ReadAllLines("/proc/meminfo");
            double Kb(string key) => double.Parse(info.First(l => l.StartsWith(key)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024.0;
            var tot = Kb("MemTotal:");
            return (0, tot - Kb("MemAvailable:"), tot);
        }
        catch { return (0, 0, 0); }
    }
}

/// <summary>Owns a project's whole process tree: stop = kill the lot, even orphaned grandchildren.</summary>
public interface IProcessContainer : IDisposable
{
    void Add(Process process);
    bool HasLiveProcesses { get; }
    IReadOnlyList<int> ProcessIds { get; }
    TimeSpan TotalCpuTime { get; }
    void KillAll();
}

public static class ProcessContainer
{
    public static IProcessContainer Create() =>
        OperatingSystem.IsWindows() ? new WindowsJob() : new TreeContainer();
}

internal sealed class WindowsJob : IProcessContainer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinWs, MaxWs;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUser, ThisPeriodKernel;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attrs, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int cls, ref ExtendedLimit info, int len);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int cls, out Accounting info, int len, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(IntPtr job, int cls, IntPtr buf, int len, IntPtr ret);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    private IntPtr _job;

    public WindowsJob()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero) throw new InvalidOperationException("Could not create a job object (error " + Marshal.GetLastWin32Error() + ").");
        // KILL_ON_JOB_CLOSE: if Server Brain itself dies, nothing it started is left running unmanaged.
        var info = new ExtendedLimit { Basic = { LimitFlags = 0x2000 } };
        SetInformationJobObject(_job, 9, ref info, Marshal.SizeOf<ExtendedLimit>());
    }

    public void Add(Process process)
    {
        if (!AssignProcessToJobObject(_job, process.Handle))
            throw new InvalidOperationException("Could not attach the process to its job (error " + Marshal.GetLastWin32Error() + ").");
    }

    private Accounting Query()
    {
        QueryInformationJobObject(_job, 1, out var a, Marshal.SizeOf<Accounting>(), IntPtr.Zero);
        return a;
    }

    public bool HasLiveProcesses => _job != IntPtr.Zero && Query().ActiveProcesses > 0;

    public TimeSpan TotalCpuTime
    {
        get
        {
            if (_job == IntPtr.Zero) return TimeSpan.Zero;
            var a = Query();
            return TimeSpan.FromTicks(a.TotalUserTime + a.TotalKernelTime);
        }
    }

    public IReadOnlyList<int> ProcessIds
    {
        get
        {
            if (_job == IntPtr.Zero) return Array.Empty<int>();
            const int max = 512;
            var len = 8 + IntPtr.Size * max;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!QueryInformationJobObject(_job, 3, buf, len, IntPtr.Zero)) return Array.Empty<int>();
                var n = Marshal.ReadInt32(buf, 4);
                var ids = new List<int>(n);
                for (var i = 0; i < n; i++) ids.Add((int)Marshal.ReadIntPtr(buf, 8 + i * IntPtr.Size));
                return ids;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
    }

    public void KillAll()
    {
        if (_job != IntPtr.Zero) TerminateJobObject(_job, 1);
    }

    public void Dispose()
    {
        if (_job == IntPtr.Zero) return;
        CloseHandle(_job);   // KILL_ON_JOB_CLOSE finishes off anything still alive
        _job = IntPtr.Zero;
    }
}

/// <summary>Non-Windows fallback: kills the root process and the children it can still see.</summary>
internal sealed class TreeContainer : IProcessContainer
{
    private Process? _root;
    public void Add(Process process) => _root = process;
    public bool HasLiveProcesses { get { try { return _root is { HasExited: false }; } catch { return false; } } }
    public IReadOnlyList<int> ProcessIds => _root is { HasExited: false } ? new[] { _root.Id } : Array.Empty<int>();
    public TimeSpan TotalCpuTime { get { try { return _root?.TotalProcessorTime ?? TimeSpan.Zero; } catch { return TimeSpan.Zero; } } }
    public void KillAll() { try { _root?.Kill(entireProcessTree: true); } catch { } }
    public void Dispose() { KillAll(); _root?.Dispose(); _root = null; }
}
