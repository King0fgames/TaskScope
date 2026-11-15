using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

    /// <summary>Reads which logical processors a process may run on. Needs only limited query access.</summary>
    public static Affinity GetAffinity(ProcRow p)
    {
        IntPtr h = Native.OpenChecked(p.Key, 0);
        try { return Affinity.Read(h); }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Limits a process to the logical processors in <paramref name="mask"/> (bits within its processor group).</summary>
    public static void SetAffinity(ProcRow p, ulong mask) => With(p, Native.PROCESS_SET_INFORMATION, h =>
    {
        if (mask == 0) throw new ArgumentException("pick at least one processor");
        if (Native.SetProcessAffinityMask(h, (UIntPtr)mask)) return;
        int err = Marshal.GetLastWin32Error();
        if (err == 87) // ERROR_INVALID_PARAMETER
            throw new InvalidOperationException(Affinity.Read(h).MultiGroup
                ? "it runs on more than one processor group, and Windows only lets a process be limited within one group"
                : "Windows rejected that set of processors");
        throw new System.ComponentModel.Win32Exception(err);
    });

    static void With(ProcRow p, uint access, Action<IntPtr> act)
    {
        IntPtr h = Native.OpenChecked(p.Key, access);
        try { act(h); }
        finally { Native.CloseHandle(h); }
    }
}

/// <summary>
/// A process's CPU affinity. Windows splits machines with more than 64 logical processors into
/// processor groups; a process's affinity mask is a set of bits within one group.
/// </summary>
public sealed record Affinity(ushort Group, ulong Mask, ulong GroupMask, bool MultiGroup)
{
    /// <summary>True when the process may not use every processor in its group.</summary>
    public bool Restricted => Mask != GroupMask;

    /// <summary>The system-wide number of this group's first logical processor (CPU 64 is bit 0 of group 1).</summary>
    public int Offset => GroupOffset(Group);

    public static int GroupCount => Math.Max(1, (int)Native.GetActiveProcessorGroupCount());

    public static int GroupOffset(ushort group)
    {
        int n = 0;
        for (ushort g = 0; g < group; g++) n += (int)Native.GetActiveProcessorCount(g);
        return n;
    }

    public static ulong FullMask(ushort group)
    {
        uint n = Native.GetActiveProcessorCount(group);
        return n >= 64 ? ulong.MaxValue : (1UL << (int)n) - 1;
    }

    internal static Affinity Read(IntPtr h)
    {
        ushort count = 4;
        var groups = new ushort[count];
        if (!Native.GetProcessGroupAffinity(h, ref count, groups))
        {
            if (Marshal.GetLastWin32Error() == 122 && count > 0) // ERROR_INSUFFICIENT_BUFFER
            {
                groups = new ushort[count];
                if (!Native.GetProcessGroupAffinity(h, ref count, groups)) count = 0;
            }
            else count = 0;
        }
        ushort group = count > 0 ? groups[0] : (ushort)0;

        if (!Native.GetProcessAffinityMask(h, out var pm, out var sm))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ulong full = (ulong)sm != 0 ? (ulong)sm : FullMask(group);
        // A process with threads in several groups reports zero masks: it may run anywhere.
        ulong mask = (ulong)pm != 0 ? (ulong)pm : full;
        return new Affinity(group, mask, full, count > 1 || (ulong)pm == 0);
    }

    /// <summary>"0–3, 8, 10–11" style list of the set bits, numbered system-wide.</summary>
    public static string Ranges(ulong mask, int offset = 0)
    {
        var parts = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) == 0) continue;
            int j = i;
            while (j + 1 < 64 && (mask & (1UL << (j + 1))) != 0) j++;
            parts.Add(i == j ? $"{i + offset}" : $"{i + offset}–{j + offset}");
            i = j;
        }
        return string.Join(", ", parts);
    }

    public static int Count(ulong mask) => System.Numerics.BitOperations.PopCount(mask);
}
