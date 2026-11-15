using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TaskScope;

public partial class SettingsWindow : Window
{
    static readonly (string label, int ms)[] Intervals =
        { ("Fast (0.5 s)", 500), ("Normal (1 s)", 1000), ("Slow (2 s)", 2000), ("Very slow (5 s)", 5000) };

    readonly Settings _settings;
    readonly ObservableCollection<WatchRule> _watches;

    public SettingsWindow(Settings settings, WatchRule? newRule, SystemSample? sys)
    {
        InitializeComponent();
        _settings = settings;
        CommitBox.Text = settings.CommitWarnGB.ToString("0.#", CultureInfo.CurrentCulture);

        var mem = Native.MemoryStatus();
        double limit = mem.ullTotalPageFile / 1073741824.0, ram = mem.ullTotalPhys / 1073741824.0;
        CommitHint.Text = $"Your commit limit is {limit:0} GB ({ram:0} GB RAM + {Math.Max(0, limit - ram):0} GB page file). " +
                          "Windows refuses new memory once it's used up, which is when programs and drivers crash.";

        foreach (var (label, _) in Intervals) IntervalBox.Items.Add(label);
        int idx = Array.FindIndex(Intervals, i => i.ms >= settings.IntervalMs);
        IntervalBox.SelectedIndex = idx < 0 ? Intervals.Length - 1 : idx;

        ActionColumn.ItemsSource = new[] { "Notify", "Suspend", "End" };
        _watches = new ObservableCollection<WatchRule>(settings.Watches.Select(w => w.Clone()));
        if (newRule != null)
        {
            var existing = _watches.FirstOrDefault(w => w.Matches(newRule.Image));
            if (existing == null) _watches.Add(existing = newRule);
            Loaded += (_, _) => { WatchGrid.SelectedItem = existing; WatchGrid.ScrollIntoView(existing); };
        }
        WatchGrid.ItemsSource = _watches;
    }

    void Add_Click(object sender, RoutedEventArgs e)
    {
        var rule = new WatchRule { Image = "", LimitGB = 8, Action = "Notify" };
        _watches.Add(rule);
        WatchGrid.SelectedItem = rule;
        WatchGrid.CurrentCell = new DataGridCellInfo(rule, WatchGrid.Columns[0]);
        WatchGrid.Focus();
        WatchGrid.BeginEdit();
    }

    void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (WatchGrid.SelectedItem is WatchRule r) _watches.Remove(r);
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        WatchGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (!double.TryParse(CommitBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double gb) || gb < 0)
        {
            MessageBox.Show(this, "Enter the warning level as a number of GB, for example 16.", "TaskScope");
            CommitBox.Focus();
            return;
        }
        _settings.CommitWarnGB = gb;
        _settings.IntervalMs = Intervals[Math.Max(0, IntervalBox.SelectedIndex)].ms;
        _settings.Watches = _watches
            .Where(w => !string.IsNullOrWhiteSpace(w.Image) && w.LimitGB > 0)
            .Select(w => { w.Image = w.Image.Trim(); return w; })
            .ToList();
        DialogResult = true;
    }
}
