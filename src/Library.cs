using System.Text.Json;
using System.Runtime.InteropServices;

namespace Wallsets;

public sealed class SetOptions
{
    public string Sort { get; set; } = "default";
    public List<string> Order { get; set; } = [];
}

public sealed class Settings
{
    public List<string> Selected { get; set; } = [];
    public double IntervalSeconds { get; set; } = 300;
    public bool AutoPause { get; set; } = true;
    public bool PauseOnBattery { get; set; }
    public bool Floating { get; set; }
    public int ThumbnailSize { get; set; } = 1;
    public bool SingleSet { get; set; }
    public bool Music { get; set; }
    public int MusicVolume { get; set; } = 50;
    public bool ShowNames { get; set; } = true;
    public bool ShowSetNames { get; set; } = true;
    // How the wallpaper fits the screen: fit, stretch, fill or tile.
    public string Scaling { get; set; } = "fill";
    // Action -> combination such as "Ctrl+Alt+P"; an empty string switches the hotkey off.
    public Dictionary<string, string> Hotkeys { get; set; } = new();
    // Put the last wallpaper frame as the Windows background when the session ends, so the next boot starts with it.
    public bool WindowsFrame { get; set; } = true;
    public bool FrameApplied { get; set; }
    public string? OriginalWallpaper { get; set; }
    public string? OriginalWallpaperStyle { get; set; }
    public string? OriginalTileWallpaper { get; set; }
    public static readonly string[] ScalingModes = ["fit", "stretch", "fill", "tile"];
    public Dictionary<string, SetOptions> Combinations { get; set; } = new();
    public Dictionary<string, SetOptions> Sets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public static Settings Load(string path)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
            s.IntervalSeconds = Math.Clamp(s.IntervalSeconds, 5, 86400);
            s.Selected = (s.Selected ?? []).Distinct().ToList();
            if (s.SingleSet && s.Selected.Count > 1) s.Selected = [s.Selected[^1]];
            s.Sets ??= new(StringComparer.OrdinalIgnoreCase);
            s.Combinations ??= new();
            s.ThumbnailSize = Math.Clamp(s.ThumbnailSize, 0, 3);
            s.MusicVolume = Math.Clamp(s.MusicVolume, 0, 100);
            if (!ScalingModes.Contains(s.Scaling)) s.Scaling = "fill";
            s.Hotkeys ??= new();
            return s;
        }
        catch { return new(); }
    }
    public void Save(string path)
    {
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public sealed record MediaSet(string Name, string Directory, List<string> Files)
{
    public override string ToString() => $"{Name}   ({Files.Count})";
}

public static class Library
{
    static readonly HashSet<string> Extensions = new(".mp4 .mkv .webm .mov .avi .m4v .wmv .mpg .mpeg .ts .gif .jpg .jpeg .png .bmp .webp .tif .tiff .avif .heic".Split(' '), StringComparer.OrdinalIgnoreCase);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    public static List<MediaSet> Scan(string root)
    {
        var result = new List<MediaSet>();
        foreach (var dir in System.IO.Directory.GetDirectories(root).OrderBy(d => Path.GetFileName(d) == "Первый набор" ? 0 : Path.GetFileName(d) == "Второй набор" ? 1 : 2).ThenBy(d => Path.GetFileName(d)!, Comparer<string>.Create(StrCmpLogicalW)))
        {
            try
            {
                var files = System.IO.Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                    .Where(f => Extensions.Contains(Path.GetExtension(f))).Select(f => Path.GetRelativePath(dir, f))
                    .OrderBy(f => f, Comparer<string>.Create(StrCmpLogicalW)).ToList();
                result.Add(new(Path.GetFileName(dir), dir, files));
            }
            catch (IOException) { }
        }
        return result;
    }
    public static List<string> Ordered(MediaSet set, SetOptions options)
    {
        return Reconcile(set.Files, options);
    }
    public static List<string> Reconcile(List<string> files, SetOptions options)
    {
        if (options.Sort == "default") return [..files];
        var available = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = (options.Order ?? []).Where(available.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = result.ToHashSet(StringComparer.OrdinalIgnoreCase);
        result.AddRange(files.Where(f => !existing.Contains(f)));
        return result;
    }
    public static string CombinationKey(IEnumerable<string> names) => JsonSerializer.Serialize(names.Order(StringComparer.OrdinalIgnoreCase));
    public static int AdjacentIndex(int current, int count, int direction) => count == 0 ? -1 : (current < 0 ? (direction < 0 ? count - 1 : 0) : (current + direction + count) % count);
    public static void Shuffle(List<string> files)
    {
        for (int i = files.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (files[i], files[j]) = (files[j], files[i]);
        }
    }
}
