using System;
using System.Collections.Generic;

namespace TaskScope;

public static class Actions
{
    /// <summary>Processes whose loss or pause can crash or freeze Windows.</summary>
    public static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Secure System", "Memory Compression", "smss.exe", "csrss.exe", "wininit.exe",
        "winlogon.exe", "services.exe", "lsass.exe", "LsaIso.exe", "dwm.exe", "svchost.exe", "fontdrvhost.exe",
    };

    public static void Kill(ProcRow p) => With(p, Native.PROCESS_TERMINATE, h =>
    {
        if (!Native.TerminateProcess(h, 1)) throw new System.ComponentModel.Win32Exception();
    });

    public static void Suspend(ProcRow p)
    {
        if (p.Pid == Environment.ProcessId) throw new InvalidOperationException("TaskScope can't pause itself");
        With(p, Native.PROCESS_SUSPEND_RESUME, h => Native.ThrowIfNt(Native.NtSuspendProcess(h)));
    }

    public static void Resume(ProcRow p) =>
        With(p, Native.PROCESS_SUSPEND_RESUME, h => Native.ThrowIfNt(Native.NtResumeProcess(h)));

    public static void SetPriority(ProcRow p, uint priorityClass) => With(p, Native.PROCESS_SET_INFORMATION, h =>
    {
        if (!Native.SetPriorityClass(h, priorityClass)) throw new System.ComponentModel.Win32Exception();
    });

    static void With(ProcRow p, uint access, Action<IntPtr> act)
    {
        IntPtr h = Native.OpenChecked(p.Key, access);
        try { act(h); }
        finally { Native.CloseHandle(h); }
    }
}
