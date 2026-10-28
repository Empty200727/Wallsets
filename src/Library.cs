using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Wallsets;

// Order of one set or of a combination of sets. Entries are file keys (set id / file id), so renaming
// a set or a file keeps the order; plain relative paths from older versions are converted on load.
public sealed class SetOptions
{
    // default, manual, shuffle or "saved:<id>" (a saved queue).
    public string Sort { get; set; } = "default";
    public List<string> Order { get; set; } = [];
    // File id -> how many times the file is in the queue; a missing file id means once.
    public Dictionary<string, int> Counts { get; set; } = new();
    // File ids that play with sound even when the sound of all wallpapers is off.
    public List<string> Sound { get; set; } = [];
}

// A queue saved under a name; it is offered in "Порядок" when exactly the same sets are ticked.
public sealed class SavedQueue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public List<string> Sets { get; set; } = [];
    public List<string> Order { get; set; } = [];
}

// Part of the primary screen for the wallpaper, in pixels from the screen's top left corner.
public sealed class ScreenArea
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public const int Smallest = 64;
    public bool Valid => Left >= 0 && Top >= 0 && Right - Left >= Smallest && Bottom - Top >= Smallest;
    public Rectangle On(Rectangle screen) => Rectangle.Intersect(Rectangle.FromLTRB(screen.Left + Left, screen.Top + Top, screen.Left + Right, screen.Top + Bottom), screen);
}

public sealed class Settings
{
    public List<string> Selected { get; set; } = [];
    // Defaults of a fresh install: change every 50 s, sound on at 60%, captions off, energy saving on.
    public double IntervalSeconds { get; set; } = 50;
    public bool AutoPause { get; set; } = true;
    // Videos change when they have played to the end; pictures still use IntervalSeconds.
    public bool ByVideoLength { get; set; }
    public bool Floating { get; set; }
    public int ThumbnailSize { get; set; } = 1;
    public bool SingleSet { get; set; }
    public bool Music { get; set; } = true;
    public int MusicVolume { get; set; } = 60;
    public bool ShowNames { get; set; }
    public bool ShowSetNames { get; set; }
    // How the wallpaper fits the screen: fit, stretch, fill or tile.
    public string Scaling { get; set; } = "fill";
    // Null: the wallpaper covers the whole screen.
    public ScreenArea? Area { get; set; }
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
    // Keyed by set id (older versions used the folder name; such keys are moved on the first scan).
    public Dictionary<string, SetOptions> Sets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SavedQueue> Queues { get; set; } = [];
    // The wallpaper on screen, so it comes back after a restart: file key, which of its copies, and the
    // path under "Наборы" in case the key no longer matches.
    public string? CurrentFile { get; set; }
    public int CurrentCopy { get; set; }
    public string? CurrentPath { get; set; }
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
            s.Queues = (s.Queues ?? []).Where(q => q != null && !string.IsNullOrEmpty(q.Id)).ToList();
            foreach (var q in s.Queues) { q.Name ??= ""; q.Sets ??= []; q.Order ??= []; }
            foreach (var options in new[] { s.Sets, s.Combinations })
                foreach (var (key, o) in options.ToList())
                {
                    if (o == null) { options.Remove(key); continue; }
                    o.Sort ??= "default"; o.Order ??= []; o.Counts ??= new(); o.Sound ??= [];
                }
            s.ThumbnailSize = Math.Clamp(s.ThumbnailSize, 0, 3);
            s.MusicVolume = Math.Clamp(s.MusicVolume, 0, 100);
            if (!ScalingModes.Contains(s.Scaling)) s.Scaling = "fill";
            s.Hotkeys ??= new();
            if (s.Area is { Valid: false }) s.Area = null;
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

// Key is unique within the library and does not change when the set or the file is renamed.
public sealed record MediaFile(string SetId, string SetName, string FileId, string Path, string Relative)
{
    public string Key => SetId + "/" + FileId;
}

public sealed record MediaSet(string Id, string Name, string Directory, List<MediaFile> Files)
{
    public override string ToString() => $"{Name}   ({Files.Count})";
}

public static class Library
{
    static readonly HashSet<string> Extensions = new(".mp4 .mkv .webm .mov .avi .m4v .wmv .mpg .mpeg .ts .gif .jpg .jpeg .png .bmp .webp .tif .tiff .avif .heic".Split(' '), StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Pictures = new(".jpg .jpeg .png .bmp .webp .tif .tiff .avif .heic".Split(' '), StringComparer.OrdinalIgnoreCase);
    // Hidden file in every set folder with the set's id: it moves with the folder when it is renamed.
    public const string Marker = ".wallsets";
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);
    public static bool IsPicture(string path) => Pictures.Contains(System.IO.Path.GetExtension(path));
    public static List<MediaSet> Scan(string root)
    {
        var result = new List<MediaSet>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in System.IO.Directory.GetDirectories(root).OrderBy(d => Path.GetFileName(d) == "Первый набор" ? 0 : Path.GetFileName(d) == "Второй набор" ? 1 : 2).ThenBy(d => Path.GetFileName(d)!, Comparer<string>.Create(StrCmpLogicalW)))
        {
            try
            {
                var name = Path.GetFileName(dir);
                var id = SetId(dir, ids);
                var files = new List<MediaFile>();
                var fileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in System.IO.Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                    .Where(f => Extensions.Contains(Path.GetExtension(f))).OrderBy(f => Path.GetRelativePath(dir, f), Comparer<string>.Create(StrCmpLogicalW)))
                {
                    var relative = Path.GetRelativePath(dir, file);
                    // Two identical copies in one set get different ids.
                    var baseId = Fingerprints.Get(file) ?? "path:" + relative.ToLowerInvariant();
                    var fileId = baseId;
                    for (int n = 2; !fileIds.Add(fileId); n++) fileId = baseId + "~" + n;
                    files.Add(new(id, name, fileId, file, relative));
                }
                result.Add(new(id, name, dir, files));
            }
            catch (IOException) { }
        }
        Fingerprints.Flush();
        return result;
    }
    // Reads the set's id from its marker file or creates one. A copied set folder carries the same id:
    // the second folder found gets a new one. A read-only folder falls back to its name.
    static string SetId(string dir, HashSet<string> taken)
    {
        var marker = Path.Combine(dir, Marker);
        try
        {
            var id = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
            if (id.Length is > 0 and <= 64 && id.All(char.IsAsciiLetterOrDigit) && taken.Add(id)) return id;
            id = Guid.NewGuid().ToString("N")[..12];
            if (File.Exists(marker)) File.SetAttributes(marker, FileAttributes.Normal);
            File.WriteAllText(marker, id);
            File.SetAttributes(marker, FileAttributes.Hidden);
            taken.Add(id);
            return id;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var id = "name:" + Path.GetFileName(dir);
            taken.Add(id);
            return id;
        }
    }
    // Arranges the files by a saved order. Every key appears as many times as in available: copies the
    // order has too many of are dropped from the end, missing copies go right after the key's last copy,
    // and new files go to the end.
    public static List<string> Reconcile(List<string> available, IEnumerable<string> order)
    {
        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in available) wanted[key] = wanted.GetValueOrDefault(key) + 1;
        var placed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var key in order ?? [])
            if (wanted.TryGetValue(key, out var n) && placed.GetValueOrDefault(key) < n) { result.Add(key); placed[key] = placed.GetValueOrDefault(key) + 1; }
        foreach (var key in available)
        {
            if (placed.GetValueOrDefault(key) >= wanted[key]) continue;
            int last = result.FindLastIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            if (last >= 0) result.Insert(last + 1, key); else result.Add(key);
            placed[key] = placed.GetValueOrDefault(key) + 1;
        }
        return result;
    }
    // Inserts copies of the key right after position index.
    public static List<string> InsertCopies(List<string> order, int index, string key, int copies)
    {
        var result = new List<string>(order);
        result.InsertRange(Math.Clamp(index + 1, 0, result.Count), Enumerable.Repeat(key, Math.Max(0, copies)));
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

// A file's id comes from its size and the first and last 64 KB, so it stays the same when the file
// or its folder is renamed. Ids are kept between runs by file name, size and modification time,
// so a rescan reads nothing new for files that did not change.
public static class Fingerprints
{
    const int Chunk = 65536;
    static readonly object gate = new();
    static Dictionary<string, string>? known;
    static readonly Dictionary<string, string> seen = new(StringComparer.OrdinalIgnoreCase);
    static bool changed;
    static string CachePath => Path.Combine(Program.DataFolder("index"), "files.json");
    public static string? Get(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var key = $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            lock (gate)
            {
                known ??= Load();
                if (known.TryGetValue(key, out var id)) { seen[key] = id; return id; }
            }
            var computed = Compute(path, info.Length);
            lock (gate) { known[key] = seen[key] = computed; changed = true; }
            return computed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
    public static string Compute(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(length));
        var buffer = new byte[Chunk];
        hash.AppendData(buffer, 0, stream.ReadAtLeast(buffer, Chunk, false));
        if (length > Chunk)
        {
            stream.Seek(Math.Max(Chunk, length - Chunk), SeekOrigin.Begin);
            hash.AppendData(buffer, 0, stream.ReadAtLeast(buffer, Chunk, false));
        }
        return Convert.ToHexString(hash.GetHashAndReset(), 0, 8).ToLowerInvariant();
    }
    // Saves the ids of the files found by the last scan.
    public static void Flush()
    {
        lock (gate)
        {
            if (known == null) return;
            if (changed || seen.Count != known.Count)
            {
                try
                {
                    var path = CachePath;
                    File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(seen));
                    File.Move(path + ".tmp", path, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                known = new(seen, StringComparer.OrdinalIgnoreCase);
            }
            seen.Clear(); changed = false;
        }
    }
    static Dictionary<string, string> Load()
    {
        try { return new(JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CachePath)) ?? [], StringComparer.OrdinalIgnoreCase); }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }
}
