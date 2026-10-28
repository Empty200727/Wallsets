namespace Wallsets;

internal static class SelfTests
{
    internal static void Run(string root)
    {
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add("PASS " + label); }
        try
        {
            var files = new List<string> { "1.mp4", "2.png", "10.mp4" };
            Check(Library.Reconcile(files, ["10.mp4", "gone.mp4", "1.mp4", "1.mp4"]).SequenceEqual(new[] { "10.mp4", "1.mp4", "2.png" }), "Manual order preserves new files and removes missing/duplicate files");
            var shuffled = new List<string>(files); Library.Shuffle(shuffled);
            Check(shuffled.Order().SequenceEqual(files.Order()), "Shuffle preserves every item");
            Check(Library.Reconcile(files, shuffled).SequenceEqual(shuffled), "Shuffle remains stable across rescans");
            Check(Library.CombinationKey(["one", "two"]) == Library.CombinationKey(["two", "one"]), "Combined order is keyed independently of selection order");
            var both = new List<string> { "one/1.mp4", "one/2.mp4", "two/1.mp4", "two/2.mp4" };
            List<string> combined = ["two/2.mp4", "one/1.mp4", "two/1.mp4", "one/2.mp4"];
            Check(Library.Reconcile(both, combined).SequenceEqual(combined), "Combined queue interleaves sets and preserves duplicate filenames in different sets");
            both.Remove("two/1.mp4"); both.Add("one/new.png");
            Check(Library.Reconcile(both, combined).SequenceEqual(new[] { "two/2.mp4", "one/1.mp4", "one/2.mp4", "one/new.png" }), "Combined queue handles additions and removals");
            // a is in the queue three times: two copies were put right after the clicked tile.
            var weighted = Library.InsertCopies(["a", "b", "c"], 0, "a", 2);
            Check(weighted.SequenceEqual(["a", "a", "a", "b", "c"]) && Library.Reconcile(["a", "a", "a", "b", "c"], ["b", "a", "c", "a", "a"]).SequenceEqual(["b", "a", "c", "a", "a"]), "More copies go right after the clicked tile and keep their places");
            Check(Library.Reconcile(["a", "b", "c"], ["b", "a", "c", "a", "a"]).SequenceEqual(["b", "a", "c"]), "Fewer copies are removed from the end of the queue");
            Check(Library.Reconcile(["a", "a", "b", "c", "d"], ["c", "a", "b"]).SequenceEqual(["c", "a", "a", "b", "d"]), "A missing copy goes after the last copy; a new file goes to the end");
            Check(Library.IsPicture("x.JPG") && !Library.IsPicture("x.gif") && !Library.IsPicture("x.mp4"), "Pictures use the interval also when videos change at their end");
            Check(Library.AdjacentIndex(0, 4, -1) == 3 && Library.AdjacentIndex(3, 4, 1) == 0 && Library.AdjacentIndex(0, 1, -1) == 0 && Library.AdjacentIndex(-1, 0, -1) == -1, "Previous and next wrap correctly including empty/single queues");
            var settingsFile = Path.Combine(Path.GetTempPath(), "wallsets-self-test-" + Environment.ProcessId + ".json");
            File.WriteAllText(settingsFile, "{\"SingleSet\":true,\"Selected\":[\"one\",\"two\",\"two\"],\"MusicVolume\":250,\"Music\":true,\"Scaling\":\"zoom\",\"Hotkeys\":null,\"MaxFps\":45}");
            var loaded = Settings.Load(settingsFile); File.Delete(settingsFile);
            Check(loaded.SingleSet && loaded.Selected.SequenceEqual(["two"]) && loaded.Music && loaded.MusicVolume == 100, "Single-set mode keeps one ticked set; music volume is limited to 0-100%");
            Check(loaded.Scaling == "fill" && loaded.Hotkeys.Count == 0 && !loaded.ShowNames && !loaded.ShowSetNames, "Unknown scaling falls back to fill; missing hotkeys use defaults; missing caption options stay off");
            Check(loaded.MaxFps == 30 && loaded.Optimize, "Unknown frame-rate limit falls back to 30; lighter copies are on unless switched off");
            var hotkey = Hotkey.Parse("Ctrl+Alt+P");
            Check(hotkey.ToString() == "Ctrl+Alt+P" && hotkey.Modifiers == 3 && hotkey.Code == Keys.P && Hotkey.Parse(hotkey.ToString()) == hotkey
                && Hotkey.Parse("Ctrl+Shift+F5").IsValid && Hotkey.Parse("F9").IsValid && Hotkey.Parse("P").IsEmpty && Hotkey.Parse("Shift+P").IsEmpty
                && Hotkey.Parse("Win+Alt+X").Modifiers == 9 && Hotkey.Parse("Hyper+Q").IsEmpty && Hotkey.Parse("").IsEmpty, "Hotkeys: combinations are stored, read back and checked (a plain letter is refused)");
            Check(HotkeyActions.All.Select(a => Hotkey.Parse(a.Default)).Where(k => k.IsValid).Distinct().Count() == HotkeyActions.All.Length, "Default hotkeys are valid and all different");
            var tiled = Player.FilterChain(Player.TileGraph(500, 300, 1920, 1080));
            var graph = tiled[(tiled.IndexOf('%', tiled.IndexOf('%') + 1) + 1)..];
            Check(tiled.StartsWith("lavfi=graph=%" + System.Text.Encoding.UTF8.GetByteCount(graph) + "%") && graph.Contains("hstack=inputs=4") && graph.Contains("vstack=inputs=4") && graph.EndsWith("crop=1920:1080:0:0")
                && Player.FilterChain(Player.TileGraph(1920, 1080, 1920, 1080)) == "lavfi=graph=%18%crop=1920:1080:0:0", "Tile: a small picture is repeated and cut to the screen size");
            Check(Player.FrameStep(120, 30) == 4 && Player.FrameStep(60, 30) == 2 && Player.FrameStep(59.94, 30) == 2 && Player.FrameStep(50, 30) == 2 && Player.FrameStep(30, 30) == 1
                && Player.FrameStep(24, 30) == 1 && Player.FrameStep(60.5, 60) == 1 && Player.FrameStep(144, 60) == 3 && Player.FrameStep(120, 0) == 1 && Player.FrameStep(0, 30) == 1,
                "Frame-rate limit keeps every n-th frame: 120 -> 30, 60 -> 30, 50 -> 25; slower files and pictures are left as they are");
            Check(Player.FilterChain("", "") == "" && Player.FilterChain("framestep=4", "crop=1920:1080:0:0") == "lavfi=graph=%30%framestep=4,crop=1920:1080:0:0", "Frame-rate limit and tile go into one filter");
            var screen = new Size(1920, 1080);
            Check(Optimizer.PlanFor(3840, 2160, 120, screen, 30) == new Optimizer.Plan(1920, 1080, 4) && Optimizer.PlanFor(1920, 1080, 30, screen, 30) == null
                && Optimizer.PlanFor(1920, 1080, 60, screen, 30) == new Optimizer.Plan(1920, 1080, 2) && Optimizer.PlanFor(3840, 2160, 60, screen, 0) == new Optimizer.Plan(1920, 1080, 1)
                && Optimizer.PlanFor(2560, 1440, 30, new Size(1920, 1200), 30) == new Optimizer.Plan(2134, 1200, 1) && Optimizer.PlanFor(2048, 1152, 30, screen, 30) == null
                && Optimizer.PlanFor(1366, 768, 30, screen, 30) == null && Optimizer.PlanFor(0, 0, 30, screen, 30) == null,
                "Lighter copies: scaled to cover the screen and to the frame-rate limit; files that are already light get none");
            var box = (string type, int size) => { var b = new byte[size]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, (uint)size); System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4); return b; };
            var mp4 = Path.Combine(Path.GetTempPath(), "wallsets-self-test-" + Environment.ProcessId + ".mp4");
            File.WriteAllBytes(mp4, [.. box("ftyp", 24), .. box("moov", 40), .. box("mdat", 100)]);
            bool whole = Optimizer.Complete(mp4);
            File.WriteAllBytes(mp4, [.. box("ftyp", 24), .. box("mdat", 100)]);
            bool noIndex = Optimizer.Complete(mp4);
            File.WriteAllBytes(mp4, [.. box("ftyp", 24), .. box("moov", 40), .. box("mdat", 100)[..60]]);
            bool cut = Optimizer.Complete(mp4);
            File.Delete(mp4);
            Check(whole && !noIndex && !cut, "A lighter copy counts only as a finished MP4: not without its index, not cut off");
            var square = new Rectangle(0, 0, 1000, 1000);
            Check(WallpaperLayout.Place("fill", new Size(1920, 1080), square).SequenceEqual([new Rectangle(-389, 0, 1778, 1000)])
                && WallpaperLayout.Place("fit", new Size(1920, 1080), square).SequenceEqual([new Rectangle(0, 219, 1000, 562)])
                && WallpaperLayout.Place("stretch", new Size(1920, 1080), square).SequenceEqual([square])
                && WallpaperLayout.Place("tile", new Size(300, 400), square).Count == 12 && WallpaperLayout.Place("tile", new Size(1200, 1100), square).SequenceEqual([new Rectangle(0, 0, 1200, 1100)]),
                "Crop preview places the picture like the player for every scaling mode");
            var area = new ScreenArea { Left = 100, Top = 50, Right = 1820, Bottom = 1030 };
            Check(area.Valid && area.On(new Rectangle(0, 0, 1920, 1080)) == Rectangle.FromLTRB(100, 50, 1820, 1030) && !new ScreenArea { Right = 10, Bottom = 1080 }.Valid
                && Player.Box(new Rectangle(0, 0, 1920, 1080), new Rectangle(1910, 0, 500, 500)) == new Rectangle(0, 0, 1920, 1080), "Wallpaper area: coordinates, too small areas and areas off the screen");
            var fresh = new Settings();
            Check(fresh.Music && fresh.MusicVolume == 60 && fresh.IntervalSeconds == 50 && !fresh.ShowNames && !fresh.ShowSetNames && fresh.AutoPause && !fresh.PauseOnAnyWindow && !fresh.SingleSet && !fresh.ByVideoLength
                && fresh.MaxFps == 30 && fresh.Optimize, "Fresh install: sound on at 60%, change every 50 s, captions off, pause behind maximized windows on, at most 30 frames per second, heavy videos lightened");
            bool sizes = true;
            foreach (var width in new[] { 980, 1550, 2200, 3400 })
                for (int size = 0; size < ThumbnailGrid.ColumnsWhenMaximized.Length; size++)
                {
                    int wanted = ThumbnailGrid.ColumnsWhenMaximized[size], gap = 10, tile = ThumbnailGrid.TileWidthFor(width, wanted, gap, 72);
                    sizes &= tile == 72 || (width - gap) / (tile + gap) == wanted;
                }
            Check(sizes && ThumbnailGrid.ColumnsWhenMaximized[1] is >= 8 and <= 10, "Medium thumbnails: 9 per row in a maximized window; every size keeps its column count");
            var scan = Library.Scan(Path.Combine(root, "Наборы"));
            Check(scan.Count >= 1 && scan.Sum(s => s.Files.Count) >= 1, $"Library: {scan.Count} sets, {scan.Sum(s => s.Files.Count)} files");
            Check(scan.SelectMany(s => s.Files).All(f => File.Exists(f.Path)), "Unicode media paths exist");
            Check(scan.Select(s => s.Id).Distinct().Count() == scan.Count && scan.All(s => s.Files.Select(f => f.Key).Distinct().Count() == s.Files.Count), "Sets and files have unique ids");
            var rescan = Library.Scan(Path.Combine(root, "Наборы"));
            Check(rescan.Select(s => s.Id).SequenceEqual(scan.Select(s => s.Id)) && rescan.SelectMany(s => s.Files.Select(f => f.Key)).SequenceEqual(scan.SelectMany(s => s.Files.Select(f => f.Key))), "Set and file ids are the same on the next scan");
            File.WriteAllLines(Path.Combine(root, "self-test.txt"), checks);
        }
        catch (Exception ex) { checks.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(root, "self-test.txt"), checks); Environment.ExitCode = 1; }
    }
}
