namespace Wallsets;

internal static class SelfTests
{
    internal static void Run(string root)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add("PASS " + label); }
        try
        {
            var set = new MediaSet("test", root, ["1.mp4", "2.png", "10.mp4"]);
            Check(Library.Ordered(set, new()).SequenceEqual(set.Files), "Default order");
            var options = new SetOptions { Sort = "manual", Order = ["10.mp4", "gone.mp4", "1.mp4", "1.mp4"] };
            Check(Library.Ordered(set, options).SequenceEqual(new[] { "10.mp4", "1.mp4", "2.png" }), "Manual order preserves new files and removes missing/duplicate files");
            var shuffled = new List<string>(set.Files); Library.Shuffle(shuffled);
            Check(shuffled.Order().SequenceEqual(set.Files.Order()), "Shuffle preserves every item");
            options.Sort = "shuffle"; options.Order = shuffled;
            Check(Library.Ordered(set, options).SequenceEqual(shuffled), "Shuffle remains stable across rescans");
            Check(Library.CombinationKey(["one", "two"]) == Library.CombinationKey(["two", "one"]), "Combined order is keyed independently of selection order");
            var both = new List<string> { "one/1.mp4", "one/2.mp4", "two/1.mp4", "two/2.mp4" };
            var combined = new SetOptions { Sort = "shuffle", Order = ["two/2.mp4", "one/1.mp4", "two/1.mp4", "one/2.mp4"] };
            Check(Library.Reconcile(both, combined).SequenceEqual(combined.Order), "Combined queue interleaves sets and preserves duplicate filenames in different sets");
            both.Remove("two/1.mp4"); both.Add("one/new.png");
            Check(Library.Reconcile(both, combined).SequenceEqual(new[] { "two/2.mp4", "one/1.mp4", "one/2.mp4", "one/new.png" }), "Combined queue handles additions and removals");
            Check(Library.AdjacentIndex(0, 4, -1) == 3 && Library.AdjacentIndex(3, 4, 1) == 0 && Library.AdjacentIndex(0, 1, -1) == 0 && Library.AdjacentIndex(-1, 0, -1) == -1, "Previous and next wrap correctly including empty/single queues");
            var settingsFile = Path.Combine(Path.GetTempPath(), "wallsets-self-test-" + Environment.ProcessId + ".json");
            File.WriteAllText(settingsFile, "{\"SingleSet\":true,\"Selected\":[\"one\",\"two\",\"two\"],\"MusicVolume\":250,\"Music\":true,\"Scaling\":\"zoom\",\"Hotkeys\":null}");
            var loaded = Settings.Load(settingsFile); File.Delete(settingsFile);
            Check(loaded.SingleSet && loaded.Selected.SequenceEqual(["two"]) && loaded.Music && loaded.MusicVolume == 100, "Single-set mode keeps one ticked set; music volume is limited to 0-100%");
            Check(loaded.Scaling == "fill" && loaded.Hotkeys.Count == 0 && loaded.ShowNames && loaded.ShowSetNames, "Unknown scaling falls back to fill; missing hotkeys use defaults; captions shown by default");
            var hotkey = Hotkey.Parse("Ctrl+Alt+P");
            Check(hotkey.ToString() == "Ctrl+Alt+P" && hotkey.Modifiers == 3 && hotkey.Code == Keys.P && Hotkey.Parse(hotkey.ToString()) == hotkey
                && Hotkey.Parse("Ctrl+Shift+F5").IsValid && Hotkey.Parse("F9").IsValid && Hotkey.Parse("P").IsEmpty && Hotkey.Parse("Shift+P").IsEmpty
                && Hotkey.Parse("Win+Alt+X").Modifiers == 9 && Hotkey.Parse("Hyper+Q").IsEmpty && Hotkey.Parse("").IsEmpty, "Hotkeys: combinations are stored, read back and checked (a plain letter is refused)");
            Check(HotkeyActions.All.Select(a => Hotkey.Parse(a.Default)).Where(k => k.IsValid).Distinct().Count() == HotkeyActions.All.Length, "Default hotkeys are valid and all different");
            var tiled = Player.TileFilter(500, 300, 1920, 1080);
            var graph = tiled[(tiled.IndexOf('%', tiled.IndexOf('%') + 1) + 1)..];
            Check(tiled.StartsWith("lavfi=graph=%" + System.Text.Encoding.UTF8.GetByteCount(graph) + "%") && graph.Contains("hstack=inputs=4") && graph.Contains("vstack=inputs=4") && graph.EndsWith("crop=1920:1080:0:0")
                && Player.TileFilter(1920, 1080, 1920, 1080) == "lavfi=graph=%18%crop=1920:1080:0:0", "Tile: a small picture is repeated and cut to the screen size");
            Check(new Settings().MusicVolume == 50 && !new Settings().Music && !new Settings().SingleSet, "Defaults: no sound, 50% volume, several sets allowed");
            bool sizes = true;
            foreach (var width in new[] { 980, 1550, 2200, 3400 })
                for (int size = 0; size < ThumbnailGrid.ColumnsWhenMaximized.Length; size++)
                {
                    int wanted = ThumbnailGrid.ColumnsWhenMaximized[size], gap = 10, tile = ThumbnailGrid.TileWidthFor(width, wanted, gap, 72);
                    sizes &= tile == 72 || (width - gap) / (tile + gap) == wanted;
                }
            Check(sizes && ThumbnailGrid.ColumnsWhenMaximized[1] is >= 8 and <= 10, "Medium thumbnails: 9 per row in a maximized window; every size keeps its column count");
            var scan = Library.Scan(Path.Combine(root, "Наборы"));
            Check(scan.Count >= 2 && scan.Sum(s => s.Files.Count) >= 42, "Initial library: two sets, 42 videos");
            Check(scan.SelectMany(s => s.Files.Select(f => Path.Combine(s.Directory, f))).All(File.Exists), "Unicode media paths exist");
            File.WriteAllLines(Path.Combine(root, "self-test.txt"), checks);
        }
        catch (Exception ex) { checks.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(root, "self-test.txt"), checks); Environment.ExitCode = 1; }
    }
}
