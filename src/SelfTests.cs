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
            var scan = Library.Scan(Path.Combine(root, "Наборы"));
            Check(scan.Count >= 2 && scan.Sum(s => s.Files.Count) >= 42, "Initial library: two sets, 42 videos");
            Check(scan.SelectMany(s => s.Files.Select(f => Path.Combine(s.Directory, f))).All(File.Exists), "Unicode media paths exist");
            File.WriteAllLines(Path.Combine(root, "self-test.txt"), checks);
        }
        catch (Exception ex) { checks.Add("FAIL " + ex); File.WriteAllLines(Path.Combine(root, "self-test.txt"), checks); Environment.ExitCode = 1; }
    }
}
