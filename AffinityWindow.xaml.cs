using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TaskScope;

/// <summary>Per-processor checkboxes for choosing where a process may run, grouped by physical core.</summary>
public partial class AffinityWindow : Window
{
    readonly Affinity _current;
    readonly CpuTopology _topology;
    readonly Dictionary<int, CheckBox> _boxes = new(); // bit within the group → checkbox

    /// <summary>The chosen mask (bits within the process's processor group), set when the dialog returns true.</summary>
    public ulong Mask { get; private set; }

    public AffinityWindow(string who, Affinity current, CpuTopology topology, string? extraNote)
    {
        InitializeComponent();
        _current = current;
        _topology = topology;
        int offset = current.Offset;

        Heading.Text = $"Which processors may {who} run on?";
        var hint = new List<string>
        {
            "Windows only runs it on the checked logical processors. The change lasts until the process exits.",
        };
        if (Affinity.GroupCount > 1)
            hint.Add($"This PC has {Affinity.GroupCount} processor groups. This process is in group {current.Group} " +
                     $"(CPU {Affinity.Ranges(current.GroupMask, offset)}), and Windows only lets its affinity be set within that group.");
        if (current.MultiGroup)
            hint.Add("It currently runs across more than one group; applying limits it to the processors below.");
        if (extraNote != null) hint.Add(extraNote);
        Hint.Text = string.Join(" ", hint);

        BuildBoxes(offset);
        BuildQuickButtons();
        UpdateCount();
    }

    /// <summary>Topology is only known for group 0 (the panel's core view has the same limit).</summary>
    bool HasCores => _current.Group == 0 && _topology.CoreThreads.Count > 0 &&
                     _boxes.Keys.All(b => b < _topology.CoreOfLogical.Length && _topology.CoreOfLogical[b] >= 0);

    void BuildBoxes(int offset)
    {
        for (int bit = 0; bit < 64; bit++)
        {
            if ((_current.GroupMask & (1UL << bit)) == 0) continue;
            var cb = new CheckBox
            {
                Content = $"CPU {bit + offset}",
                IsChecked = (_current.Mask & (1UL << bit)) != 0,
                MinWidth = 86, Margin = new Thickness(0, 0, 6, 0),
            };
            cb.Checked += (_, _) => UpdateCount();
            cb.Unchecked += (_, _) => UpdateCount();
            _boxes[bit] = cb;
        }

        if (!HasCores)
        {
            foreach (var cb in _boxes.Values) { cb.Margin = new Thickness(0, 0, 10, 6); CpuPanel.Children.Add(cb); }
            return;
        }

        // One card per physical core, so hyper-threading siblings and P/E cores are easy to tell apart.
        for (int c = 0; c < _topology.CoreThreads.Count; c++)
        {
            var bits = _topology.CoreThreads[c].Where(_boxes.ContainsKey).ToList();
            if (bits.Count == 0) continue;
            string kind = _topology.Hybrid ? (_topology.CoreIsEfficiency[c] ? " · E" : " · P") : "";
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = $"Core {c}{kind}", FontSize = 11, Opacity = 0.65, Margin = new Thickness(0, 0, 0, 4) });
            foreach (int b in bits) { _boxes[b].Margin = new Thickness(0, 0, 0, 2); stack.Children.Add(_boxes[b]); }
            var card = new Border
            {
                Child = stack, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 8, 8),
                ToolTip = _topology.Hybrid
                    ? (_topology.CoreIsEfficiency[c] ? "Efficiency core: slower, uses less power" : "Performance core")
                    : $"Physical core {c}",
            };
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            CpuPanel.Children.Add(card);
        }
    }

    void BuildQuickButtons()
    {
        AddQuick("All", "Every processor", _ => true);
        AddQuick("None", "Clear every box", _ => false);
        if (!HasCores) return;
        if (_topology.Hybrid)
        {
            AddQuick("P-cores only", "Only performance cores", b => !_topology.CoreIsEfficiency[_topology.CoreOfLogical[b]]);
            AddQuick("E-cores only", "Only efficiency cores", b => _topology.CoreIsEfficiency[_topology.CoreOfLogical[b]]);
        }
        if (_topology.CoreThreads.Any(t => t.Length > 1))
            AddQuick("One per core", "The first logical processor of each core, so no two threads share a core",
                b => _topology.CoreThreads[_topology.CoreOfLogical[b]][0] == b);
    }

    void AddQuick(string label, string tip, Func<int, bool> pick)
    {
        var btn = new Button { Content = label, ToolTip = tip, Margin = new Thickness(0, 0, 6, 0) };
        btn.Click += (_, _) => { foreach (var (bit, cb) in _boxes) cb.IsChecked = pick(bit); };
        QuickButtons.Children.Add(btn);
    }

    ulong Selected()
    {
        ulong m = 0;
        foreach (var (bit, cb) in _boxes) if (cb.IsChecked == true) m |= 1UL << bit;
        return m;
    }

    void UpdateCount()
    {
        ulong m = Selected();
        int n = Affinity.Count(m), total = _boxes.Count;
        CountText.Text = n == 0 ? "Pick at least one processor."
            : n == total ? $"All {total} logical processors"
            : $"{n} of {total} logical processors";
        ApplyButton.IsEnabled = n > 0;
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        Mask = Selected();
        if (Mask == 0) return;
        DialogResult = true;
    }
}
