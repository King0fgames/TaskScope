using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace TaskScope;

public sealed class RingBuffer
{
    readonly double[] _a;
    int _start, _count;
    public RingBuffer(int capacity) => _a = new double[capacity];
    public int Count => _count;
    public int Capacity => _a.Length;
    public double this[int i] => _a[(_start + i) % _a.Length];
    public double Last => _count == 0 ? 0 : this[_count - 1];
    public void Add(double v)
    {
        if (_count < _a.Length) _a[(_start + _count++) % _a.Length] = v;
        else { _a[_start] = v; _start = (_start + 1) % _a.Length; }
    }
    public double Max()
    {
        double m = 0;
        for (int i = 0; i < _count; i++) m = Math.Max(m, this[i]);
        return m;
    }
}

/// <summary>A row in the process list: either one process or an app group.</summary>
public abstract class Row : INotifyPropertyChanged
{
    public static double MemScale = 16.0 * (1L << 30);
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; Changed(name!); return true;
    }

    public string ImageName { get; protected init; } = "";
    public abstract bool IsGroup { get; }
    public abstract long PidSort { get; }
    public abstract string Tip { get; }

    string _name = ""; public string Name { get => _name; set => Set(ref _name, value); }
    ImageSource? _icon; public ImageSource? Icon { get => _icon; set => Set(ref _icon, value); }
    double _indent;
    public double Indent { get => _indent; set { if (Set(ref _indent, value)) Changed(nameof(IndentMargin)); } }
    public Thickness IndentMargin => new(_indent, 0, 0, 0);
    string _count = ""; public string CountText { get => _count; protected set => Set(ref _count, value); }
    string _glyph = ""; public string ExpandGlyph { get => _glyph; protected set => Set(ref _glyph, value); }
    public Visibility ExpanderVisibility => IsGroup ? Visibility.Visible : Visibility.Hidden;
    public virtual string PidText => "";

    // Raw values (sorting uses these)
    public double Cpu, Disk, Gpu;
    public long Private, WorkingSet, GpuMem;
    public int Threads, Handles;
    public bool Suspended;
    public readonly RingBuffer CpuHist = new(120), MemHist = new(120);

    // Display values (bound in the grid)
    string _cpuT = "", _privT = "", _wsT = "", _diskT = "", _gpuT = "", _gpuMemT = "", _thrT = "", _hndT = "", _status = "";
    Brush? _cpuH, _memH, _diskH, _gpuH;
    public string CpuText { get => _cpuT; private set => Set(ref _cpuT, value); }
    public string PrivateText { get => _privT; private set => Set(ref _privT, value); }
    public string WorkingSetText { get => _wsT; private set => Set(ref _wsT, value); }
    public string DiskText { get => _diskT; private set => Set(ref _diskT, value); }
    public string GpuText { get => _gpuT; private set => Set(ref _gpuT, value); }
    public string GpuMemText { get => _gpuMemT; private set => Set(ref _gpuMemT, value); }
    public string ThreadsText { get => _thrT; private set => Set(ref _thrT, value); }
    public string HandlesText { get => _hndT; private set => Set(ref _hndT, value); }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public Brush? CpuHeat { get => _cpuH; private set => Set(ref _cpuH, value); }
    public Brush? MemHeat { get => _memH; private set => Set(ref _memH, value); }
    public Brush? DiskHeat { get => _diskH; private set => Set(ref _diskH, value); }
    public Brush? GpuHeat { get => _gpuH; private set => Set(ref _gpuH, value); }

    protected void Publish()
    {
        CpuText = Fmt.Pct(Cpu);
        PrivateText = Fmt.Bytes(Private);
        WorkingSetText = Fmt.Bytes(WorkingSet);
        DiskText = Fmt.Rate(Disk);
        GpuText = Fmt.Pct(Gpu);
        GpuMemText = GpuMem > 0 ? Fmt.Bytes(GpuMem) : "";
        ThreadsText = Threads.ToString("N0");
        HandlesText = Handles.ToString("N0");
        StatusText = Suspended ? "Suspended" : "";
        CpuHeat = Heat.For(Cpu / 50.0);
        MemHeat = Heat.For(Private / MemScale);
        DiskHeat = Heat.For(Disk / (50.0 * 1048576));
        GpuHeat = Heat.For(Gpu / 100.0);
        CpuHist.Add(Cpu);
        MemHist.Add(Private);
    }
}

public sealed class ProcRow : Row
{
    public ProcKey Key { get; }
    public int Pid => Key.Pid;
    public int ParentPid { get; private set; }
    public int BasePriority { get; private set; }
    public string Path { get; }
    public string CommandLine { get; }
    public string Description { get; }
    public DateTime? Started { get; }
    public GroupRow? Group;
    public long LastTick;

    public ProcRow(ProcSample s)
    {
        Key = s.Key;
        ImageName = s.Raw.Name;
        Path = s.Info.Path;
        CommandLine = s.Info.CommandLine;
        Description = s.Info.Description;
        Name = string.IsNullOrWhiteSpace(Description) ? ImageName : Description;
        if (s.Raw.CreateTime > 0) Started = DateTime.FromFileTime(s.Raw.CreateTime);
    }

    public override bool IsGroup => false;
    public override long PidSort => Pid;
    public override string PidText => Pid.ToString();
    public override string Tip => CommandLine.Length > 0 ? CommandLine : Path.Length > 0 ? Path : ImageName;

    public void Update(ProcSample s, long tick)
    {
        LastTick = tick;
        var r = s.Raw;
        Cpu = s.Cpu; Disk = s.Disk; Gpu = s.Gpu; GpuMem = s.GpuMem;
        Private = r.PrivateBytes; WorkingSet = r.WorkingSet;
        Threads = r.Threads; Handles = r.Handles; Suspended = r.Suspended;
        ParentPid = r.ParentPid; BasePriority = r.BasePriority;
        Publish();
    }
}

public sealed class GroupRow : Row
{
    public List<ProcRow> Children { get; } = new();
    long _minPid;
    bool _expanded;

    public GroupRow(string imageName)
    {
        ImageName = imageName;
        Name = imageName;
        ExpandGlyph = "";
    }

    public bool IsExpanded
    {
        get => _expanded;
        set { if (Set(ref _expanded, value)) ExpandGlyph = value ? "" : ""; }
    }

    public override bool IsGroup => true;
    public override long PidSort => _minPid;
    public override string Tip => $"{Children.Count} {ImageName} processes" +
        (Children.Count > 0 && Children[0].Path.Length > 0 ? "\n" + Children[0].Path : "");

    public void Aggregate()
    {
        Cpu = Disk = Gpu = 0; Private = WorkingSet = GpuMem = 0; Threads = Handles = 0;
        _minPid = long.MaxValue;
        bool allSuspended = Children.Count > 0;
        string? desc = null;
        foreach (var c in Children)
        {
            Cpu += c.Cpu; Disk += c.Disk; Gpu += c.Gpu;
            Private += c.Private; WorkingSet += c.WorkingSet; GpuMem += c.GpuMem;
            Threads += c.Threads; Handles += c.Handles;
            allSuspended &= c.Suspended;
            _minPid = Math.Min(_minPid, c.Pid);
            if (desc == null && !string.IsNullOrWhiteSpace(c.Description)) desc = c.Description;
            Icon ??= c.Icon;
        }
        Cpu = Math.Min(100, Cpu); Gpu = Math.Min(100, Gpu);
        Suspended = allSuspended;
        Name = desc ?? ImageName;
        CountText = $"({Children.Count})";
        Publish();
    }
}

public static class Fmt
{
    const double MB = 1048576, GB = 1073741824;
    public static string Pct(double p) => p < 0.05 ? "0%" : p < 10 ? $"{p:0.0}%" : $"{p:0}%";
    public static string Bytes(double b)
    {
        if (b <= 0) return "0 MB";
        double mb = b / MB;
        if (mb < 1000) return mb < 10 ? $"{mb:0.0} MB" : $"{mb:0} MB";
        double gb = b / GB;
        return gb < 10 ? $"{gb:0.00} GB" : $"{gb:0.0} GB";
    }
    public static string Rate(double bps)
    {
        double mb = bps / MB;
        return mb < 0.05 ? "0 MB/s" : mb < 10 ? $"{mb:0.0} MB/s" : $"{mb:0} MB/s";
    }
    public static string Gb(double bytes) => $"{bytes / GB:0.0} GB";
    public static string Priority(int basePriority) => basePriority switch
    {
        4 => "Low", 6 => "Below normal", 8 => "Normal", 10 => "Above normal",
        13 => "High", 24 => "Realtime", _ => basePriority.ToString()
    };
}

public static class Heat
{
    static readonly Brush[] Levels = Enumerable.Range(0, 11).Select(i =>
    {
        var b = new SolidColorBrush(i == 0 ? Colors.Transparent : Color.FromArgb((byte)(16 + i * 12), 0xF5, 0xA6, 0x23));
        b.Freeze();
        return (Brush)b;
    }).ToArray();

    public static Brush For(double fraction)
    {
        if (double.IsNaN(fraction) || fraction < 0.04) return Levels[0];
        return Levels[Math.Clamp((int)Math.Ceiling(fraction * 10), 1, 10)];
    }
}
