using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace TaskScope;

public partial class MainWindow : Window
{
    const double GB = 1073741824;

    readonly Settings _settings = Settings.Load();
    readonly Sampler _sampler = new();
    readonly Dictionary<ProcKey, ProcRow> _procs = new();
    readonly Dictionary<string, GroupRow> _groups = new(StringComparer.OrdinalIgnoreCase);
    readonly ObservableCollection<Row> _rows = new();
    readonly DispatcherTimer _timer = new();
    readonly RingBuffer _cpuHist = new(60), _memHist = new(60), _commitHist = new(60), _gpuHist = new(60), _diskHist = new(60);
    readonly HashSet<ProcKey> _watchFired = new();
    readonly bool _elevated;
    WinForms.NotifyIcon? _tray;
    bool _busy, _commitAlertArmed = true, _paused;
    string _search = "";
    long _tick;
    SystemSample? _lastSys;

    public MainWindow()
    {
        InitializeComponent();
        _elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        Icon = IconCache.AppIcon(32);
        if (Environment.GetCommandLineArgs().Contains("--no-activate")) ShowActivated = false;
        if (_elevated) { AdminButton.Visibility = Visibility.Collapsed; Title = "TaskScope (Administrator)"; }

        ProcGrid.ItemsSource = _rows;
        CpuSpark.Data = _cpuHist; MemSpark.Data = _memHist; CommitSpark.Data = _commitHist;
        GpuSpark.Data = _gpuHist; DiskSpark.Data = _diskHist;

        GroupToggle.IsChecked = _settings.GroupByApp;
        TopToggle.IsChecked = Topmost = _settings.AlwaysOnTop;
        RestorePlacement();
        ApplySortGlyph();
        SetupTray();
        InitPanel();

        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.IntervalMs, 250, 10000));
        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) =>
        {
            Tick();
            await Task.Delay(400);
            Tick();
            _timer.Start();
        };
        PreviewKeyDown += Window_PreviewKeyDown;
        Closing += Window_Closing;
    }

    // ───────────────────────── sampling ─────────────────────────

    async void Tick()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snap = await Task.Run(_sampler.Sample);
            Apply(snap);
        }
        catch (Exception ex) { StatusRight.Text = "Sampling error: " + ex.Message; }
        finally { _busy = false; }
    }

    void Apply(Snapshot snap)
    {
        _tick++;
        _lastSys = snap.Sys;
        Row.MemScale = Math.Max(GB, snap.Sys.PhysTotal / 4.0);
        Row.GpuIndex = snap.Sys.Gpus.ToDictionary(a => a.Luid, a => a.Index);

        foreach (var s in snap.Procs)
        {
            if (!_procs.TryGetValue(s.Key, out var row))
            {
                row = new ProcRow(s) { Icon = IconCache.Get(s.Info.Path) };
                _procs[s.Key] = row;
                if (!_groups.TryGetValue(row.ImageName, out var g))
                    _groups[row.ImageName] = g = new GroupRow(row.ImageName);
                g.Children.Add(row);
                row.Group = g;
            }
            row.Update(s, _tick);
        }

        foreach (var dead in _procs.Values.Where(p => p.LastTick != _tick).ToList())
        {
            _procs.Remove(dead.Key);
            dead.Group?.Children.Remove(dead);
            _watchFired.Remove(dead.Key);
        }
        foreach (var g in _groups.Values.ToList())
        {
            if (g.Children.Count == 0) _groups.Remove(g.ImageName);
            else g.Aggregate();
        }

        RecordHistories(snap.Sys);
        UpdateTiles(snap.Sys);
        CheckCommit(snap.Sys);
        CheckWatches();
        if (!_paused) RebuildView();
        UpdateDetails();
        UpdatePanel();
        StatusLeft.Text = $"{_procs.Count} processes · {snap.Sys.Threads:N0} threads · {snap.Sys.Handles:N0} handles" +
                          (_paused ? " · PAUSED" : $" · updates every {_timer.Interval.TotalSeconds:0.#} s");
        UpdateTrayText(snap.Sys);
    }

    void UpdateTiles(SystemSample s, bool record = true)
    {
        if (record)
        {
            _cpuHist.Add(s.Cpu);
            _memHist.Add(s.PhysTotal > 0 ? (s.PhysTotal - s.PhysAvail) * 100.0 / s.PhysTotal : 0);
            _commitHist.Add(s.CommitLimit > 0 ? (s.CommitLimit - s.CommitAvail) * 100.0 / s.CommitLimit : 0);
            _gpuHist.Add(s.Gpu);
            _diskHist.Add(s.Disk);
        }
        CpuValue.Text = Fmt.Pct(s.Cpu);
        CpuSub.Text = $"{Environment.ProcessorCount} logical processors";

        double physUsed = s.PhysTotal - s.PhysAvail;
        double memPct = s.PhysTotal > 0 ? physUsed * 100 / s.PhysTotal : 0;
        MemValue.Text = $"{physUsed / GB:0.0} / {s.PhysTotal / GB:0} GB";
        MemSub.Text = $"{memPct:0}% in use · {Fmt.Gb(s.PhysAvail)} available";

        double commitUsed = s.CommitLimit - s.CommitAvail;
        CommitValue.Text = $"{commitUsed / GB:0.0} / {s.CommitLimit / GB:0} GB";
        CommitSub.Text = $"{Fmt.Gb(s.CommitAvail)} free · warn under {_settings.CommitWarnGB:0.#} GB";
        bool low = s.CommitAvail / GB < _settings.CommitWarnGB;
        if (low)
        {
            var red = TryFindResource("SystemFillColorCriticalBrush") as Brush ?? Brushes.IndianRed;
            CommitTile.BorderBrush = red; CommitValue.Foreground = red;
            CommitSpark.Stroke = red;
        }
        else
        {
            CommitTile.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            CommitValue.ClearValue(TextBlock.ForegroundProperty);
            CommitSpark.Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        }

        if (s.GpuAvailable)
        {
            GpuValue.Text = Fmt.Pct(s.Gpu);
            GpuSub.Text = GpuTileSub(s);
        }
        else { GpuValue.Text = "n/a"; GpuSub.Text = "GPU counters unavailable"; }

        DiskValue.Text = Fmt.Rate(s.Disk);
        DiskSub.Text = s.Disks.Count > 0 ? $"{s.Disks.Count} disk{(s.Disks.Count == 1 ? "" : "s")} · busiest {s.Disks.Max(d => d.Active):0}% active"
                                         : "read + write, all processes";


        // The open tile gets an accent outline (the commit tile stays red while memory is low).
        var accent = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
        foreach (var tile in new[] { CpuTile, MemTile, CommitTile, GpuTile, DiskTile })
        {
            if (tile == CommitTile && low) continue;
            if ((string)tile.Tag == _settings.OpenPanel) tile.BorderBrush = accent;
            else tile.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        }

        CpuSpark.Refresh(); MemSpark.Refresh(); CommitSpark.Refresh(); GpuSpark.Refresh(); DiskSpark.Refresh();
    }

    /// <summary>"3D 12% · 1.2 / 8.0 GB dedicated": the busiest engine type and dedicated memory.</summary>
    static string GpuTileSub(SystemSample s)
    {
        string mem = s.GpuMemTotal > 0 ? $"{Fmt.Bytes(s.GpuMem)} / {Fmt.Bytes(s.GpuMemTotal)} dedicated" : $"{Fmt.Bytes(s.GpuMem)} dedicated";
        if (s.Gpus.Count > 1) return $"{s.Gpus.Count} GPUs · {mem}";
        var busiest = s.Gpus.SelectMany(a => a.Engines).Where(e => e.Value >= 0.05).OrderByDescending(e => e.Value).FirstOrDefault();
        return busiest.Key == null ? mem : $"{busiest.Key} {Fmt.Pct(busiest.Value)} · {mem}";
    }

    // ───────────────────────── alerts ─────────────────────────

    void CheckCommit(SystemSample s)
    {
        double freeGB = s.CommitAvail / GB;
        if (freeGB < _settings.CommitWarnGB)
        {
            var top = _procs.Values.OrderByDescending(p => p.Private).Take(3)
                .Select(p => $"{p.ImageName} {Fmt.Bytes(p.Private)}").ToList();
            BannerText.Text = $"Low on memory: only {freeGB:0.0} GB of commit left (limit {s.CommitLimit / GB:0} GB). " +
                              $"When it reaches zero, programs crash. Biggest: {string.Join(", ", top)}. " +
                              "Pause (suspend) a big process to stop it growing without losing its work.";
            Banner.Visibility = Visibility.Visible;
            if (_commitAlertArmed)
            {
                _commitAlertArmed = false;
                Notify("Low on memory", $"Only {freeGB:0.0} GB of commit left. Biggest: {string.Join(", ", top)}");
            }
        }
        else
        {
            Banner.Visibility = Visibility.Collapsed;
            if (freeGB > _settings.CommitWarnGB + 2) _commitAlertArmed = true;
        }
    }

    void CheckWatches()
    {
        if (_settings.Watches.Count == 0) return;
        foreach (var p in _procs.Values.ToList())
        {
            foreach (var rule in _settings.Watches)
            {
                if (!rule.Matches(p.ImageName) || p.Private < rule.LimitGB * GB || !_watchFired.Add(p.Key)) continue;
                string what = $"{p.ImageName} (PID {p.Pid}) is using {Fmt.Bytes(p.Private)}, over its {rule.LimitGB:0.#} GB limit.";
                try
                {
                    switch (rule.Action)
                    {
                        case "Suspend":
                            Actions.Suspend(p);
                            Notify("Process paused", what + " It has been paused; right-click it and choose Resume to continue.");
                            break;
                        case "End":
                            Actions.Kill(p);
                            Notify("Process ended", what + " It has been ended.");
                            break;
                        default:
                            Notify("Memory watch", what);
                            break;
                    }
                }
                catch (Exception ex) { Notify("Memory watch", what + $" Could not {rule.Action.ToLower()} it: {ex.Message}"); }
                break;
            }
        }
    }

    void Notify(string title, string text)
    {
        StatusRight.Text = $"{DateTime.Now:HH:mm:ss}  {title}: {text}";
        _tray?.ShowBalloonTip(8000, title, text, WinForms.ToolTipIcon.Warning);
    }

    // ───────────────────────── list building ─────────────────────────

    bool Matches(ProcRow p) =>
        _search.Length == 0 ||
        p.Name.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        p.ImageName.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        p.Pid.ToString() == _search ||
        p.Path.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
        p.CommandLine.Contains(_search, StringComparison.OrdinalIgnoreCase);

    int Compare(Row a, Row b)
    {
        int c = _settings.SortKey switch
        {
            "Name" => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            "ImageName" => string.Compare(a.ImageName, b.ImageName, StringComparison.OrdinalIgnoreCase),
            "Pid" => a.PidSort.CompareTo(b.PidSort),
            "Status" => a.Suspended.CompareTo(b.Suspended),
            "Cpu" or "Cores" => a.Cpu.CompareTo(b.Cpu),
            "Private" => a.Private.CompareTo(b.Private),
            "WorkingSet" => a.WorkingSet.CompareTo(b.WorkingSet),
            "Disk" => a.Disk.CompareTo(b.Disk),
            "Gpu" => a.Gpu.CompareTo(b.Gpu),
            "GpuMem" => a.GpuMem.CompareTo(b.GpuMem),
            "GpuShared" => a.GpuShared.CompareTo(b.GpuShared),
            "GpuEngine" => string.CompareOrdinal(a.GpuEngineText, b.GpuEngineText) is int e && e != 0 ? e : a.Gpu.CompareTo(b.Gpu),
            "Threads" => a.Threads.CompareTo(b.Threads),
            "Handles" => a.Handles.CompareTo(b.Handles),
            _ => 0,
        };
        if (_settings.SortDesc) c = -c;
        if (c == 0) c = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        if (c == 0) c = a.PidSort.CompareTo(b.PidSort);
        return c;
    }

    void RebuildView()
    {
        var desired = new List<Row>(_procs.Count);
        if (_settings.GroupByApp)
        {
            var tops = new List<(Row top, List<ProcRow> kids)>();
            foreach (var g in _groups.Values)
            {
                var kids = g.Children.Where(Matches).ToList();
                if (kids.Count == 0) continue;
                if (g.Children.Count == 1) tops.Add((g.Children[0], kids));
                else tops.Add((g, kids));
            }
            tops.Sort((x, y) => Compare(x.top, y.top));
            foreach (var (top, kids) in tops)
            {
                top.Indent = 0;
                desired.Add(top);
                if (top is GroupRow tg) tg.ShowGlyph(tg.IsExpanded || _search.Length > 0);
                if (top is GroupRow g && (g.IsExpanded || _search.Length > 0))
                {
                    kids.Sort(Compare);
                    foreach (var k in kids) { k.Indent = 22; desired.Add(k); }
                }
            }
        }
        else
        {
            var all = _procs.Values.Where(Matches).ToList();
            all.Sort(Compare);
            foreach (var p in all) { p.Indent = 0; desired.Add(p); }
        }

        // Reconcile in place so selection and scroll position survive updates.
        for (int i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            if (i < _rows.Count && ReferenceEquals(_rows[i], want)) continue;
            int j = -1;
            for (int k = i + 1; k < _rows.Count; k++) if (ReferenceEquals(_rows[k], want)) { j = k; break; }
            if (j >= 0) _rows.Move(j, i);
            else _rows.Insert(i, want);
        }
        while (_rows.Count > desired.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    void ApplySortGlyph()
    {
        foreach (var col in ProcGrid.Columns)
            col.SortDirection = col.SortMemberPath == _settings.SortKey
                ? (_settings.SortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
    }

    // ───────────────────────── details pane ─────────────────────────

    void UpdateDetails()
    {
        if (ProcGrid.SelectedItem is not Row r)
        {
            _selAffinity = _sysAffinity = 0;
            DetailsEmpty.Visibility = Visibility.Visible;
            DetailsBody.Visibility = Visibility.Collapsed;
            return;
        }
        DetailsEmpty.Visibility = Visibility.Collapsed;
        DetailsBody.Visibility = Visibility.Visible;
        DIcon.Source = r.Icon;
        DName.Text = r.Name;
        if (r is ProcRow p)
        {
            string parent = _procs.Values.FirstOrDefault(x => x.Pid == p.ParentPid && x.Key.Create <= p.Key.Create)?.ImageName ?? "exited";
            DSub.Text = $"{p.ImageName} · PID {p.Pid} · parent {p.ParentPid} ({parent})" +
                        (p.Started is { } st ? $" · started {st:g}" : "") +
                        $" · {Fmt.Priority(p.BasePriority)} priority" + (p.Suspended ? " · PAUSED" : "");
            DPath.Text = p.Path.Length > 0 ? p.Path : (_elevated ? "(none)" : "(access denied – run as admin to see more)");
            DCmd.Text = p.CommandLine.Length > 0 ? p.CommandLine : "(not available)";
        }
        else if (r is GroupRow g)
        {
            DSub.Text = $"{g.ImageName} · {g.Children.Count} processes · {Fmt.Bytes(g.Private)} memory in total";
            DPath.Text = g.Children.FirstOrDefault(c => c.Path.Length > 0)?.Path ?? "";
            DCmd.Text = "Expand the group to see each process's command line.";
        }
        DPath.ToolTip = DPath.Text;
        UpdateCpuUse(r);
        UpdateGpuUse(r);
        DCpu.Data = r.CpuHist; DMem.Data = r.MemHist;
        DCpu.Refresh(); DMem.Refresh();
        DCpuVal.Text = r.CpuText; DMemVal.Text = r.PrivateText;
    }

    // ───────────────────────── grid events ─────────────────────────

    void ProcGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        string key = e.Column.SortMemberPath;
        if (key == _settings.SortKey) _settings.SortDesc = !_settings.SortDesc;
        else
        {
            _settings.SortKey = key;
            _settings.SortDesc = key is not ("Name" or "ImageName" or "Pid");
        }
        ApplySortGlyph();
        RebuildView();
    }

    void ProcGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateDetails();

    void Expander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is GroupRow g) ToggleGroup(g, !g.IsExpanded);
        e.Handled = true;
    }

    void ToggleGroup(GroupRow g, bool expand)
    {
        g.IsExpanded = expand;
        RebuildView();
    }

    void ProcGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcGrid.SelectedItem is GroupRow g) ToggleGroup(g, !g.IsExpanded);
    }

    void ProcGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Delete:
                End_Click(this, e); e.Handled = true; break;
            case Key.Right when ProcGrid.SelectedItem is GroupRow g && !g.IsExpanded:
                ToggleGroup(g, true); e.Handled = true; break;
            case Key.Left when ProcGrid.SelectedItem is GroupRow g && g.IsExpanded:
                ToggleGroup(g, false); e.Handled = true; break;
            case Key.Left when ProcGrid.SelectedItem is ProcRow { Group: { IsExpanded: true } pg } && _rows.Contains(pg):
                ProcGrid.SelectedItem = pg; ProcGrid.ScrollIntoView(pg); e.Handled = true; break;
        }
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            SearchBox.Clear(); ProcGrid.Focus(); e.Handled = true;
        }
        else if (e.Key == Key.Space && !SearchBox.IsKeyboardFocusWithin)
        {
            PauseToggle.IsChecked = !PauseToggle.IsChecked;
            PauseToggle_Click(this, e); e.Handled = true;
        }
    }

    void ProcGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var t = Targets();
        if (t.Count == 0) { e.Handled = true; return; }
        MiSuspend.IsEnabled = t.Any(p => !p.Suspended);
        MiResume.IsEnabled = t.Any(p => p.Suspended);
        MiEnd.Header = t.Count > 1 ? $"End {t.Count} processes" : "End task";
    }

    // ───────────────────────── actions ─────────────────────────

    List<ProcRow> Targets() => ProcGrid.SelectedItems.Cast<Row>()
        .SelectMany(r => r is GroupRow g ? g.Children.AsEnumerable() : new[] { (ProcRow)r })
        .Distinct().ToList();

    bool ConfirmCritical(IEnumerable<ProcRow> targets, string verb)
    {
        var crit = targets.Where(p => Actions.Critical.Contains(p.ImageName)).Select(p => p.ImageName).Distinct().ToList();
        if (crit.Count == 0) return true;
        return MessageBox.Show(this,
            $"{string.Join(", ", crit)} {(crit.Count == 1 ? "is" : "are")} part of Windows. If you {verb} {(crit.Count == 1 ? "it" : "them")}, " +
            "Windows may freeze, crash or log you out.\n\nContinue anyway?",
            "TaskScope", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    void Run(IEnumerable<ProcRow> targets, Action<ProcRow> action, string verb)
    {
        var errors = new List<string>();
        foreach (var p in targets)
        {
            try { action(p); }
            catch (Exception ex) { errors.Add($"{p.ImageName} (PID {p.Pid}): {ex.Message}"); }
        }
        if (errors.Count > 0)
        {
            string hint = !_elevated && errors.Any(e => e.Contains("denied", StringComparison.OrdinalIgnoreCase))
                ? "\n\nClick “Run as admin” to control elevated and system processes." : "";
            MessageBox.Show(this, $"Couldn't {verb}:\n" + string.Join("\n", errors.Take(12)) + hint,
                "TaskScope", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Tick();
    }

    void End_Click(object sender, RoutedEventArgs e)
    {
        var t = Targets();
        if (t.Count == 0 || !ConfirmCritical(t, "end")) return;
        if (t.Count > 1 && MessageBox.Show(this, $"End {t.Count} processes? Unsaved work in them will be lost.", "TaskScope",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Run(t, Actions.Kill, "end");
    }

    void EndTree_Click(object sender, RoutedEventArgs e)
    {
        var roots = Targets();
        if (roots.Count == 0) return;
        var byParent = _procs.Values.ToLookup(p => p.ParentPid);
        var all = new List<ProcRow>();
        var seen = new HashSet<ProcKey>();
        foreach (var root in roots)
        {
            var stack = new Stack<ProcRow>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                if (!seen.Add(cur.Key)) continue;
                all.Add(cur);
                foreach (var c in byParent[cur.Pid])
                    if (c.Key.Create >= cur.Key.Create) stack.Push(c);
            }
        }
        if (!ConfirmCritical(all, "end")) return;
        if (MessageBox.Show(this,
                $"End {all.Count} process{(all.Count == 1 ? "" : "es")} ({string.Join(", ", all.Select(p => p.ImageName).Distinct().Take(6))})? " +
                "Unsaved work in them will be lost.",
                "End process tree", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        all.Reverse(); // children first
        Run(all, Actions.Kill, "end");
    }

    void Suspend_Click(object sender, RoutedEventArgs e)
    {
        var t = Targets().Where(p => !p.Suspended && p.Pid != Environment.ProcessId).ToList();
        if (t.Count == 0 || !ConfirmCritical(t, "pause")) return;
        Run(t, Actions.Suspend, "pause");
    }

    void Resume_Click(object sender, RoutedEventArgs e) => Run(Targets().Where(p => p.Suspended).ToList(), Actions.Resume, "resume");

    void Priority_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string tag || !uint.TryParse(tag, out uint cls)) return;
        Run(Targets(), p => Actions.SetPriority(p, cls), "change the priority of");
    }

    void Affinity_Click(object sender, RoutedEventArgs e)
    {
        var t = Targets();
        if (t.Count == 0) return;
        var read = new List<(ProcRow p, Affinity a)>();
        var unreadable = new List<string>();
        foreach (var p in t)
        {
            try { read.Add((p, Actions.GetAffinity(p))); }
            catch (Exception ex) { unreadable.Add($"{p.ImageName} (PID {p.Pid}): {ex.Message}"); }
        }
        if (read.Count == 0)
        {
            MessageBox.Show(this, "Couldn't read the affinity of:\n" + string.Join("\n", unreadable.Take(12)) +
                (_elevated ? "" : "\n\nClick “Run as admin” to control elevated and system processes."),
                "TaskScope", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var first = read[0].a;
        if (read.Any(x => x.a.Group != first.Group))
        {
            MessageBox.Show(this, "These processes are in different processor groups, and each group has its own processors. " +
                "Select processes from one group at a time.", "TaskScope", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string who = read.Count == 1 ? $"{read[0].p.ImageName} (PID {read[0].p.Pid})" : $"these {read.Count} processes";
        var notes = new List<string>();
        if (read.Any(x => x.a.Mask != first.Mask))
            notes.Add($"They don't all have the same affinity now; the boxes show {read[0].p.ImageName} (PID {read[0].p.Pid}).");
        if (unreadable.Count > 0)
            notes.Add($"{unreadable.Count} more couldn't be read and will be left alone" + (_elevated ? "." : " (run as admin to include them)."));

        var w = new AffinityWindow(who, first, _topology, notes.Count > 0 ? string.Join(" ", notes) : null) { Owner = this };
        if (w.ShowDialog() != true) return;
        var targets = read.Select(x => x.p).ToList();
        if (w.Mask != first.GroupMask && !ConfirmCritical(targets, "limit")) return;
        Run(targets, p => Actions.SetAffinity(p, w.Mask), "change the affinity of");
    }

    void Watch_Click(object sender, RoutedEventArgs e)
    {
        var t = Targets();
        if (t.Count == 0) return;
        string image = t[0].ImageName;
        double biggest = t.Where(p => p.ImageName == image).Max(p => p.Private) / GB;
        var rule = new WatchRule { Image = image, LimitGB = Math.Max(1, Math.Ceiling(biggest * 1.5)), Action = "Notify" };
        OpenSettings(rule);
    }

    void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var p = Targets().FirstOrDefault(x => x.Path.Length > 0);
        if (p == null) { StatusRight.Text = "No file path available for that process."; return; }
        Process.Start("explorer.exe", $"/select,\"{p.Path}\"");
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var t = Targets();
        string text = ((sender as MenuItem)?.Tag as string) switch
        {
            "pid" => string.Join(Environment.NewLine, t.Select(p => p.Pid)),
            "path" => string.Join(Environment.NewLine, t.Select(p => p.Path).Distinct()),
            "cmd" => string.Join(Environment.NewLine, t.Select(p => p.CommandLine)),
            _ => string.Join(Environment.NewLine, t.Select(p => p.ImageName).Distinct()),
        };
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    // ───────────────────────── toolbar ─────────────────────────

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RebuildView();
    }

    void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.GroupByApp = GroupToggle.IsChecked == true;
        RebuildView();
    }

    void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        _paused = PauseToggle.IsChecked == true;
        if (!_paused) RebuildView();
        StatusLeft.Text = _paused ? "Paused: the list is frozen, graphs keep running" : StatusLeft.Text;
    }

    void TopToggle_Click(object sender, RoutedEventArgs e) => Topmost = _settings.AlwaysOnTop = TopToggle.IsChecked == true;

    void ShowBiggest_Click(object sender, RoutedEventArgs e)
    {
        _settings.SortKey = "Private"; _settings.SortDesc = true;
        SearchBox.Clear();
        ApplySortGlyph();
        RebuildView();
        if (_rows.Count > 0) { ProcGrid.SelectedItem = _rows[0]; ProcGrid.ScrollIntoView(_rows[0]); ProcGrid.Focus(); }
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(null);

    void OpenSettings(WatchRule? newRule)
    {
        var w = new SettingsWindow(_settings, newRule, _lastSys) { Owner = this };
        if (w.ShowDialog() == true)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.IntervalMs, 250, 10000));
            _watchFired.Clear();
            _commitAlertArmed = true;
            _settings.Save();
            Tick();
        }
    }

    void AdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Close();
        }
        catch (Win32Exception) { /* user said no to the UAC prompt */ }
    }

    // ───────────────────────── tray & window ─────────────────────────

    void SetupTray()
    {
        _tray = new WinForms.NotifyIcon { Icon = IconCache.AppTrayIcon(), Text = "TaskScope", Visible = true };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Show TaskScope", null, (_, _) => ShowFromTray());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void UpdateTrayText(SystemSample s)
    {
        if (_tray == null) return;
        string t = $"TaskScope · CPU {s.Cpu:0}% · RAM {(s.PhysTotal - s.PhysAvail) * 100.0 / Math.Max(1, s.PhysTotal):0}% · commit free {s.CommitAvail / GB:0} GB";
        _tray.Text = t.Length > 63 ? t[..63] : t;
    }

    void RestorePlacement()
    {
        if (_settings.Width is double w && _settings.Height is double h && _settings.Left is double l && _settings.Top is double t &&
            l + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            t + 50 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight &&
            l + w > SystemParameters.VirtualScreenLeft + 100 && t > SystemParameters.VirtualScreenTop - 20)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t; Width = w; Height = h;
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    void Window_Closing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        var b = RestoreBounds;
        if (!b.IsEmpty) { _settings.Left = b.Left; _settings.Top = b.Top; _settings.Width = b.Width; _settings.Height = b.Height; }
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        if (!_busy) _sampler.Dispose(); // a sample still running would use the buffer; the process exits anyway
    }
}
