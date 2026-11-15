using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskScope;

public readonly record struct ProcKey(int Pid, long Create);

public sealed class RawProc
{
    public int Pid, ParentPid, Threads, Handles, SessionId, BasePriority;
    public long CreateTime, CpuTime, PrivateBytes, WorkingSet, IoBytes;
    public string Name = "";
    public bool Suspended;
    /// <summary>Per-thread id and total CPU time (100 ns units), used to count busy threads.</summary>
    public int[] ThreadIds = Array.Empty<int>();
    public long[] ThreadTimes = Array.Empty<long>();
}

public sealed class ProcInfo
{
    public string Path = "", Description = "", CommandLine = "";
}

public sealed class ProcSample
{
    public ProcKey Key;
    public RawProc Raw = null!;
    public ProcInfo Info = null!;
    public double Cpu, Disk, Gpu;
    public long GpuMem;
    /// <summary>Threads that used at least ~0.5% of one core since the last sample.</summary>
    public int BusyThreads;
    /// <summary>GPU % per adapter (LUID key), only for processes using a GPU.</summary>
    public Dictionary<string, double>? GpuByAdapter;
}

public sealed class DiskSample
{
    public string Name = "";
    public double Read, Write, Active;
}

public sealed class GpuAdapterSample
{
    public string Luid = "", Name = "";
    public double Util;
    public long DedicatedUsed, DedicatedTotal;
}

public sealed class SystemSample
{
    public double Cpu, Disk, Gpu;
    public bool GpuAvailable;
    public long GpuMem;
    public ulong PhysTotal, PhysAvail, CommitLimit, CommitAvail;
    public int Threads, Handles;
    /// <summary>Per logical processor: busy % and the kernel-mode part of it.</summary>
    public double[]? CoreBusy, CoreKernel;
    public List<DiskSample> Disks = new();
    public List<GpuAdapterSample> Gpus = new();
}

public sealed class Snapshot
{
    public List<ProcSample> Procs = new();
    public SystemSample Sys = new();
}

/// <summary>Takes one snapshot of every process using a single NtQuerySystemInformation call.</summary>
public sealed unsafe class Sampler : IDisposable
{
    const int SystemProcessInformation = 5, SystemProcessorPerformanceInformation = 8;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    IntPtr _buf = IntPtr.Zero;
    int _bufSize = 1 << 20;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    TimeSpan _lastAt;
    long _lastIdle, _lastKernel, _lastUser;
    Dictionary<ProcKey, (long cpu, long io)> _prev = new();
    Dictionary<int, long> _prevThreads = new();
    long[]? _coreIdle, _coreKernel, _coreUser;
    readonly Dictionary<ProcKey, ProcInfo> _info = new();
    readonly Dictionary<string, string> _descByPath = new(StringComparer.OrdinalIgnoreCase);
    readonly GpuSampler _gpu = new();
    readonly DiskSampler _disk = new();
    readonly int _cpuCount = Environment.ProcessorCount;

    public Snapshot Sample()
    {
        var snap = new Snapshot();
        var now = _clock.Elapsed;
        double dt = (now - _lastAt).TotalSeconds;
        bool havePrev = _lastAt != TimeSpan.Zero && dt > 0;
        _lastAt = now;

        var raws = QueryProcesses();
        var gpu = _gpu.Sample();

        // System CPU from GetSystemTimes (kernel time includes idle time)
        var sys = snap.Sys;
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _lastIdle, dk = kernel - _lastKernel, du = user - _lastUser;
            if (_lastKernel != 0 && dk + du > 0) sys.Cpu = Math.Clamp(100.0 * (dk + du - di) / (dk + du), 0, 100);
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
        }
        SampleCores(sys);
        var mem = Native.MemoryStatus();
        sys.PhysTotal = mem.ullTotalPhys; sys.PhysAvail = mem.ullAvailPhys;
        sys.CommitLimit = mem.ullTotalPageFile; sys.CommitAvail = mem.ullAvailPageFile;
        sys.GpuAvailable = gpu.Available; sys.Gpu = gpu.Total; sys.GpuMem = gpu.TotalMem;
        sys.Gpus = gpu.Adapters;
        sys.Disks = _disk.Sample();

        var next = new Dictionary<ProcKey, (long, long)>(raws.Count);
        var nextThreads = new Dictionary<int, long>(_prevThreads.Count + 256);
        double busyThreshold = dt * 1e7 * 0.005; // 0.5% of one core
        var alive = new HashSet<ProcKey>();
        foreach (var r in raws)
        {
            sys.Threads += r.Threads; sys.Handles += r.Handles;
            if (r.Pid == 0) continue; // System Idle Process
            var key = new ProcKey(r.Pid, r.CreateTime);
            alive.Add(key);
            var s = new ProcSample { Key = key, Raw = r };
            if (havePrev && _prev.TryGetValue(key, out var p))
            {
                s.Cpu = Math.Clamp((r.CpuTime - p.cpu) / (dt * 1e7 * _cpuCount) * 100.0, 0, 100);
                s.Disk = Math.Max(0, (r.IoBytes - p.io) / dt);
            }
            next[key] = (r.CpuTime, r.IoBytes);
            for (int i = 0; i < r.ThreadIds.Length; i++)
            {
                int tid = r.ThreadIds[i]; long t = r.ThreadTimes[i];
                if (havePrev && _prevThreads.TryGetValue(tid, out long pt) && t - pt >= busyThreshold) s.BusyThreads++;
                nextThreads[tid] = t;
            }
            if (gpu.PerPid.TryGetValue(r.Pid, out var g)) { s.Gpu = Math.Min(100, g.util); s.GpuMem = g.mem; }
            gpu.PerPidAdapter.TryGetValue(r.Pid, out s.GpuByAdapter);
            if (!_info.TryGetValue(key, out var info)) _info[key] = info = LoadInfo(r);
            s.Info = info;
            sys.Disk += s.Disk;
            snap.Procs.Add(s);
        }
        _prev = next;
        _prevThreads = nextThreads;
        if (_info.Count > alive.Count + 64)
            foreach (var k in new List<ProcKey>(_info.Keys)) if (!alive.Contains(k)) _info.Remove(k);
        return snap;
    }

    void SampleCores(SystemSample sys)
    {
        // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION is 48 bytes: Idle, Kernel (includes idle), User, Dpc, Interrupt, count.
        int n = Math.Min(_cpuCount, 64);
        int size = 48 * n;
        byte* b = stackalloc byte[size];
        if (Native.NtQuerySystemInformation(SystemProcessorPerformanceInformation, (IntPtr)b, size, out int ret) != 0) return;
        n = ret / 48;
        bool have = _coreIdle != null && _coreIdle.Length == n;
        var idle = new long[n]; var kern = new long[n]; var user = new long[n];
        var busy = new double[n]; var kbusy = new double[n];
        for (int i = 0; i < n; i++)
        {
            byte* e = b + i * 48;
            idle[i] = *(long*)e; kern[i] = *(long*)(e + 8); user[i] = *(long*)(e + 16);
            if (!have) continue;
            long di = idle[i] - _coreIdle![i], dk = kern[i] - _coreKernel![i], du = user[i] - _coreUser![i];
            long total = dk + du;
            if (total <= 0) continue;
            busy[i] = Math.Clamp(100.0 * (total - di) / total, 0, 100);
            kbusy[i] = Math.Clamp(100.0 * (dk - di) / total, 0, busy[i]);
        }
        _coreIdle = idle; _coreKernel = kern; _coreUser = user;
        if (have) { sys.CoreBusy = busy; sys.CoreKernel = kbusy; }
    }

    List<RawProc> QueryProcesses()
    {
        while (true)
        {
            if (_buf == IntPtr.Zero) _buf = Marshal.AllocHGlobal(_bufSize);
            int st = Native.NtQuerySystemInformation(SystemProcessInformation, _buf, _bufSize, out int needed);
            if (st == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero;
                _bufSize = Math.Max(_bufSize * 2, needed + (256 << 10));
                continue;
            }
            Native.ThrowIfNt(st);
            break;
        }

        // SYSTEM_PROCESS_INFORMATION (x64) is 0x100 bytes, followed by SYSTEM_THREAD_INFORMATION[0x50] entries.
        var list = new List<RawProc>(400);
        byte* p = (byte*)_buf;
        while (true)
        {
            uint nextOffset = *(uint*)p;
            int threads = *(int*)(p + 4);
            var r = new RawProc
            {
                Threads = threads,
                CreateTime = *(long*)(p + 32),
                CpuTime = *(long*)(p + 40) + *(long*)(p + 48),
                BasePriority = *(int*)(p + 72),
                Pid = (int)*(long*)(p + 80),
                ParentPid = (int)*(long*)(p + 88),
                Handles = *(int*)(p + 96),
                SessionId = *(int*)(p + 100),
                WorkingSet = *(long*)(p + 144),
                PrivateBytes = *(long*)(p + 184),           // PagefileUsage = private commit
                IoBytes = *(long*)(p + 232) + *(long*)(p + 240), // read + write transfer bytes
            };
            ushort nameLen = *(ushort*)(p + 56);
            IntPtr nameBuf = *(IntPtr*)(p + 64);
            r.Name = nameLen > 0 && nameBuf != IntPtr.Zero ? new string((char*)nameBuf, 0, nameLen / 2)
                   : r.Pid == 0 ? "System Idle Process" : "System";

            // Threads: CPU time per thread, and Suspended = every thread Waiting (5) with WaitReason Suspended (5)
            bool allSuspended = threads > 0;
            r.ThreadIds = new int[threads];
            r.ThreadTimes = new long[threads];
            byte* t = p + 0x100;
            for (int i = 0; i < threads; i++, t += 0x50)
            {
                r.ThreadTimes[i] = *(long*)t + *(long*)(t + 8);
                r.ThreadIds[i] = (int)*(long*)(t + 48); // ClientId.UniqueThread
                if (*(uint*)(t + 68) != 5 || *(uint*)(t + 72) != 5) allSuspended = false;
            }
            r.Suspended = allSuspended && r.Pid > 4;
            list.Add(r);
            if (nextOffset == 0) break;
            p += nextOffset;
        }
        return list;
    }

    ProcInfo LoadInfo(RawProc r)
    {
        var info = new ProcInfo();
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, r.Pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                if (Native.QueryFullProcessImageNameW(h, 0, sb, ref size)) info.Path = sb.ToString(0, size);
                info.CommandLine = ReadCommandLine(h);
            }
            finally { Native.CloseHandle(h); }
        }
        if (info.Path.Length > 0)
        {
            if (!_descByPath.TryGetValue(info.Path, out var desc))
            {
                try { desc = FileVersionInfo.GetVersionInfo(info.Path).FileDescription?.Trim() ?? ""; }
                catch { desc = ""; }
                _descByPath[info.Path] = desc;
            }
            info.Description = desc;
        }
        return info;
    }

    static string ReadCommandLine(IntPtr h)
    {
        const int ProcessCommandLineInformation = 60;
        Native.NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
        if (len <= 16 || len > (1 << 20)) return "";
        IntPtr b = Marshal.AllocHGlobal(len);
        try
        {
            if (Native.NtQueryInformationProcess(h, ProcessCommandLineInformation, b, len, out _) != 0) return "";
            ushort l = *(ushort*)b;
            IntPtr s = *(IntPtr*)((byte*)b + 8);
            return s == IntPtr.Zero ? "" : new string((char*)s, 0, l / 2);
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    public void Dispose()
    {
        _gpu.Dispose();
        _disk.Dispose();
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
    }
}

/// <summary>Small helper for reading wildcard performance counters.</summary>
internal static unsafe class Pdh
{
    public const uint FMT_LARGE = 0x400, FMT_DOUBLE = 0x200, FMT_NOCAP100 = 0x8000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);

    public static IntPtr Open() =>
        Native.PdhOpenQueryW(null, IntPtr.Zero, out IntPtr q) == 0 ? q : IntPtr.Zero;

    public static IntPtr Add(IntPtr q, string path) =>
        q != IntPtr.Zero && Native.PdhAddEnglishCounterW(q, path, IntPtr.Zero, out IntPtr c) == 0 ? c : IntPtr.Zero;

    public static void Read(IntPtr counter, uint fmt, Action<string, double, long> each)
    {
        if (counter == IntPtr.Zero) return;
        uint size = 0;
        int st = Native.PdhGetFormattedCounterArrayW(counter, fmt, ref size, out uint count, IntPtr.Zero);
        if (st != PDH_MORE_DATA || size == 0) return;
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.PdhGetFormattedCounterArrayW(counter, fmt, ref size, out count, buf) != 0) return;
            // PDH_FMT_COUNTERVALUE_ITEM_W: { LPWSTR name; DWORD CStatus; (pad); union { double; long } } = 24 bytes
            for (int i = 0; i < count; i++)
            {
                byte* item = (byte*)buf + i * 24;
                if (*(uint*)(item + 8) > 1) continue; // not valid / new data
                each(new string(*(char**)item), *(double*)(item + 16), *(long*)(item + 16));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>Per-process and per-adapter GPU usage from the GPU performance counters.</summary>
public sealed class GpuSampler : IDisposable
{
    public sealed class Result
    {
        public bool Available;
        public double Total;
        public long TotalMem;
        public Dictionary<int, (double util, long mem)> PerPid = new();
        public Dictionary<int, Dictionary<string, double>> PerPidAdapter = new();
        public List<GpuAdapterSample> Adapters = new();
    }

    IntPtr _q;
    readonly IntPtr _eng, _mem, _adapterMem;
    readonly bool _ok;
    readonly Dictionary<string, (string name, long dedicated)> _names;

    public GpuSampler()
    {
        _names = GpuNames.Load();
        try
        {
            _q = Pdh.Open();
            _eng = Pdh.Add(_q, @"\GPU Engine(*)\Utilization Percentage");
            if (_eng == IntPtr.Zero) return;
            _mem = Pdh.Add(_q, @"\GPU Process Memory(*)\Dedicated Usage");
            _adapterMem = Pdh.Add(_q, @"\GPU Adapter Memory(*)\Dedicated Usage");
            Native.PdhCollectQueryData(_q);
            _ok = true;
        }
        catch { _ok = false; }
    }

    public Result Sample()
    {
        var res = new Result();
        if (!_ok || Native.PdhCollectQueryData(_q) != 0) return res;
        res.Available = true;

        // An engine is "luid_<adapter>_phys_N_eng_N_engtype_X". A GPU's usage is its busiest engine
        // (like Task Manager); a process's usage on a GPU is its busiest engine there.
        var perEngine = new Dictionary<string, double>();
        var adapterOf = new Dictionary<string, string>();
        var perPidEngine = new Dictionary<(int, string), double>();
        Pdh.Read(_eng, Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100, (name, d, _) =>
        {
            int pid = ParsePid(name);
            string? luid = ParseLuid(name);
            if (luid == null) return;
            int at = name.IndexOf("luid_", StringComparison.Ordinal);
            string eng = name[(at + 5)..];
            int hash = eng.IndexOf('#'); if (hash >= 0) eng = eng[..hash];
            perEngine[eng] = perEngine.GetValueOrDefault(eng) + d;
            adapterOf[eng] = luid;
            if (pid > 0) perPidEngine[(pid, eng)] = perPidEngine.GetValueOrDefault((pid, eng)) + d;
        });

        var adapters = new Dictionary<string, GpuAdapterSample>();
        GpuAdapterSample Adapter(string luid)
        {
            if (!adapters.TryGetValue(luid, out var a))
            {
                var known = _names.TryGetValue(luid, out var n);
                adapters[luid] = a = new GpuAdapterSample
                {
                    Luid = luid,
                    Name = known ? n.name : "GPU " + luid,
                    DedicatedTotal = known ? n.dedicated : 0,
                };
            }
            return a;
        }

        foreach (var (eng, v) in perEngine)
        {
            var a = Adapter(adapterOf[eng]);
            a.Util = Math.Min(100, Math.Max(a.Util, v));
            res.Total = Math.Min(100, Math.Max(res.Total, v));
        }
        foreach (var ((pid, eng), v) in perPidEngine)
        {
            var cur = res.PerPid.GetValueOrDefault(pid);
            res.PerPid[pid] = (Math.Max(cur.util, v), cur.mem);
            if (v <= 0) continue;
            if (!res.PerPidAdapter.TryGetValue(pid, out var byAdapter)) res.PerPidAdapter[pid] = byAdapter = new();
            string luid = adapterOf[eng];
            byAdapter[luid] = Math.Min(100, Math.Max(byAdapter.GetValueOrDefault(luid), v));
        }
        Pdh.Read(_mem, Pdh.FMT_LARGE, (name, _, l) =>
        {
            int pid = ParsePid(name);
            res.TotalMem += l;
            if (pid > 0)
            {
                var cur = res.PerPid.GetValueOrDefault(pid);
                res.PerPid[pid] = (cur.util, cur.mem + l);
            }
        });
        Pdh.Read(_adapterMem, Pdh.FMT_LARGE, (name, _, l) =>
        {
            if (ParseLuid(name) is { } luid) Adapter(luid).DedicatedUsed += l;
        });

        // Show real GPUs only (those DirectX reports), unless DirectX told us nothing.
        foreach (var a in adapters.Values)
            if (_names.Count == 0 || _names.ContainsKey(a.Luid)) res.Adapters.Add(a);
        res.Adapters.Sort((x, y) => string.CompareOrdinal(x.Luid, y.Luid));
        return res;
    }

    static int ParsePid(string name)
    {
        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return 0;
        int end = name.IndexOf('_', 4);
        return end > 4 && int.TryParse(name.AsSpan(4, end - 4), out int pid) ? pid : 0;
    }

    /// <summary>"..._luid_0x00000000_0x0000D1B5_phys_0..." → "0x00000000_0x0000D1B5"</summary>
    static string? ParseLuid(string name)
    {
        int at = name.IndexOf("luid_", StringComparison.Ordinal);
        if (at < 0 || name.Length < at + 5 + 21) return null;
        return name.Substring(at + 5, 21).ToUpperInvariant().Replace("0X", "0x");
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}

/// <summary>GPU names and memory sizes from DXGI, keyed by the same LUID text the counters use.</summary>
internal static unsafe class GpuNames
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    public static Dictionary<string, (string name, long dedicated)> Load()
    {
        var map = new Dictionary<string, (string, long)>();
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(in iid, out IntPtr factory) != 0 || factory == IntPtr.Zero) return map;
            var fv = *(IntPtr**)factory;
            var enumAdapters1 = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)fv[12];
            for (uint i = 0; ; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) != 0) break;
                var av = *(IntPtr**)adapter;
                var getDesc1 = (delegate* unmanaged<IntPtr, byte*, int>)av[10];
                byte* d = stackalloc byte[320];
                if (getDesc1(adapter, d) == 0)
                {
                    uint flags = *(uint*)(d + 304);
                    if ((flags & 2) == 0) // skip software adapters (Microsoft Basic Render Driver)
                    {
                        string name = new string((char*)d).Trim();
                        long dedicated = (long)*(ulong*)(d + 272);
                        uint low = *(uint*)(d + 296); int high = *(int*)(d + 300);
                        map[$"0x{high:X8}_0x{low:X8}"] = (name, dedicated);
                    }
                }
                ((delegate* unmanaged<IntPtr, uint>)av[2])(adapter); // Release
            }
            ((delegate* unmanaged<IntPtr, uint>)fv[2])(factory);
        }
        catch { /* no DXGI: fall back to counter names */ }
        return map;
    }
}

/// <summary>Per physical disk throughput and active time from the PhysicalDisk counters.</summary>
public sealed class DiskSampler : IDisposable
{
    IntPtr _q;
    readonly IntPtr _read, _write, _idle;

    public DiskSampler()
    {
        try
        {
            _q = Pdh.Open();
            _read = Pdh.Add(_q, @"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _write = Pdh.Add(_q, @"\PhysicalDisk(*)\Disk Write Bytes/sec");
            _idle = Pdh.Add(_q, @"\PhysicalDisk(*)\% Idle Time");
            if (_q != IntPtr.Zero) Native.PdhCollectQueryData(_q);
        }
        catch { _q = IntPtr.Zero; }
    }

    public List<DiskSample> Sample()
    {
        var list = new List<DiskSample>();
        if (_q == IntPtr.Zero || Native.PdhCollectQueryData(_q) != 0) return list;
        var byName = new Dictionary<string, DiskSample>();
        DiskSample Get(string inst)
        {
            if (!byName.TryGetValue(inst, out var d))
            {
                // "0 C:" → "Disk 0 (C:)"; "2" → "Disk 2"
                var parts = inst.Split(' ', 2);
                string name = "Disk " + parts[0] + (parts.Length > 1 ? $" ({parts[1].Trim()})" : "");
                byName[inst] = d = new DiskSample { Name = name, Active = 0 };
            }
            return d;
        }
        Pdh.Read(_read, Pdh.FMT_DOUBLE, (n, v, _) => { if (n != "_Total") Get(n).Read = v; });
        Pdh.Read(_write, Pdh.FMT_DOUBLE, (n, v, _) => { if (n != "_Total") Get(n).Write = v; });
        Pdh.Read(_idle, Pdh.FMT_DOUBLE | Pdh.FMT_NOCAP100, (n, v, _) => { if (n != "_Total") Get(n).Active = Math.Clamp(100 - v, 0, 100); });
        list.AddRange(byName.Values);
        static int Num(DiskSample d) => int.TryParse(d.Name.AsSpan(5).ToString().Split(' ')[0], out int n) ? n : 999;
        list.Sort((a, b) => Num(a).CompareTo(Num(b)));
        return list;
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}

/// <summary>Which logical processors belong to which physical core, and whether a core is a P- or E-core.</summary>
public sealed unsafe class CpuTopology
{
    public int[] CoreOfLogical = Array.Empty<int>();
    public List<int[]> CoreThreads = new();
    public List<bool> CoreIsEfficiency = new();
    public bool Hybrid;

    public static CpuTopology Load(int logicalCount)
    {
        var t = new CpuTopology { CoreOfLogical = new int[logicalCount] };
        for (int i = 0; i < logicalCount; i++) t.CoreOfLogical[i] = -1;
        var effs = new List<byte>();
        try
        {
            uint len = 0;
            Native.GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len); // RelationProcessorCore
            if (len > 0)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)len);
                try
                {
                    if (Native.GetLogicalProcessorInformationEx(0, buf, ref len))
                    {
                        byte* p = (byte*)buf, end = p + len;
                        while (p < end)
                        {
                            int size = *(int*)(p + 4);
                            byte eff = *(p + 9);
                            ushort groups = *(ushort*)(p + 30);
                            var threads = new List<int>();
                            for (int g = 0; g < groups; g++)
                            {
                                ulong mask = *(ulong*)(p + 32 + g * 16);
                                ushort group = *(ushort*)(p + 40 + g * 16);
                                if (group != 0) continue;
                                for (int bit = 0; bit < 64; bit++)
                                    if ((mask & (1UL << bit)) != 0 && bit < logicalCount) threads.Add(bit);
                            }
                            if (threads.Count > 0)
                            {
                                foreach (int l in threads) t.CoreOfLogical[l] = t.CoreThreads.Count;
                                t.CoreThreads.Add(threads.ToArray());
                                effs.Add(eff);
                            }
                            if (size <= 0) break;
                            p += size;
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch { }

        if (t.CoreThreads.Count == 0) // fall back: one "core" per logical processor
            for (int i = 0; i < logicalCount; i++) { t.CoreOfLogical[i] = i; t.CoreThreads.Add(new[] { i }); effs.Add(0); }

        byte max = 0, min = 255;
        foreach (var e in effs) { max = Math.Max(max, e); min = Math.Min(min, e); }
        t.Hybrid = max != min;
        foreach (var e in effs) t.CoreIsEfficiency.Add(t.Hybrid && e < max);
        return t;
    }
}
