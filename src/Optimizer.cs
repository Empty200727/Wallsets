using System.Buffers.Binary;
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
        // The encoder (and Revision) that could not make the copy; another encoder tries again.
        public string? FailedWith { get; set; }
        // The graphics card could not decode it (the player decoded it on the processor): an H.264 copy is made
        // even when its size and frame rate need none.
        public bool Software { get; set; }
        // How the player reads the original's colours ("matrix:levels:primaries:gamma" of mpv's video-params):
        // a copy is shown with exactly these, whatever colour tags its encoder wrote.
        public string? Colors { get; set; }
    }
    // A video as the optimizer sees it, for the window that lists them.
    public enum State { Unknown, Light, Kept, Waiting, Working, Ready, Failed }
    public readonly record struct Item(string Path, State State, int Percent);
    public readonly record struct Plan(int Width, int Height, int Step);
    static readonly HashSet<string> Videos = new(".mp4 .mkv .webm .mov .avi .m4v .wmv .mpg .mpeg .ts".Split(' '), StringComparer.OrdinalIgnoreCase);
    // Graphics card encoders first (NVIDIA, AMD, Intel). Media Foundation is the Windows way to the same hardware
    // (through the graphics driver, so it also works where FFmpeg's NVENC needs a newer driver), and last its
    // software encoder, which works everywhere but on the processor. Options a driver does not know are skipped.
    // Bitrate: the encoder also gets b= (bits per second, see BitrateFor). Media Foundation always passes a mean
    // bitrate to the driver, FFmpeg's default of 200 kbit/s unless one is set, and a driver without a quality mode
    // encodes with it: copies came out blurred and dull. Quick Sync falls back to it the same way.
    static readonly (string Name, string Codec, string Options, bool Bitrate)[] Encoders =
    [
        ("NVIDIA NVENC", "h264_nvenc", "preset=p5,tune=hq,rc=vbr,cq=21,b=0,spatial-aq=1", false),
        ("AMD AMF", "h264_amf", "quality=quality,rc=cqp,qp_i=20,qp_p=22,qp_b=24", false),
        ("Intel Quick Sync", "h264_qsv", "preset=slower,global_quality=21", true),
        ("Windows (видеокарта)", "h264_mf", "hw_encoding=1,rate_control=u_vbr", true),
        ("Windows (процессор)", "h264_mf", "hw_encoding=0,rate_control=u_vbr", true),
    ];
    // About 0.08 bit per pixel of every frame kept: generous for an encoder of a graphics card at screen size.
    public static long BitrateFor(Plan plan, double fps) =>
        Math.Max(2_000_000L, (long)(plan.Width * (double)plan.Height * (fps > 0 ? fps / plan.Step : 30) * 0.08));
    // Only these files are ever deleted, even when the folder falls back to the shared temporary folder.
    static readonly Regex Own = new(@"^[0-9A-F]{32}(\.test)?\.mp4(\.part|\.log)?$");
    static readonly Regex Noise = new(@"\]\[vd\]|AVHW|hwaccel|no frame!");
    static readonly Regex DriverNeeded = new(@"minimum required Nvidia driver for nvenc is ([0-9.]+)");
    const string Off = "Выключено: все видео играют как есть.";
    const long MinimumFree = 2L << 30;
    // Changes of the way copies are made: failures of an older way are tried again. 2: copies without sound.
    const string Revision = "#2";
    // A copy whose video is gone is deleted only after this time, so cutting a video and pasting it into
    // another set (or renaming it) does not lose its copy.
    static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(10);
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
    // The NVIDIA driver FFmpeg's NVENC asked for, when the installed one is older.
    string? driver;
    // The video being encoded and how far it is.
    string? working;
    int percent;
    // Copies without a video since this time (Environment.TickCount64); used by the worker only.
    readonly Dictionary<string, long> orphans = new(StringComparer.OrdinalIgnoreCase);
    bool cleanupPlanned;
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
        // Copies that failed before the encoder was remembered get one more try.
        foreach (var probe in index.Values.Where(p => p.Failed && p.FailedWith == null && p.Width > 0)) probe.FailedWith = "?";
        Status = enabled ? "Ждёт списка обоев…" : Off;
        _ = Task.Run(Work);
    }
    public bool Enabled { get => enabled; set { enabled = value; Restart(); } }
    public int MaxFps { get => maxFps; set { maxFps = value; Restart(); } }
    // All wallpaper files, the ones that play soonest first.
    public void Update(List<string> paths) { lock (sync) files = paths; wake.Release(); }
    // The player decoded this video on the processor: from now on it gets a copy in H.264, which every graphics
    // card decodes.
    public void SoftwareDecoded(string path)
    {
        if (!Videos.Contains(Path.GetExtension(path)) || SourceKey(path) is not { } key) return;
        lock (sync)
        {
            if (!index.TryGetValue(key, out var probe) || probe.Software) return;
            probe.Software = true; SaveIndex();
        }
        wake.Release();
    }
    // Settings changed: the copy being made may be the wrong one, so it is dropped and the list is checked again.
    public void Restart() { lock (sync) pass?.Cancel(); wake.Release(); }

    // The lighter copy of a wallpaper when it is ready, otherwise the file itself.
    public string Resolve(string path)
    {
        if (!enabled) return path;
        return CopyFor(path, out _) is { } copy && File.Exists(copy) ? copy : path;
    }
    // Where the copy of a file goes for the frame-rate limit (the current one by default); null when the file
    // needs none or is not checked yet.
    string? CopyFor(string path, out Plan plan, bool evenFailed = false, int? limit = null)
    {
        plan = default;
        if (SourceKey(path) is not { } key) return null;
        Probe? probe;
        lock (sync) index.TryGetValue(key, out probe);
        if (probe == null || probe.Keep || probe.Failed && !evenFailed) return null;
        if (PlanFor(probe.Width, probe.Height, probe.Fps, Screen.PrimaryScreen!.Bounds.Size, limit ?? maxFps, probe.Software) is not { } p) return null;
        plan = p;
        return CopyName(key, p);
    }
    // CopyVersion: copies of an older way of making them are made again (2: sharper scaling, enough bitrate).
    const string CopyVersion = "2";
    string CopyName(string key, Plan plan) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key}|{plan.Width}x{plan.Height}|{plan.Step}|{CopyVersion}")))[..32] + ".mp4");
    // The original's colours for the player to show its copy with, or null.
    public string? ColorsOf(string path)
    {
        if (SourceKey(path) is not { } key) return null;
        lock (sync) return index.TryGetValue(key, out var probe) ? probe.Colors : null;
    }
    // A video is known by its contents (Fingerprints: its size and its first and last 64 KB), so a renamed file
    // or one moved to another set keeps its copy. When the file cannot be read, its path stands in.
    static string? SourceKey(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        return Fingerprints.Get(path) is { } id ? $"{id}|{file.Length}" : PathKey(file);
    }
    // How older versions knew a video: its path, size and time of change.
    static string PathKey(FileInfo file) => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    // Moves what older versions knew by path (sizes, failures, copies for every limit) to the contents key.
    void Migrate(List<string> videos)
    {
        bool changed = false;
        foreach (var video in videos)
        {
            var file = new FileInfo(video);
            if (!file.Exists || SourceKey(video) is not { } key) continue;
            var old = PathKey(file);
            if (key == old) continue;
            Probe? probe;
            lock (sync)
            {
                if (!index.Remove(old, out probe)) continue;
                index.TryAdd(key, probe);
                changed = true;
            }
            foreach (var limit in Settings.FpsLimits)
                if (PlanFor(probe.Width, probe.Height, probe.Fps, Screen.PrimaryScreen!.Bounds.Size, limit, probe.Software) is { } plan
                    && CopyName(old, plan) is var from && File.Exists(from) && CopyName(key, plan) is var to && !File.Exists(to))
                    try { File.Move(from, to); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        if (changed) lock (sync) SaveIndex();
    }
    // The copy still covers the whole screen in every scaling mode (the larger of the two scale factors), so
    // nothing is lost on screen. A video that is less than about 10% larger than needed keeps its size.
    public static Plan? PlanFor(int width, int height, double fps, Size screen, int maxFps, bool reencode = false)
    {
        if (width <= 0 || height <= 0 || screen.Width <= 0 || screen.Height <= 0) return null;
        double scale = Math.Max((double)screen.Width / width, (double)screen.Height / height);
        int w = width, h = height;
        if (scale < 0.9) { w = Even(width * scale); h = Even(height * scale); }
        int step = Player.FrameStep(fps, maxFps);
        return w == width && h == height && step == 1 && !reencode ? null : new Plan(w, h, step);
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
        Migrate(videos);
        // Sizes and frame rates first: a short helper player for each file not seen before.
        for (int i = 0; i < videos.Count; i++)
        {
            if (SourceKey(videos[i]) is not { } key) continue;
            Probe? known;
            lock (sync) index.TryGetValue(key, out known);
            // Checked before colours were kept: read again, keeping what is known about it.
            if (known != null && (known.Colors != null || known.Failed && known.Width == 0)) continue;
            Say($"Проверка видео: {i + 1} из {videos.Count}…");
            var probe = await Read(videos[i], token);
            if (known != null && !probe.Failed) { probe.Failed = known.Failed; probe.FailedWith = known.FailedWith; probe.Software = known.Software; }
            lock (sync) { index[key] = probe; SaveIndex(); }
        }
        // Copies that another encoder could not make get one more try once this session's encoder is known.
        if (encoder < 0 && videos.FirstOrDefault(v => Retry(v) && CopyFor(v, out _, true) is { } c && !File.Exists(c)) is { } sample)
        {
            CopyFor(sample, out var plan, true);
            Say("Подбор кодировщика видеокарты…");
            // When nothing works, the next passes of this session do not try again and again.
            if (!await Detect(sample, plan, token) && ++misses >= 2) encoder = Encoders.Length;
        }
        if (encoder >= 0 && encoder < Encoders.Length)
            lock (sync)
            {
                bool changed = false;
                foreach (var probe in index.Values.Where(p => p.Failed && p.FailedWith != null && p.FailedWith != Encoders[encoder].Name + Revision)) { probe.Failed = false; probe.FailedWith = null; changed = true; }
                if (changed) SaveIndex();
            }
        // Copies for the current limit are made; those for the other limits stay too, so switching back is instant.
        var work = new List<(string Path, string Copy, Plan Plan)>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            if (CopyFor(video, out var plan) is { } copy && wanted.Add(copy) && !File.Exists(copy)) work.Add((video, copy, plan));
            foreach (var limit in Settings.FpsLimits) if (CopyFor(video, out _, limit: limit) is { } other) keep.Add(other);
        }
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
            var number = $"{wanted.Count - work.Count + i + 1} из {wanted.Count}";
            if (encoder < 0)
            {
                Say($"Облегчение видео {number}: подбор кодировщика видеокарты…");
                if (!await Detect(path, plan, token))
                {
                    // Either no encoder works or this file cannot be read: two files in a row decide.
                    Fail(path, "Нет рабочего кодировщика H.264 для файла ", null);
                    if (++misses < 2) continue;
                    encoder = Encoders.Length;
                }
            }
            if (encoder >= Encoders.Length) { Say("Не удалось облегчить видео: видеокарта и Windows не дали кодировщик H.264. Видео играют как есть." + DriverHint); return; }
            working = path; percent = 0;
            try
            {
                if (await Encode(path, copy, plan, encoder, false, value => { percent = value; Say($"Облегчение видео {number}: «{name}» — {value}%" + Using); }, token)) Ready?.Invoke(path);
                else Fail(path, $"Кодировщик {Encoders[encoder].Name} не смог облегчить файл ", Encoders[encoder].Name + Revision);
            }
            finally { working = null; }
        }
        Tidy(keep);
        int failed;
        lock (sync) failed = videos.Count(v => SourceKey(v) is { } k && index.TryGetValue(k, out var p) && p.Failed);
        long size = OwnFiles().Where(keep.Contains).Sum(f => new FileInfo(f).Length);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var text = wanted.Count == 0
            ? "Тяжёлых видео нет: все не больше экрана" + (maxFps > 0 ? $" и не чаще {maxFps} кадров/с." : ".")
            : $"Готово: облегчено {wanted.Count(File.Exists)} видео, копии занимают " + (size >= 1L << 30 ? (size / (double)(1L << 30)).ToString("0.0", ru) + " ГБ." : (size >> 20) + " МБ.");
        if (failed > 0) text += $" Не удалось облегчить: {failed} (они играют как есть, подробности в errors.log).";
        Say(text + (wanted.Count > 0 ? Using : "") + DriverHint);
    }
    // Leftovers of stopped encodes go at once. A copy whose video is gone (deleted, changed, or the screen size
    // changed) goes after OrphanGrace; a copy that still plays is deleted on a later pass.
    void Tidy(HashSet<string> keep)
    {
        long now = Environment.TickCount64;
        foreach (var file in OwnFiles().ToList())
        {
            if (keep.Contains(file)) { orphans.Remove(file); continue; }
            bool leftover = !file.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".test.mp4", StringComparison.OrdinalIgnoreCase);
            if (!leftover && (!orphans.TryGetValue(file, out var since) || now - since < (long)OrphanGrace.TotalMilliseconds)) { orphans.TryAdd(file, now); continue; }
            try { File.Delete(file); orphans.Remove(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var gone in orphans.Keys.Where(f => !File.Exists(f)).ToList()) orphans.Remove(gone);
        // Nothing else may wake the optimizer: it looks again once the grace time is over.
        if (orphans.Count > 0 && !cleanupPlanned)
        {
            cleanupPlanned = true;
            _ = Task.Delay(OrphanGrace + TimeSpan.FromSeconds(30), stop.Token).ContinueWith(t => { cleanupPlanned = false; if (!t.IsCanceled) wake.Release(); }, TaskScheduler.Default);
        }
    }
    string Using => encoder >= 0 && encoder < Encoders.Length ? $" Кодировщик: {Encoders[encoder].Name}." : "";
    string DriverHint => driver != null && (encoder < 0 || Encoders[Math.Min(encoder, Encoders.Length - 1)].Codec != "h264_nvenc")
        ? $" NVENC видеокарты NVIDIA не запустился: ему нужен драйвер NVIDIA {driver} или новее — обновите драйвер, и копии будут делаться быстрее." : "";
    // Tries the encoders in order on one second of the video and keeps the first that makes a finished MP4.
    async Task<bool> Detect(string path, Plan plan, CancellationToken token)
    {
        var test = Path.Combine(folder, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".test.mp4");
        for (int codec = 0; codec < Encoders.Length; codec++)
            if (await Encode(path, test, plan, codec, true, null, token)) { encoder = codec; return true; }
        return false;
    }
    double FpsOf(string path)
    {
        if (SourceKey(path) is not { } key) return 0;
        lock (sync) return index.TryGetValue(key, out var probe) ? probe.Fps : 0;
    }
    // A file that failed with an encoder other than the one found now.
    bool Retry(string path)
    {
        lock (sync) return SourceKey(path) is { } key && index.TryGetValue(key, out var probe) && probe.Failed && probe.FailedWith != null;
    }
    void Fail(string path, string reason, string? encoderName)
    {
        Program.Log(new IOException(reason + path));
        lock (sync) { if (SourceKey(path) is { } key && index.TryGetValue(key, out var probe)) { probe.Failed = true; probe.FailedWith = encoderName; } SaveIndex(); }
    }
    // Every video of the wallpaper list and what happens to it, in the list's order.
    public List<Item> Items()
    {
        List<string> list;
        lock (sync) list = files;
        var result = new List<Item>();
        foreach (var path in list.Where(f => Videos.Contains(Path.GetExtension(f))).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Probe? probe = null;
            if (SourceKey(path) is { } key) lock (sync) index.TryGetValue(key, out probe);
            var state = probe == null ? State.Unknown : probe.Failed ? State.Failed : probe.Keep ? State.Kept
                : CopyFor(path, out _) is not { } copy ? State.Light : File.Exists(copy) ? State.Ready
                : string.Equals(path, working, StringComparison.OrdinalIgnoreCase) ? State.Working : State.Waiting;
            result.Add(new Item(path, state, state == State.Working ? percent : 0));
        }
        return result;
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
                    var colors = new List<string>();
                    foreach (var name in new[] { "colormatrix", "colorlevels", "primaries" })
                        if (await Value(pipe, "video-params/" + name) is { ValueKind: JsonValueKind.String } part && part.GetString() is { Length: > 0 } text && text != "auto") colors.Add(text);
                    return new Probe
                    {
                        Width = (int)width, Height = (int)height, Fps = fps, Keep = rotate % 360 != 0 || gamma is "pq" or "hlg",
                        Colors = colors.Count == 3 && gamma.Length > 0 && gamma != "auto" ? string.Join(":", colors.Append(gamma)) : ""
                    };
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested) { }
            }
            return new Probe { Failed = true };
        }
        finally { Kill(process); }
    }
    // Encoding mode of mpv: the graphics card decodes, every n-th frame is kept and scaled, the encoder writes
    // an MP4 next to the final name, which it gets only when it is complete. A test encodes 1 second.
    async Task<bool> Encode(string path, string output, Plan plan, int codec, bool test, Action<int>? progress, CancellationToken token)
    {
        var part = output + ".part";
        var pipe = $"wallsets-encode-{Environment.ProcessId}-{Interlocked.Increment(ref requests)}";
        // Lanczos keeps thin dark outlines dark and sharp; the filtering avoids the jagged steps of the quick
        // scaling the player does on the fly. Full chroma precision keeps colours clean at their edges.
        var graph = (plan.Step > 1 ? $"framestep={plan.Step}," : "") + $"scale={plan.Width}:{plan.Height}:flags=lanczos+accurate_rnd+full_chroma_int";
        // Only the picture: the player takes the sound from the original file. It also avoids an mpv 0.41 hang when
        // a soundtrack starts a little after the picture.
        var args = new List<string> { "--hwdec=auto-copy", "--sid=no", "--aid=no", "--o=" + part, "--of=mp4", "--ofopts=movflags=+faststart", "--ovc=" + Encoders[codec].Codec, "--ovcopts=" + Encoders[codec].Options + (Encoders[codec].Bitrate ? ",b=" + BitrateFor(plan, FpsOf(path)) : ""), "--vf=" + Player.FilterChain(graph), "--input-ipc-server=" + pipe };
        // A test keeps mpv's log: the reason an encoder does not work goes to errors.log.
        var log = output + ".log";
        if (test) { args.Add("--length=1"); args.Add("--log-file=" + log); }
        args.Add("--"); args.Add(path);
        var process = Start([.. args]);
        try
        {
            using var kill = token.Register(() => Kill(process));
            // mpv can stay open after the last frame or stop moving: after two minutes without progress it is
            // stopped, and the copy counts if the MP4 was finished.
            double last = -1;
            long moved = Environment.TickCount64, written = 0;
            bool stalled = false;
            while (!process.HasExited)
            {
                await Task.WhenAny(process.WaitForExitAsync(token), Task.Delay(1000, token));
                token.ThrowIfCancellationRequested();
                if (process.HasExited) break;
                try
                {
                    var percent = await Number(pipe, "percent-pos");
                    if (percent != last) { last = percent; moved = Environment.TickCount64; }
                    progress?.Invoke((int)percent);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested) { }
                // A busy encoder can keep mpv from answering: a growing file is progress too.
                long size = File.Exists(part) ? new FileInfo(part).Length : 0;
                if (size != written) { written = size; moved = Environment.TickCount64; }
                if (Environment.TickCount64 - moved > 120_000)
                {
                    stalled = true;
                    Kill(process); process.WaitForExit(5000);
                    Program.Log(new TimeoutException($"mpv не завершился после {last:0}% пережатия, остановлен: {path}"));
                    break;
                }
            }
            if (!(stalled || process.ExitCode == 0) || !Complete(part))
            {
                if (test) Explain(codec, log);
                return false;
            }
            if (!test) File.Move(part, output, true);
            return true;
        }
        finally
        {
            Kill(process); process.Dispose();
            foreach (var file in new[] { part, log })
                try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    // "The minimum required Nvidia driver for nvenc is 610.00 or newer" -> "610.00".
    internal static string? DriverFrom(string line) => DriverNeeded.Match(line) is { Success: true } m ? m.Groups[1].Value : null;
    // Errors from mpv's log of a failed test, and the NVIDIA driver version NVENC asks for.
    void Explain(int codec, string log)
    {
        try
        {
            // Messages of the video decoder (its hardware part falls back to the processor on its own) are left out.
            var all = File.Exists(log) ? File.ReadLines(log).Where(l => (l.Contains("][e][") || l.Contains("][f][")) && !Noise.IsMatch(l))
                .Select(l => l[(l.IndexOf("][", l.IndexOf(']')) + 1)..].Trim()).Distinct().ToList() : [];
            foreach (var line in all) if (DriverFrom(line) is { } version) driver = version;
            var lines = all.TakeLast(5).ToList();
            Program.Log(new IOException($"Кодировщик {Encoders[codec].Name} не подошёл" + (lines.Count > 0 ? ": " + string.Join(" | ", lines) : ".")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    // A finished MP4: its top-level boxes fill the file exactly and include the index (moov) and the data
    // (mdat). The muxer writes the index last, so a file cut off while it was written has none.
    internal static bool Complete(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var header = new byte[16];
            long at = 0, length = file.Length;
            bool moov = false, mdat = false;
            while (at < length)
            {
                file.Position = at;
                file.ReadExactly(header, 0, 8);
                long size = BinaryPrimitives.ReadUInt32BigEndian(header);
                var type = Encoding.ASCII.GetString(header, 4, 4);
                if (size == 1) { file.ReadExactly(header, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); }
                if (size < 8 || size > length - at) return false;
                moov |= type == "moov"; mdat |= type == "mdat";
                at += size;
            }
            return moov && mdat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    // Helper players are hidden, at idle priority and in efficiency mode, and end together with Wallsets. Built-in
    // scripts, media keys and the Windows media controls are off: an encoder needs none of them, and they can
    // keep mpv from closing.
    Process Start(params string[] args)
    {
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        string[] quiet = ["--no-config", "--terminal=no", "--msg-level=all=no", "--idle=no", "--osc=no", "--load-scripts=no", "--ytdl=no", "--load-auto-profiles=no", "--load-stats-overlay=no",
            "--load-console=no", "--load-select=no", "--load-positioning=no", "--load-commands=no", "--media-controls=no", "--input-media-keys=no"];
        foreach (var arg in quiet.Concat(args)) start.ArgumentList.Add(arg);
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
    // The copies made for one frame-rate limit. A video that is not faster than the limit has the same copy for it
    // and for the higher limits (a 30 fps 4K video: one copy for "До 30", "До 60" and "Как в видео").
    public List<string> CopiesFor(int limit)
    {
        List<string> list;
        lock (sync) list = files;
        return list.Where(f => Videos.Contains(Path.GetExtension(f))).Select(f => CopyFor(f, out _, limit: limit))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();
    }
    // Deletes the copies of one limit; those of the other limits stay. A copy that still plays goes on a later pass.
    public void Delete(int limit)
    {
        lock (sync) pass?.Cancel();
        int deleted = 0;
        foreach (var file in CopiesFor(limit))
            try { File.Delete(file); deleted++; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Say($"Удалено копий: {deleted}." + (enabled ? "" : " " + Off));
    }
    public void Dispose()
    {
        stop.Cancel();
        // Closing the group ends a helper player that is still running.
        if (job != IntPtr.Zero) Native.CloseHandle(job);
    }
}
