using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskScope;

internal static unsafe class Native
{
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);
    [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr h);
    [DllImport("ntdll.dll")] public static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetPriorityClass(IntPtr h, uint cls);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static MEMORYSTATUSEX MemoryStatus()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)sizeof(MEMORYSTATUSEX) };
        GlobalMemoryStatusEx(ref m);
        return m;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO sfi, uint size, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

    // PDH (performance counters) for GPU
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhOpenQueryW(string? src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern int PdhAddEnglishCounterW(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] public static extern int PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] public static extern int PdhCloseQuery(IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    public static extern int PdhGetFormattedCounterArrayW(IntPtr c, uint fmt, ref uint bufSize, out uint count, IntPtr buf);

    /// <summary>Opens a process and checks it is still the same instance (guards against PID reuse).</summary>
    public static IntPtr OpenChecked(ProcKey key, uint access)
    {
        IntPtr h = OpenProcess(access | PROCESS_QUERY_LIMITED_INFORMATION, false, key.Pid);
        if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (key.Create != 0 && GetProcessTimes(h, out long create, out _, out _, out _) && create != key.Create)
        {
            CloseHandle(h);
            throw new InvalidOperationException("the process has already exited");
        }
        return h;
    }

    public static void ThrowIfNt(int status)
    {
        if (status < 0) throw new Win32Exception(RtlNtStatusToDosError(status));
    }
}
