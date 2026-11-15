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
}

public sealed class SystemSample
{
    public double Cpu, Disk, Gpu;
    public bool GpuAvailable;
    public long GpuMem;
    public ulong PhysTotal, PhysAvail, CommitLimit, CommitAvail;
    public int Threads, Handles;
}

public sealed class Snapshot
{
    public List<ProcSample> Procs = new();
    public SystemSample Sys = new();
}

/// <summary>Takes one snapshot of every process using a single NtQuerySystemInformation call.</summary>
public sealed unsafe class Sampler : IDisposable
{
    const int SystemProcessInformation = 5;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    IntPtr _buf = IntPtr.Zero;
    int _bufSize = 1 << 20;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    TimeSpan _lastAt;
    long _lastIdle, _lastKernel, _lastUser;
    Dictionary<ProcKey, (long cpu, long io)> _prev = new();
    readonly Dictionary<ProcKey, ProcInfo> _info = new();
    readonly Dictionary<string, string> _descByPath = new(StringComparer.OrdinalIgnoreCase);
    readonly GpuSampler _gpu = new();
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
        var mem = Native.MemoryStatus();
        sys.PhysTotal = mem.ullTotalPhys; sys.PhysAvail = mem.ullAvailPhys;
        sys.CommitLimit = mem.ullTotalPageFile; sys.CommitAvail = mem.ullAvailPageFile;
        sys.GpuAvailable = gpu.Available; sys.Gpu = gpu.Total; sys.GpuMem = gpu.TotalMem;

        var next = new Dictionary<ProcKey, (long, long)>(raws.Count);
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
            if (gpu.PerPid.TryGetValue(r.Pid, out var g)) { s.Gpu = Math.Min(100, g.util); s.GpuMem = g.mem; }
            if (!_info.TryGetValue(key, out var info)) _info[key] = info = LoadInfo(r);
            s.Info = info;
            sys.Disk += s.Disk;
            snap.Procs.Add(s);
        }
        _prev = next;
        if (_info.Count > alive.Count + 64)
            foreach (var k in new List<ProcKey>(_info.Keys)) if (!alive.Contains(k)) _info.Remove(k);
        return snap;
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

            // Suspended = every thread is Waiting (5) with WaitReason Suspended (5)
            bool allSuspended = threads > 0;
            byte* t = p + 0x100;
            for (int i = 0; i < threads; i++, t += 0x50)
            {
                if (*(uint*)(t + 68) != 5 || *(uint*)(t + 72) != 5) { allSuspended = false; break; }
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
        if (_buf != IntPtr.Zero) { Marshal.FreeHGlobal(_buf); _buf = IntPtr.Zero; }
    }
}

/// <summary>Per-process GPU usage from the "GPU Engine" and "GPU Process Memory" performance counters.</summary>
public sealed unsafe class GpuSampler : IDisposable
{
    public sealed class Result
    {
        public bool Available;
        public double Total;
        public long TotalMem;
        public Dictionary<int, (double util, long mem)> PerPid = new();
    }

    const uint PDH_FMT_LARGE = 0x400, PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
    const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    IntPtr _q, _eng, _mem;
    readonly bool _ok;

    public GpuSampler()
    {
        try
        {
            if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _q) != 0) return;
            if (Native.PdhAddEnglishCounterW(_q, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _eng) != 0) return;
            Native.PdhAddEnglishCounterW(_q, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out _mem);
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

        // Sum per (pid, engine), then a process's GPU% is its busiest engine (like Task Manager).
        var perEngine = new Dictionary<string, double>();
        var perPidEngine = new Dictionary<(int, string), double>();
        Read(_eng, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, (name, d, _) =>
        {
            int pid = ParsePid(name);
            int luid = name.IndexOf("_luid_", StringComparison.Ordinal);
            string eng = luid >= 0 ? name[(luid + 6)..] : name;
            int hash = eng.IndexOf('#'); if (hash >= 0) eng = eng[..hash];
            perEngine[eng] = perEngine.GetValueOrDefault(eng) + d;
            if (pid > 0) perPidEngine[(pid, eng)] = perPidEngine.GetValueOrDefault((pid, eng)) + d;
        });
        foreach (var v in perEngine.Values) res.Total = Math.Max(res.Total, v);
        res.Total = Math.Min(100, res.Total);
        foreach (var ((pid, _), v) in perPidEngine)
        {
            var cur = res.PerPid.GetValueOrDefault(pid);
            res.PerPid[pid] = (Math.Max(cur.util, v), cur.mem);
        }
        if (_mem != IntPtr.Zero)
            Read(_mem, PDH_FMT_LARGE, (name, _, l) =>
            {
                int pid = ParsePid(name);
                res.TotalMem += l;
                if (pid > 0)
                {
                    var cur = res.PerPid.GetValueOrDefault(pid);
                    res.PerPid[pid] = (cur.util, cur.mem + l);
                }
            });
        return res;
    }

    static int ParsePid(string name)
    {
        // "pid_1234_luid_0x..._phys_0_eng_0_engtype_3D"
        if (!name.StartsWith("pid_", StringComparison.Ordinal)) return 0;
        int end = name.IndexOf('_', 4);
        return end > 4 && int.TryParse(name.AsSpan(4, end - 4), out int pid) ? pid : 0;
    }

    static void Read(IntPtr counter, uint fmt, Action<string, double, long> each)
    {
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
                uint status = *(uint*)(item + 8);
                if (status > 1) continue;
                string name = new string(*(char**)item);
                each(name, *(double*)(item + 16), *(long*)(item + 16));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public void Dispose()
    {
        if (_q != IntPtr.Zero) { Native.PdhCloseQuery(_q); _q = IntPtr.Zero; }
    }
}
