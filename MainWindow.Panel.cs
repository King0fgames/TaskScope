using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TaskScope;

/// <summary>The detail panel under the tiles: CPU cores/threads, per-disk, per-GPU engines and memory, and stacked app graphs.</summary>
public partial class MainWindow
{
    public static readonly DependencyProperty CellColumnsProperty =
        DependencyProperty.Register(nameof(CellColumns), typeof(int), typeof(MainWindow), new PropertyMetadata(4));
    public int CellColumns { get => (int)GetValue(CellColumnsProperty); set => SetValue(CellColumnsProperty, value); }

    // Per-app stacked histories behind the five tiles (values match each tile's scale).
    readonly StackedHistory _cpuStack = new(60), _memStack = new(60), _commitStack = new(60), _gpuStack = new(60), _diskStack = new(60);

    readonly CpuTopology _topology = CpuTopology.Load(Math.Min(Environment.ProcessorCount, 64));
    readonly ObservableCollection<ChartCell> _threadCells = new(), _coreCells = new(), _diskCells = new(), _gpuCells = new(),
        _gpuEngineCells = new(), _gpuMemCells = new();
    /// <summary>Engines (adapter|type) that have been at least 0.5% busy, so they get a graph.</summary>
    readonly HashSet<string> _gpuEnginesUsed = new();
    ulong _selAffinity, _sysAffinity;

    static readonly Brush CpuBlue = Frozen("#3B8EEA"), DiskTeal = Frozen("#2BB5C9"), GpuGreen = Frozen("#3FB37F"), GpuMemGreen = Frozen("#7CC47F");
    static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    void InitPanel()
    {
        StackToggle.IsChecked = _settings.StackedGraphs;
        CpuSpark.Stack = _cpuStack; MemSpark.Stack = _memStack; CommitSpark.Stack = _commitStack;
        GpuSpark.Stack = _gpuStack; DiskSpark.Stack = _diskStack;
        ApplyStackedToTiles();
        ShowPanel(_settings.OpenPanel);
    }

    // ───────────────────────── recording ─────────────────────────

    void RecordHistories(SystemSample s)
    {
        double physTotal = Math.Max(1, s.PhysTotal), commitLimit = Math.Max(1, s.CommitLimit);
        var cpu = new Dictionary<string, double>(); var mem = new Dictionary<string, double>();
        var commit = new Dictionary<string, double>(); var gpu = new Dictionary<string, double>();
        var disk = new Dictionary<string, double>();
        foreach (var g in _groups.Values)
        {
            string k = g.ImageName;
            foreach (var st in new[] { _cpuStack, _memStack, _commitStack, _gpuStack, _diskStack }) st.Labels[k] = g.Name;
            if (g.Cpu > 0) cpu[k] = g.Cpu;
            if (g.WorkingSet > 0) mem[k] = g.WorkingSet * 100.0 / physTotal;
            if (g.Private > 0) commit[k] = g.Private * 100.0 / commitLimit;
            if (g.Gpu > 0) gpu[k] = g.Gpu;
            if (g.Disk > 0) disk[k] = g.Disk;
        }
        _cpuStack.Add(s.Cpu, cpu);
        _memStack.Add((s.PhysTotal - s.PhysAvail) * 100.0 / physTotal, mem);
        _commitStack.Add((s.CommitLimit - s.CommitAvail) * 100.0 / commitLimit, commit);
        _gpuStack.Add(s.Gpu, gpu);
        _diskStack.Add(s.Disk, disk);

        RecordCpuCells(s);
        RecordDiskCells(s);
        RecordGpuCells(s);
        RecordGpuEngineCells(s);
        RecordGpuMemCells(s);
    }

    void RecordCpuCells(SystemSample s)
    {
        if (s.CoreBusy is not { } busy || s.CoreKernel is not { } kern) return;
        int n = busy.Length;
        if (_threadCells.Count != n)
        {
            _threadCells.Clear();
            bool smt = _topology.CoreThreads.Any(t => t.Length > 1);
            for (int i = 0; i < n; i++)
            {
                int core = i < _topology.CoreOfLogical.Length ? _topology.CoreOfLogical[i] : -1;
                string kind = core >= 0 && _topology.Hybrid ? (_topology.CoreIsEfficiency[core] ? " (E-core)" : " (P-core)") : "";
                _threadCells.Add(new ChartCell($"t{i}", new StackedHistory(60, "User", "Kernel"))
                {
                    FixedMax = 100, Stroke = CpuBlue,
                    // "C1" names the physical core, so SMT siblings (two logical CPUs on one core) share it.
                    Label = $"CPU {i}" + (core >= 0 && smt ? $" · C{core}" : "") +
                            (core >= 0 && _topology.Hybrid ? (_topology.CoreIsEfficiency[core] ? " · E" : " · P") : ""),
                    Tip = $"Logical processor {i}" + (core >= 0 ? $" · core {core}{kind}" : "") +
                          (core >= 0 && _topology.CoreThreads[core].Length > 1
                              ? $"\nShares this core with CPU {string.Join(", ", _topology.CoreThreads[core].Where(t => t != i))} (hyper-threading)"
                              : ""),
                });
            }
            _coreCells.Clear();
            for (int c = 0; c < _topology.CoreThreads.Count; c++)
            {
                var threads = _topology.CoreThreads[c];
                string kind = _topology.Hybrid ? (_topology.CoreIsEfficiency[c] ? " · E" : " · P") : "";
                _coreCells.Add(new ChartCell($"c{c}", new StackedHistory(60, "User", "Kernel"))
                {
                    FixedMax = 100, Stroke = CpuBlue, Label = $"Core {c}{kind}",
                    Tip = $"Core {c}{(_topology.Hybrid ? (_topology.CoreIsEfficiency[c] ? " (efficiency core)" : " (performance core)") : "")}" +
                          $" · logical processors {string.Join(", ", threads)}",
                });
            }
        }
        for (int i = 0; i < n; i++)
        {
            var cell = _threadCells[i];
            cell.Data.Add(busy[i]);
            cell.Stack.Add(busy[i], new() { ["User"] = busy[i] - kern[i], ["Kernel"] = kern[i] });
            cell.ValueText = Fmt.Pct(busy[i]);
        }
        for (int c = 0; c < _coreCells.Count; c++)
        {
            var threads = _topology.CoreThreads[c].Where(t => t < n).ToArray();
            if (threads.Length == 0) continue;
            double b = threads.Average(t => busy[t]), k = threads.Average(t => kern[t]);
            var cell = _coreCells[c];
            cell.Data.Add(b);
            cell.Stack.Add(b, new() { ["User"] = b - k, ["Kernel"] = k });
            cell.ValueText = Fmt.Pct(b);
        }
    }

    void RecordDiskCells(SystemSample s)
    {
        foreach (var c in _diskCells) c.Seen = false;
        foreach (var d in s.Disks)
        {
            var cell = _diskCells.FirstOrDefault(c => c.Key == d.Name);
            if (cell == null)
            {
                cell = new ChartCell(d.Name, new StackedHistory(60, "Read", "Write")) { MinMax = 1048576, Stroke = DiskTeal, Label = d.Name };
                _diskCells.Add(cell);
            }
            cell.Seen = true;
            cell.Data.Add(d.Read + d.Write);
            cell.Stack.Add(d.Read + d.Write, new() { ["Read"] = d.Read, ["Write"] = d.Write });
            cell.ValueText = $"{d.Active:0}% active";
            cell.Sub = $"Read {Fmt.Rate(d.Read)} · Write {Fmt.Rate(d.Write)}";
            cell.Tip = $"{d.Name}: busy {d.Active:0}% of the time";
        }
        foreach (var gone in _diskCells.Where(c => !c.Seen).ToList()) _diskCells.Remove(gone);
    }

    void RecordGpuCells(SystemSample s)
    {
        foreach (var c in _gpuCells) c.Seen = false;
        foreach (var a in s.Gpus)
        {
            var cell = _gpuCells.FirstOrDefault(c => c.Key == a.Luid);
            if (cell == null)
            {
                cell = new ChartCell(a.Luid, new StackedHistory(60) { SlotSource = _gpuStack }) { FixedMax = 100, Stroke = GpuGreen, Label = a.Name };
                _gpuCells.Add(cell);
            }
            cell.Seen = true;
            var parts = new Dictionary<string, double>();
            foreach (var g in _groups.Values)
                if (g.GpuByAdapter != null && g.GpuByAdapter.TryGetValue(a.Luid, out double v) && v > 0) parts[g.ImageName] = v;
            cell.Data.Add(a.Util);
            cell.Stack.Add(a.Util, parts);
            cell.ValueText = Fmt.Pct(a.Util);
            cell.Label = s.Gpus.Count > 1 ? $"GPU {a.Index} · {a.Name}" : a.Name;
            cell.Sub = $"{UsedOf(a.DedicatedUsed, a.DedicatedTotal)} dedicated · {UsedOf(a.SharedUsed, a.SharedTotal)} shared";
            var top = a.Engines.Where(e => e.Value >= 0.05).OrderByDescending(e => e.Value).Take(4).Select(e => $"{e.Key} {Fmt.Pct(e.Value)}");
            cell.Tip = $"{a.Name}\nBusiest engine: {Fmt.Pct(a.Util)}" + (top.Any() ? $" ({string.Join(", ", top)})" : "");
        }
        foreach (var gone in _gpuCells.Where(c => !c.Seen).ToList()) _gpuCells.Remove(gone);
    }

    static string UsedOf(long used, long total) => total > 0 ? $"{Fmt.Bytes(used)} / {Fmt.Bytes(total)}" : Fmt.Bytes(used);

    /// <summary>One graph per engine type on each GPU (3D, Copy, Video Encode, Video Decode, …), like Task Manager.</summary>
    void RecordGpuEngineCells(SystemSample s)
    {
        var luids = s.Gpus.Select(a => a.Luid).ToHashSet();
        bool changed = false;
        foreach (var gone in _gpuEngineCells.Where(c => !luids.Contains(GpuEngine.Split(c.Key).luid)).ToList())
        {
            _gpuEngineCells.Remove(gone); changed = true;
        }
        var byKey = _gpuEngineCells.ToDictionary(c => c.Key);
        foreach (var a in s.Gpus)
        {
            // The main engines always have a graph; others (Compute, Security, …) once they have been busy.
            // A graph that goes quiet stays (reading 0%) so the grid doesn't jump around.
            foreach (var (type, util) in a.Engines)
            {
                string key = GpuEngine.Key(a.Luid, type);
                if (util >= 0.5) _gpuEnginesUsed.Add(key);
                if (byKey.ContainsKey(key) || !(GpuEngine.IsMain(type) || _gpuEnginesUsed.Contains(key))) continue;
                var cell = new ChartCell(key, new StackedHistory(60) { SlotSource = _gpuStack }) { FixedMax = 100, Stroke = GpuGreen };
                byKey[key] = cell; _gpuEngineCells.Add(cell); changed = true;
            }
            foreach (var (key, cell) in byKey)
            {
                var (luid, type) = GpuEngine.Split(key);
                if (luid != a.Luid) continue;
                double v = a.Engines.GetValueOrDefault(type);
                var parts = new Dictionary<string, double>();
                foreach (var g in _groups.Values)
                    if (g.GpuEngines != null && g.GpuEngines.TryGetValue(key, out double gv) && gv > 0) parts[g.ImageName] = gv;
                cell.Data.Add(v);
                cell.Stack.Add(v, parts);
                cell.Label = s.Gpus.Count > 1 ? $"GPU {a.Index} · {type}" : type;
                cell.ValueText = Fmt.Pct(v);
                var top = parts.OrderByDescending(p => p.Value).Take(3).Select(p => $"{_gpuStack.Label(p.Key)} {Fmt.Pct(p.Value)}");
                cell.Tip = $"{a.Name} · {type}: {Fmt.Pct(v)} (busiest {type} engine)" + (top.Any() ? "\n" + string.Join("\n", top) : "");
            }
        }
        if (!changed) return;
        int Index(ChartCell c) => s.Gpus.FindIndex(a => a.Luid == GpuEngine.Split(c.Key).luid);
        var sorted = _gpuEngineCells.OrderBy(Index).ThenBy(c => GpuEngine.Split(c.Key).type, Comparer<string>.Create(GpuEngine.Compare)).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = _gpuEngineCells.IndexOf(sorted[i]);
            if (at != i) _gpuEngineCells.Move(at, i);
        }
    }

    /// <summary>Dedicated (video) and shared (system RAM) GPU memory, one pair of graphs per GPU.</summary>
    void RecordGpuMemCells(SystemSample s)
    {
        foreach (var c in _gpuMemCells) c.Seen = false;
        foreach (var a in s.Gpus)
        {
            string gpu = s.Gpus.Count > 1 ? $"GPU {a.Index} · " : "";
            Cell(a.Luid + "|dedicated", $"{gpu}Dedicated memory", a.DedicatedUsed, a.DedicatedTotal, GpuGreen,
                 $"{a.Name}\nDedicated GPU memory: video memory on the graphics card (on integrated GPUs, RAM set aside for it)");
            Cell(a.Luid + "|shared", $"{gpu}Shared memory", a.SharedUsed, a.SharedTotal, GpuMemGreen,
                 $"{a.Name}\nShared GPU memory: system RAM the GPU is using; it counts toward RAM in use");
        }
        foreach (var gone in _gpuMemCells.Where(c => !c.Seen).ToList()) _gpuMemCells.Remove(gone);

        void Cell(string key, string label, long used, long total, Brush stroke, string tip)
        {
            var cell = _gpuMemCells.FirstOrDefault(c => c.Key == key);
            if (cell == null)
            {
                // No per-app split for memory: the stack stays empty, so the graph is a line even when stacked.
                cell = new ChartCell(key, new StackedHistory(60)) { FixedMax = total, MinMax = 256.0 * 1048576, Stroke = stroke };
                _gpuMemCells.Add(cell);
            }
            cell.Seen = true;
            cell.Data.Add(used);
            cell.Label = label;
            cell.ValueText = UsedOf(used, total);
            cell.Sub = total > 0 ? $"{used * 100.0 / total:0}% used" : "";
            cell.Tip = tip;
        }
    }

    // ───────────────────────── panel ─────────────────────────

    void Tile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string res }) return;
        ShowPanel(_settings.OpenPanel == res ? null : res);
    }

    void ClosePanel_Click(object sender, RoutedEventArgs e) => ShowPanel(null);

    void StackToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.StackedGraphs = StackToggle.IsChecked == true;
        ApplyStackedToTiles();
        UpdatePanel();
    }

    void ApplyStackedToTiles()
    {
        bool st = _settings.StackedGraphs;
        foreach (var sp in new[] { CpuSpark, MemSpark, CommitSpark, GpuSpark, DiskSpark }) sp.Stacked = st;
    }

    void ShowPanel(string? res)
    {
        _settings.OpenPanel = res;
        ViewButtons.Children.Clear();
        if (res == "cpu") AddViewButtons(("overall", "Overall"), ("cores", "Cores"), ("threads", "Threads"));
        if (res == "gpu") AddViewButtons(("overall", "Overall"), ("engines", "Engines"), ("gpus", "Per GPU"), ("memory", "Memory"));
        UpdatePanel();
        if (_lastSys != null) UpdateTiles(_lastSys, record: false);
    }

    void AddViewButtons(params (string key, string label)[] views)
    {
        foreach (var (key, label) in views)
        {
            var b = new ToggleButton { Content = label, Tag = key, Margin = new Thickness(4, 0, 0, 0) };
            b.Click += (_, _) =>
            {
                if (_settings.OpenPanel == "cpu") _settings.CpuView = key; else _settings.GpuView = key;
                UpdatePanel();
            };
            ViewButtons.Children.Add(b);
        }
    }

    string CurrentView() => _settings.OpenPanel switch
    {
        "cpu" => _settings.CpuView,
        "gpu" => _settings.GpuView,
        "disk" => "disks",
        _ => "overall",
    };

    void UpdatePanel()
    {
        string? res = _settings.OpenPanel;
        if (res == null) { ResourcePanel.Visibility = Visibility.Collapsed; return; }
        ResourcePanel.Visibility = Visibility.Visible;
        string view = CurrentView();
        foreach (ToggleButton b in ViewButtons.Children) b.IsChecked = (string)b.Tag == view;
        bool stacked = _settings.StackedGraphs;
        var s = _lastSys;

        int nLogical = _threadCells.Count > 0 ? _threadCells.Count : Environment.ProcessorCount;
        int eCores = _topology.CoreIsEfficiency.Count(x => x);
        string cpuShape = $"{nLogical} logical processors on {_topology.CoreThreads.Count} cores" +
                          (_topology.Hybrid ? $" ({_topology.CoreThreads.Count - eCores} P + {eCores} E)" : "");
        PanelTitle.Text = res switch { "cpu" => "CPU", "mem" => "Memory (RAM)", "commit" => "Commit", "gpu" => "GPU", _ => "Disks" };
        PanelSub.Text = s == null ? "" : res switch
        {
            "cpu" => $"{Fmt.Pct(s.Cpu)} · {cpuShape}" +
                     (view != "overall" && _selAffinity != 0 && _selAffinity != _sysAffinity ? " · outlined = where the selected process may run" : ""),
            "mem" => $"{Fmt.Gb(s.PhysTotal - s.PhysAvail)} of {Fmt.Gb(s.PhysTotal)} in use · last 60 s",
            "commit" => $"{Fmt.Gb(s.CommitLimit - s.CommitAvail)} of {Fmt.Gb(s.CommitLimit)} committed · {Fmt.Gb(s.CommitAvail)} free",
            "gpu" => $"{_gpuCells.Count} GPU{(_gpuCells.Count == 1 ? "" : "s")} · busiest engine {Fmt.Pct(s.Gpu)}" +
                     $" · {UsedOf(s.GpuMem, s.GpuMemTotal)} dedicated · {Fmt.Bytes(s.GpuShared)} shared",
            _ => $"{_diskCells.Count} disk{(_diskCells.Count == 1 ? "" : "s")} · {Fmt.Rate(s.Disk)} by all processes",
        };

        if (view is "cores" or "threads" or "gpus" or "engines" or "memory" or "disks")
        {
            var cells = view switch
            {
                "cores" => _coreCells, "threads" => _threadCells, "gpus" => _gpuCells,
                "engines" => _gpuEngineCells, "memory" => _gpuMemCells, _ => _diskCells,
            };
            BigView.Visibility = Visibility.Collapsed;
            CellsView.Visibility = Visibility.Visible;
            if (!ReferenceEquals(CellsView.ItemsSource, cells)) CellsView.ItemsSource = cells;
            int n = Math.Max(1, cells.Count);
            int rows = view switch
            {
                "gpus" or "disks" or "memory" => n <= 4 ? 1 : 2,
                "engines" => n <= 6 ? 1 : 2,
                _ => n <= 8 ? 1 : n <= 16 ? 2 : n <= 48 ? 3 : 4,
            };
            CellColumns = (int)Math.Ceiling(n / (double)rows);
            bool restricted = _selAffinity != 0 && _selAffinity != _sysAffinity;
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[i];
                c.Stacked = stacked;
                c.Highlight = restricted && view switch
                {
                    "threads" => i < 64 && (_selAffinity & (1UL << i)) != 0,
                    "cores" => _topology.CoreThreads[i].Any(t => t < 64 && (_selAffinity & (1UL << t)) != 0),
                    _ => false,
                };
                c.Bump();
            }
            HeaderLegend.ItemsSource = !stacked ? null : view switch
            {
                "gpus" or "engines" => AppLegend(_gpuStack, Fmt.Pct, swatches: true, withValues: false),
                "memory" => null,
                "disks" => FixedLegend(("Read", 0), ("Write", 1)),
                _ => FixedLegend(("User", 0), ("Kernel", 1)),
            };
            return;
        }

        CellsView.Visibility = Visibility.Collapsed;
        BigView.Visibility = Visibility.Visible;
        HeaderLegend.ItemsSource = null;
        var (stack, data, fixedMax, minMax, stroke, fmt) = res switch
        {
            "cpu" => (_cpuStack, _cpuHist, 100.0, 1.0, CpuBlue, (Func<double, string>)Fmt.Pct),
            "mem" => (_memStack, _memHist, 100.0, 1.0, Frozen("#A66BFF"), v => Fmt.Bytes(v / 100 * (s?.PhysTotal ?? 0))),
            "commit" => (_commitStack, _commitHist, 100.0, 1.0, Frozen("#E8A33D"), v => Fmt.Bytes(v / 100 * (s?.CommitLimit ?? 0))),
            "gpu" => (_gpuStack, _gpuHist, 100.0, 1.0, GpuGreen, Fmt.Pct),
            _ => (_diskStack, _diskHist, 0.0, 1048576.0, DiskTeal, (Func<double, string>)Fmt.Rate),
        };
        BigGraph.Data = data; BigGraph.Stack = stack; BigGraph.FixedMax = fixedMax; BigGraph.MinMax = minMax;
        BigGraph.Stroke = stroke; BigGraph.Stacked = stacked; BigGraph.Version++;
        LegendTitle.Text = stacked ? "Colors · now" : "Top apps · now";
        Legend.ItemsSource = AppLegend(stack, fmt, swatches: stacked, withValues: true);
    }

    static List<LegendItem> FixedLegend(params (string label, int slot)[] items) =>
        items.Select(i => new LegendItem { Swatch = ChartPalette.Slot(i.slot), Label = i.label }).ToList();

    /// <summary>Legend for a per-app history: the colored series plus "Other", or just the top apps.</summary>
    static List<LegendItem> AppLegend(StackedHistory st, Func<double, string> fmt, bool swatches, bool withValues)
    {
        var items = new List<LegendItem>();
        if (swatches)
        {
            double shown = 0;
            foreach (var (key, slot) in st.Series())
            {
                double v = st.LastPart(key);
                shown += v;
                items.Add(new LegendItem { Swatch = ChartPalette.Slot(slot), Label = st.Label(key), Value = withValues ? fmt(v) : "" });
            }
            items.Add(new LegendItem { Swatch = ChartPalette.Other, Label = "Other", Value = withValues ? fmt(Math.Max(0, st.LastTotal - shown)) : "" });
        }
        else
        {
            foreach (var (key, v) in st.LastParts.OrderByDescending(p => p.Value).Take(7))
                items.Add(new LegendItem { Label = st.Label(key), Value = fmt(v) });
        }
        return items;
    }

    // ───────────────────────── selected process: cores & threads ─────────────────────────

    void UpdateCpuUse(Row r)
    {
        _selAffinity = _sysAffinity = 0;
        if (r is ProcRow p)
        {
            DCpuUse.Text = $"Using {p.Cores:0.0} cores ({Fmt.Pct(p.Cpu)}) · {p.BusyThreads} of {p.Threads} threads busy";
            SetAffinityLink("Change…");
            try
            {
                var a = Actions.GetAffinity(p);
                // The core/thread cells only cover group 0, so only outline them for processes there.
                if (a.Group == 0) { _selAffinity = a.Mask; _sysAffinity = a.GroupMask; }
                string group = Affinity.GroupCount > 1 ? $" in processor group {a.Group}" : "";
                DAffinityText.Text = a.MultiGroup && !a.Restricted ? "Affinity: may run on processors in more than one group "
                    : !a.Restricted ? $"Affinity: may run on any logical processor{group} "
                    : $"Affinity: limited to CPU {Affinity.Ranges(a.Mask, a.Offset)}" +
                      $" ({Affinity.Count(a.Mask)} of {Affinity.Count(a.GroupMask)}{DescribeCores(a)}){group} ";
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                DAffinityText.Text = "Affinity: can't read it" + (_elevated ? " " : " (run as admin to see more) ");
            }
        }
        else if (r is GroupRow g)
        {
            DCpuUse.Text = $"Using {g.Cores:0.0} cores ({Fmt.Pct(g.Cpu)}) · {g.BusyThreads} of {g.Threads} threads busy across {g.Children.Count} processes";
            DAffinityText.Text = "Affinity: set per process ";
            SetAffinityLink($"Set for all {g.Children.Count}…");
        }
    }

    // ───────────────────────── selected process: GPU ─────────────────────────

    /// <summary>"GPU 12%: 3D 12%, Video Decode 3% · 1.2 GB dedicated, 85 MB shared"; hidden when the process doesn't use a GPU.</summary>
    void UpdateGpuUse(Row r)
    {
        var engines = r.GpuEngines?.Where(e => e.Value >= 0.05).OrderByDescending(e => e.Value).Take(4)
                          .Select(e => $"{Row.EngineLabel(e.Key)} {Fmt.Pct(e.Value)}").ToList() ?? new();
        if (engines.Count == 0 && r.GpuMem == 0 && r.GpuShared == 0) { DGpuUse.Visibility = Visibility.Collapsed; return; }
        DGpuUse.Visibility = Visibility.Visible;
        string use = engines.Count > 0 ? $"GPU {Fmt.Pct(r.Gpu)}: {string.Join(", ", engines)}" : "GPU idle";
        DGpuUse.Text = $"{use} · {Fmt.Bytes(r.GpuMem)} dedicated, {Fmt.Bytes(r.GpuShared)} shared GPU memory";
    }

    string _affinityLink = "Change…";
    void SetAffinityLink(string text)
    {
        if (text == _affinityLink) return; // rebuilding the inline every tick would reset its hover state
        _affinityLink = text;
        DAffinityLink.Inlines.Clear();
        DAffinityLink.Inlines.Add(text);
    }

    /// <summary>", P-cores only" style note for hybrid CPUs, so an E-core-only limit is obvious.</summary>
    string DescribeCores(Affinity a)
    {
        if (a.Group != 0 || !_topology.Hybrid) return "";
        bool anyP = false, anyE = false;
        for (int i = 0; i < 64 && i < _topology.CoreOfLogical.Length; i++)
        {
            if ((a.Mask & (1UL << i)) == 0 || _topology.CoreOfLogical[i] < 0) continue;
            if (_topology.CoreIsEfficiency[_topology.CoreOfLogical[i]]) anyE = true; else anyP = true;
        }
        return anyP && !anyE ? ", P-cores only" : anyE && !anyP ? ", E-cores only" : "";
    }
}
