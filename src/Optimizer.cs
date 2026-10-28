using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wallsets;

// Lighter copies of heavy videos. A 4K video at 60-120 frames per second keeps the graphics card's video
// decoder busy even when the frame-rate limit drops most of its frames; a copy scaled down to the screen
// and to the limit is decoded several times faster. The bundled mpv encodes each copy once, with the
// graphics card's H.264 encoder, into .cache\optimized. The originals are not changed: the player takes the
// copy when it is ready, and a new copy is made when the file, the screen size or the limit changes.
internal sealed class Optimizer : IDisposable
{
    public sealed class Probe
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public double Fps { get; set; }
        // Turned or HDR video: a plain H.264 copy would show it wrong, so the original plays.
        public bool Keep { get; set; }
        public bool Failed { get; set; }
    }
    public readonly record struct Plan(int Width, int Height, int Step);
    static readonly HashSet<string> Videos = new(".mp4 .mkv .webm .mov .avi .m4v .wmv .mpg .mpeg .ts".Split(' '), StringComparer.OrdinalIgnoreCase);
    // Graphics card encoders first (NVIDIA, AMD, Intel); Media Foundation has a software one on every
    // Windows 10/11. Options a driver does not know are skipped by mpv with a warning.
    static readonly (string Codec, string Options)[] Encoders =
    [
        ("h264_nvenc", "preset=p5,tune=hq,rc=vbr,cq=21,b=0,spatial-aq=1"),
        ("h264_amf", "quality=quality,rc=cqp,qp_i=20,qp_p=22,qp_b=24"),
        ("h264_qsv", "preset=slower,global_quality=21"),
        ("h264_mf", "rate_control=quality,quality=80"),
    ];
    // Only these files are ever deleted, even when the folder falls back to the shared temporary folder.
    static readonly Regex Own = new(@"^[0-9A-F]{32}(\.test)?\.mp4(\.part)?$");
    const string Off = "Выключено: все видео играют как есть.";
    const long MinimumFree = 2L << 30;
    readonly string folder, indexPath;
    readonly string engine = Path.Combine(Program.Root, "engine", "mpv.exe");
    readonly Dictionary<string, Probe> index;
    readonly object sync = new();
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource stop = new();
    readonly IntPtr job = Native.KillOnCloseJob();
    CancellationTokenSource? pass;
    List<string> files = [];
    int encoder = -1, misses, requests;
    volatile bool enabled;
    volatile int maxFps;
    public string Status { get; private set; }
    // Both are raised on a worker thread.
    public event Action? Changed;
    public event Action<string>? Ready;

    public Optimizer(bool enabled, int maxFps)
    {
        this.enabled = enabled; this.maxFps = maxFps;
        folder = Program.DataFolder("optimized");
        if (Path.TrimEndingDirectorySeparator(folder) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()))
        {
            folder = Path.Combine(folder, "wallsets-optimized");
            Directory.CreateDirectory(folder);
        }
        indexPath = Path.Combine(folder, "wallsets-optimized.json");
        try { index = JsonSerializer.Deserialize<Dictionary<string, Probe>>(File.ReadAllText(indexPath)) ?? []; }
        catch { index = []; }
        Status = enabled ? "Ждёт списка обоев…" : Off;
        _ = Task.Run(Work);
    }
    public bool Enabled { get => enabled; set { enabled = value; Restart(); } }
    public int MaxFps { get => maxFps; set { maxFps = value; Restart(); } }
    // All wallpaper files, the ones that play soonest first.
    public void Update(List<string> paths) { lock (sync) files = paths; wake.Release(); }
    // Settings changed: the copy being made may be the wrong one, so it is dropped and the list is checked again.
    public void Restart() { lock (sync) pass?.Cancel(); wake.Release(); }

    // The lighter copy of a wallpaper when it is ready, otherwise the file itself.
    public string Resolve(string path)
    {
        if (!enabled) return path;
        return CopyFor(path, out _) is { } copy && File.Exists(copy) ? copy : path;
    }
    // Where the copy of a file goes; null when the file needs none or is not checked yet.
    string? CopyFor(string path, out Plan plan)
    {
        plan = default;
        if (SourceKey(path) is not { } key) return null;
        Probe? probe;
        lock (sync) index.TryGetValue(key, out probe);
        if (probe == null || probe.Keep || probe.Failed) return null;
        if (PlanFor(probe.Width, probe.Height, probe.Fps, Screen.PrimaryScreen!.Bounds.Size, maxFps) is not { } p) return null;
        plan = p;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|{p.Width}x{p.Height}|{p.Step}"));
        return Path.Combine(folder, Convert.ToHexString(hash)[..32] + ".mp4");
    }
    static string? SourceKey(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}" : null;
    }
    // The copy still covers the whole screen in every scaling mode (the larger of the two scale factors), so
    // nothing is lost on screen. A video that is less than about 10% larger than needed keeps its size.
    public static Plan? PlanFor(int width, int height, double fps, Size screen, int maxFps)
    {
        if (width <= 0 || height <= 0 || screen.Width <= 0 || screen.Height <= 0) return null;
        double scale = Math.Max((double)screen.Width / width, (double)screen.Height / height);
        int w = width, h = height;
        if (scale < 0.9) { w = Even(width * scale); h = Even(height * scale); }
        int step = Player.FrameStep(fps, maxFps);
        return w == width && h == height && step == 1 ? null : new Plan(w, h, step);
    }
    static int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);

    async Task Work()
    {
        while (true)
        {
            try { await wake.WaitAsync(stop.Token); } catch (OperationCanceledException) { return; }
            while (wake.Wait(0)) { }
            CancellationToken token;
            lock (sync) { pass?.Dispose(); pass = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); token = pass.Token; }
            try
            {
                if (!enabled) Say(Off);
                else await Pass(token);
            }
            // A stopped helper player can also end with a broken pipe.
            catch (Exception) when (token.IsCancellationRequested) { if (stop.IsCancellationRequested) return; }
            catch (Exception ex) { Program.Log(ex); Say("Облегчение видео остановлено из-за ошибки: " + ex.Message); }
        }
    }
    async Task Pass(CancellationToken token)
    {
        List<string> list;
        lock (sync) list = files;
        if (list.Count == 0) { Say("Ждёт списка обоев…"); return; }
        if (!File.Exists(engine)) { Say("Нет файла engine\\mpv.exe."); return; }
        var videos = list.Where(f => Videos.Contains(Path.GetExtension(f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Sizes and frame rates first: a short helper player for each file not seen before.
        for (int i = 0; i < videos.Count; i++)
        {
            if (SourceKey(videos[i]) is not { } key) continue;
            bool known;
            lock (sync) known = index.ContainsKey(key);
            if (known) continue;
            Say($"Проверка видео: {i + 1} из {videos.Count}…");
            var probe = await Read(videos[i], token);
            lock (sync) { index[key] = probe; SaveIndex(); }
        }
        var work = new List<(string Path, string Copy, Plan Plan)>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
            if (CopyFor(video, out var plan) is { } copy && keep.Add(copy) && !File.Exists(copy)) work.Add((video, copy, plan));
        for (int i = 0; i < work.Count; i++)
        {
            var (path, copy, plan) = work[i];
            while (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Offline)
            {
                Say("Ждёт зарядки: от батареи видео не пережимаются.");
                await Task.Delay(30000, token);
            }
            if (new DriveInfo(folder).AvailableFreeSpace < MinimumFree) { Say("Остановлено: на диске с папкой .cache меньше 2 ГБ свободного места."); return; }
            var name = Path.GetFileNameWithoutExtension(path);
            var number = $"{keep.Count - work.Count + i + 1} из {keep.Count}";
            if (encoder < 0)
            {
                Say($"Облегчение видео {number}: подбор кодировщика видеокарты…");
                var test = Path.Combine(folder, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".test.mp4");
                for (int codec = 0; codec < Encoders.Length && encoder < 0; codec++)
                    if (await Encode(path, test, plan, codec, true, null, token)) encoder = codec;
                if (encoder < 0)
                {
                    // Either no encoder works or this file cannot be read: two files in a row decide.
                    Fail(path, "Нет рабочего кодировщика H.264 для файла ");
                    if (++misses < 2) continue;
                    encoder = Encoders.Length;
                }
            }
            if (encoder >= Encoders.Length) { Say("Не удалось облегчить видео: видеокарта и Windows не дали кодировщик H.264. Видео играют как есть."); return; }
            if (await Encode(path, copy, plan, encoder, false, percent => Say($"Облегчение видео {number}: «{name}» — {percent}%"), token)) Ready?.Invoke(path);
            else Fail(path, $"Кодировщик {Encoders[encoder].Codec} не смог облегчить файл ");
        }
        // Copies of removed or changed files and of an old screen size or limit; one that still plays goes next time.
        foreach (var file in OwnFiles())
            if (!keep.Contains(file)) try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        int failed;
        lock (sync) failed = videos.Count(v => SourceKey(v) is { } k && index.TryGetValue(k, out var p) && p.Failed);
        long size = OwnFiles().Where(keep.Contains).Sum(f => new FileInfo(f).Length);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var text = keep.Count == 0
            ? "Тяжёлых видео нет: все не больше экрана" + (maxFps > 0 ? $" и не чаще {maxFps} кадров/с." : ".")
            : $"Готово: облегчено {keep.Count} видео, копии занимают " + (size >= 1L << 30 ? (size / (double)(1L << 30)).ToString("0.0", ru) + " ГБ." : (size >> 20) + " МБ.");
        if (failed > 0) text += $" Не удалось облегчить: {failed} (они играют как есть, подробности в errors.log).";
        Say(text);
    }
    void Fail(string path, string reason)
    {
        Program.Log(new IOException(reason + path));
        lock (sync) { if (SourceKey(path) is { } key && index.TryGetValue(key, out var probe)) probe.Failed = true; SaveIndex(); }
    }

    // Size, frame rate, turn and colour curve from a paused helper player without picture or sound.
    async Task<Probe> Read(string path, CancellationToken token)
    {
        var pipe = $"wallsets-probe-{Environment.ProcessId}-{Interlocked.Increment(ref requests)}";
        using var process = Start("--pause=yes", "--vo=null", "--ao=null", "--aid=no", "--sid=no", "--hwdec=no", "--input-ipc-server=" + pipe, "--", path);
        using var kill = token.Register(() => Kill(process));
        try
        {
            for (int i = 0; i < 100 && !process.HasExited; i++)
            {
                await Task.Delay(100, token);
                try
                {
                    double width = await Number(pipe, "current-tracks/video/demux-w"), height = await Number(pipe, "current-tracks/video/demux-h");
                    if (width <= 0 || height <= 0) continue;
                    double rotate = await Number(pipe, "current-tracks/video/demux-rotate"), fps = await Number(pipe, "current-tracks/video/demux-fps");
                    // The colour curve is known once the first frame is decoded.
                    var gamma = "";
                    for (int j = 0; j < 20 && gamma.Length == 0; j++)
                    {
                        var value = await Value(pipe, "video-params/gamma");
                        if (value.ValueKind == JsonValueKind.String) gamma = value.GetString() ?? "";
                        else await Task.Delay(100, token);
                    }
                    return new Probe { Width = (int)width, Height = (int)height, Fps = fps, Keep = rotate % 360 != 0 || gamma is "pq" or "hlg" };
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested) { }
            }
            return new Probe { Failed = true };
        }
        finally { Kill(process); }
    }
    // Encoding mode of mpv: the graphics card decodes, every n-th frame is kept and scaled, the encoder writes
    // an MP4 next to the final name, which it gets only when mpv ends without an error. A test encodes 1 second.
    async Task<bool> Encode(string path, string output, Plan plan, int codec, bool test, Action<int>? progress, CancellationToken token)
    {
        var part = output + ".part";
        var pipe = $"wallsets-encode-{Environment.ProcessId}-{Interlocked.Increment(ref requests)}";
        var graph = (plan.Step > 1 ? $"framestep={plan.Step}," : "") + $"scale={plan.Width}:{plan.Height}:flags=bicubic";
        var args = new List<string> { "--hwdec=auto-copy", "--sid=no", "--o=" + part, "--of=mp4", "--ofopts=movflags=+faststart", "--ovc=" + Encoders[codec].Codec, "--ovcopts=" + Encoders[codec].Options, "--oac=aac", "--oacopts=b=192k", "--vf=" + Player.FilterChain(graph), "--input-ipc-server=" + pipe };
        if (test) args.Add("--length=1");
        args.Add("--"); args.Add(path);
        var process = Start([.. args]);
        try
        {
            using var kill = token.Register(() => Kill(process));
            while (!process.HasExited)
            {
                await Task.WhenAny(process.WaitForExitAsync(token), Task.Delay(1000, token));
                token.ThrowIfCancellationRequested();
                if (progress == null || process.HasExited) continue;
                try { progress((int)await Number(pipe, "percent-pos")); }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested) { }
            }
            if (process.ExitCode != 0 || !File.Exists(part) || new FileInfo(part).Length == 0) return false;
            if (!test) File.Move(part, output, true);
            return true;
        }
        finally
        {
            Kill(process); process.Dispose();
            try { File.Delete(part); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    // Helper players are hidden, at idle priority and in efficiency mode, and end together with Wallsets.
    Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        foreach (var arg in new[] { "--no-config", "--terminal=no", "--msg-level=all=no", "--idle=no", "--osc=no" }.Concat(args)) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить engine\\mpv.exe.");
        if (job != IntPtr.Zero) Native.AssignProcessToJobObject(job, process.Handle);
        try { process.PriorityClass = ProcessPriorityClass.Idle; } catch { }
        Native.Efficiency(process.Handle);
        return process;
    }
    static void Kill(Process process) { try { if (!process.HasExited) process.Kill(); } catch { } }
    static async Task<JsonElement> Value(string pipe, string name)
    {
        try { return await Player.Send(pipe, "get_property", name); }
        catch (InvalidOperationException) { return default; }
    }
    static async Task<double> Number(string pipe, string name) => await Value(pipe, name) is { ValueKind: JsonValueKind.Number } v ? v.GetDouble() : 0;
    IEnumerable<string> OwnFiles() => Directory.EnumerateFiles(folder).Where(f => Own.IsMatch(Path.GetFileName(f)));
    void SaveIndex()
    {
        try
        {
            File.WriteAllText(indexPath + ".tmp", JsonSerializer.Serialize(index));
            File.Move(indexPath + ".tmp", indexPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log(ex); }
    }
    void Say(string text) { Status = text; Changed?.Invoke(); }

    // Deletes every copy and what is known about the files. A copy that still plays is deleted on the next pass.
    public void Clear()
    {
        lock (sync) { pass?.Cancel(); index.Clear(); SaveIndex(); }
        foreach (var file in OwnFiles()) try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Say(enabled ? "Копии удалены." : "Копии удалены. " + Off);
    }
    public void Dispose()
    {
        stop.Cancel();
        // Closing the group ends a helper player that is still running.
        if (job != IntPtr.Zero) Native.CloseHandle(job);
    }
}
