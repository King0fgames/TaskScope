using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TaskScope;

/// <summary>
/// Series colors for stacked graphs: the validated categorical palette (slots in fixed order),
/// stepped separately for light and dark surfaces, plus a muted gray for "Other".
/// </summary>
public static class ChartPalette
{
    public static readonly bool Dark = IsDarkTheme();
    static readonly Color[] LightSlots = { C("#2a78d6"), C("#eb6834"), C("#1baf7a"), C("#eda100") };
    static readonly Color[] DarkSlots = { C("#3987e5"), C("#d95926"), C("#199e70"), C("#c98500") };
    static readonly Brush[] SlotBrushes = (Dark ? DarkSlots : LightSlots).Select(Frozen).ToArray();
    public const int SlotCount = 4;
    public static readonly Brush Other = Frozen(Color.FromArgb(0x99, 0x89, 0x87, 0x81));
    public static readonly Brush Surface = Frozen(Dark ? C("#2b2b2b") : C("#fbfbfb"));
    public static readonly Brush Grid = Frozen(Dark ? C("#3a3a38") : C("#e1e0d9"));

    public static Brush Slot(int i) => SlotBrushes[Math.Clamp(i, 0, SlotCount - 1)];

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    static bool IsDarkTheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }
}

/// <summary>
/// History of a total plus its parts (e.g. CPU % per app). The biggest parts over the visible window
/// become colored series; each keeps its color while it stays in the top (color follows the entity).
/// </summary>
public sealed class StackedHistory
{
    const int KeepPerFrame = 12;
    readonly List<double> _totals = new();
    readonly List<Dictionary<string, double>> _parts = new();
    readonly Dictionary<string, int> _slots = new();
    readonly Dictionary<string, int>? _fixedSlots;

    public int Capacity { get; }
    public Dictionary<string, string> Labels { get; } = new();
    /// <summary>Use another history's series and colors (so one app has the same color on every GPU).</summary>
    public StackedHistory? SlotSource { get; init; }

    /// <param name="fixedSeries">Fixed series in slot order (e.g. "User", "Kernel"); null = top apps.</param>
    public StackedHistory(int capacity, params string[]? fixedSeries)
    {
        Capacity = capacity;
        if (fixedSeries is { Length: > 0 })
            _fixedSlots = fixedSeries.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
    }

    public int Count => _totals.Count;
    public double Total(int i) => _totals[i];
    public double Part(int i, string key) => _parts[i].GetValueOrDefault(key);
    public double LastTotal => _totals.Count == 0 ? 0 : _totals[^1];
    public double LastPart(string key) => _parts.Count == 0 ? 0 : _parts[^1].GetValueOrDefault(key);
    public IEnumerable<KeyValuePair<string, double>> LastParts =>
        _parts.Count == 0 ? Enumerable.Empty<KeyValuePair<string, double>>() : _parts[^1];

    public void Add(double total, Dictionary<string, double> parts)
    {
        if (_fixedSlots == null && parts.Count > KeepPerFrame)
            parts = parts.OrderByDescending(p => p.Value).Take(KeepPerFrame).ToDictionary(p => p.Key, p => p.Value);
        _totals.Add(total);
        _parts.Add(parts);
        if (_totals.Count > Capacity) { _totals.RemoveAt(0); _parts.RemoveAt(0); }
    }

    /// <summary>The colored series, bottom to top, each with its palette slot.</summary>
    public List<(string key, int slot)> Series()
    {
        if (SlotSource != null) return SlotSource.Series();
        if (_fixedSlots != null) return _fixedSlots.OrderBy(x => x.Value).Select(x => (x.Key, x.Value)).ToList();

        var sums = new Dictionary<string, double>();
        foreach (var frame in _parts)
            foreach (var (k, v) in frame) sums[k] = sums.GetValueOrDefault(k) + v;
        var top = sums.Where(x => x.Value > 0).OrderByDescending(x => x.Value)
                      .Take(ChartPalette.SlotCount).Select(x => x.Key).ToList();

        foreach (var k in _slots.Keys.ToList()) if (!top.Contains(k)) _slots.Remove(k);
        foreach (var k in top)
        {
            if (_slots.ContainsKey(k)) continue;
            for (int s = 0; s < ChartPalette.SlotCount; s++)
                if (!_slots.ContainsValue(s)) { _slots[k] = s; break; }
        }
        // Stack in slot order so neighbouring bands are neighbouring palette slots (validated pairs).
        return top.Select(k => (k, _slots[k])).OrderBy(x => x.Item2).ToList();
    }

    public double Max()
    {
        double m = 0;
        for (int i = 0; i < _totals.Count; i++)
            m = Math.Max(m, Math.Max(_totals[i], _parts[i].Values.Sum()));
        return m;
    }

    public string Label(string key) => Labels.TryGetValue(key, out var l) ? l : key;
}

/// <summary>A history graph: one filled line, or (when Stacked) colored bands from a StackedHistory.</summary>
public sealed class Sparkline : FrameworkElement
{
    static FrameworkPropertyMetadata Render(object def) => new(def, FrameworkPropertyMetadataOptions.AffectsRender);

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), Render(Brushes.DodgerBlue));
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(nameof(Data), typeof(RingBuffer), typeof(Sparkline), Render(null!));
    public static readonly DependencyProperty StackProperty = DependencyProperty.Register(nameof(Stack), typeof(StackedHistory), typeof(Sparkline), Render(null!));
    public static readonly DependencyProperty StackedProperty = DependencyProperty.Register(nameof(Stacked), typeof(bool), typeof(Sparkline), Render(false));
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(Sparkline), Render(0));
    public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(nameof(FixedMax), typeof(double), typeof(Sparkline), Render(0.0));
    public static readonly DependencyProperty MinMaxProperty = DependencyProperty.Register(nameof(MinMax), typeof(double), typeof(Sparkline), Render(1.0));
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(Sparkline), Render(false));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public RingBuffer? Data { get => (RingBuffer?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public StackedHistory? Stack { get => (StackedHistory?)GetValue(StackProperty); set => SetValue(StackProperty, value); }
    public bool Stacked { get => (bool)GetValue(StackedProperty); set => SetValue(StackedProperty, value); }
    /// <summary>Bump to redraw after the data changed (data objects are mutated in place).</summary>
    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }
    /// <summary>Fixed top of the scale (e.g. 100 for percentages); 0 = auto-scale.</summary>
    public double FixedMax { get => (double)GetValue(FixedMaxProperty); set => SetValue(FixedMaxProperty, value); }
    /// <summary>Smallest top of the scale when auto-scaling, so idle noise stays flat.</summary>
    public double MinMax { get => (double)GetValue(MinMaxProperty); set => SetValue(MinMaxProperty, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (ShowGrid)
        {
            var pen = new Pen(ChartPalette.Grid, 1);
            for (int i = 1; i < 4; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            }
        }
        if (Stacked && Stack is { Count: >= 2 } s) RenderStacked(dc, w, h, s);
        else if (Data is { Count: >= 2 } d) RenderLine(dc, w, h, d);
    }

    void RenderLine(DrawingContext dc, double w, double h, RingBuffer d)
    {
        double max = FixedMax > 0 ? FixedMax : Math.Max(d.Max() * 1.1, MinMax);
        double step = w / (d.Capacity - 1);
        double x0 = w - (d.Count - 1) * step;
        Point P(int i) => new(x0 + i * step, h - 1 - Math.Clamp(d[i] / max, 0, 1) * (h - 2));

        var line = new StreamGeometry();
        using (var c = line.Open())
        {
            c.BeginFigure(P(0), false, false);
            for (int i = 1; i < d.Count; i++) c.LineTo(P(i), true, true);
        }
        line.Freeze();
        var fill = new StreamGeometry();
        using (var c = fill.Open())
        {
            c.BeginFigure(new Point(x0, h), true, true);
            for (int i = 0; i < d.Count; i++) c.LineTo(P(i), false, false);
            c.LineTo(new Point(w, h), false, false);
        }
        fill.Freeze();

        var stroke = Stroke;
        Brush fillBrush = stroke;
        if (stroke is SolidColorBrush sb)
        {
            fillBrush = new SolidColorBrush(Color.FromArgb(0x38, sb.Color.R, sb.Color.G, sb.Color.B));
            fillBrush.Freeze();
        }
        dc.DrawGeometry(fillBrush, null, fill);
        dc.DrawGeometry(null, new Pen(stroke, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }

    void RenderStacked(DrawingContext dc, double w, double h, StackedHistory s)
    {
        int n = s.Count;
        double max = FixedMax > 0 ? FixedMax : Math.Max(s.Max() * 1.1, MinMax);
        double step = w / (s.Capacity - 1);
        double x0 = w - (n - 1) * step;
        double Y(double v) => h - Math.Clamp(v / max, 0, 1) * h;

        var lower = new double[n];
        var upper = new double[n];
        var gap = new Pen(ChartPalette.Surface, h > 60 ? 2 : 1) { LineJoin = PenLineJoin.Round };

        void Band(Brush fill)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, Y(upper[0])), true, true);
                for (int i = 1; i < n; i++) c.LineTo(new Point(x0 + i * step, Y(upper[i])), false, false);
                for (int i = n - 1; i >= 0; i--) c.LineTo(new Point(x0 + i * step, Y(lower[i])), false, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, null, g);
        }
        void Edge()
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, Y(upper[0])), false, false);
                for (int i = 1; i < n; i++) c.LineTo(new Point(x0 + i * step, Y(upper[i])), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, gap, g);
        }

        var edges = new List<double[]>();
        foreach (var (key, slot) in s.Series())
        {
            for (int i = 0; i < n; i++) upper[i] = lower[i] + Math.Max(0, s.Part(i, key));
            Band(ChartPalette.Slot(slot));
            edges.Add((double[])upper.Clone());
            Array.Copy(upper, lower, n);
        }
        // Whatever the top series don't explain (other apps, kernel time, …) in muted gray.
        for (int i = 0; i < n; i++) upper[i] = Math.Max(lower[i], s.Total(i));
        Band(ChartPalette.Other);
        // Surface-colored gaps between bands so adjacent colors never touch.
        foreach (var e in edges) { Array.Copy(e, upper, n); Edge(); }
    }
}

/// <summary>One small graph in a grid (a CPU thread or core, a disk, a GPU).</summary>
public sealed class ChartCell : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public ChartCell(string key, StackedHistory stack) { Key = key; Stack = stack; }

    public string Key { get; }
    public RingBuffer Data { get; } = new(60);
    public StackedHistory Stack { get; }
    public double FixedMax { get; init; }
    public double MinMax { get; init; } = 1;
    public Brush Stroke { get; init; } = Brushes.DodgerBlue;
    public bool Seen;

    string _label = "", _sub = "", _value = "", _tip = "";
    bool _stacked, _highlight;
    int _version;
    public string Label { get => _label; set => Set(ref _label, value); }
    public string Sub { get => _sub; set { Set(ref _sub, value); PropertyChanged?.Invoke(this, new(nameof(SubVisibility))); } }
    public Visibility SubVisibility => _sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ValueText { get => _value; set => Set(ref _value, value); }
    public string Tip { get => _tip; set => Set(ref _tip, value); }
    public bool Stacked { get => _stacked; set => Set(ref _stacked, value); }
    /// <summary>Marks the cell, e.g. CPUs the selected process is allowed to run on.</summary>
    public bool Highlight { get => _highlight; set => Set(ref _highlight, value); }
    public int Version { get => _version; private set => Set(ref _version, value); }
    public void Bump() => Version++;
}

public sealed class LegendItem
{
    public Brush? Swatch { get; init; }
    public Visibility SwatchVisibility => Swatch == null ? Visibility.Collapsed : Visibility.Visible;
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
}
