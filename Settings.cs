using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace TaskScope;

public sealed class WatchRule
{
    public string Image { get; set; } = "";
    public double LimitGB { get; set; } = 8;
    /// <summary>"Notify", "Suspend" or "End".</summary>
    public string Action { get; set; } = "Notify";

    public bool Matches(string imageName) =>
        string.Equals(Image, imageName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Image + ".exe", imageName, StringComparison.OrdinalIgnoreCase);

    public WatchRule Clone() => new() { Image = Image, LimitGB = LimitGB, Action = Action };
}

public sealed class Settings
{
    public double CommitWarnGB { get; set; } = 16;
    public int IntervalMs { get; set; } = 1000;
    public bool GroupByApp { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool StackedGraphs { get; set; }
    /// <summary>Which tile's detail panel is open: cpu, mem, commit, gpu, disk, or null.</summary>
    public string? OpenPanel { get; set; }
    public string CpuView { get; set; } = "threads";
    public string GpuView { get; set; } = "overall";
    public string SortKey { get; set; } = "Private";
    public bool SortDesc { get; set; } = true;
    public List<WatchRule> Watches { get; set; } = new();
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TaskScope");
    static string FilePath => Path.Combine(Folder, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* not fatal */ }
    }
}
