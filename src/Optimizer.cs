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
        // Seconds; a copy made on a retry must be about as long.
        public double Duration { get; set; }
        // Why the copy could not be made, for the optimizer window (the full story is in errors.log).
        public string? Reason { get; set; }
    }
    // A video as the optimizer sees it, for the window that lists them.
    public enum State { Unknown, Light, Kept, Waiting, Working, Ready, Failed }
    public readonly record struct Item(string Path, State State, int Percent, string? Reason = null);
    public readonly record struct Plan(int Width, int Height, int Step);
    static readonly HashSet<string> Videos = new(".mp4 .mkv .webm .mov .avi .m4v .wmv .mpg .mpeg .ts".Split(' '), StringComparer.OrdinalIgnoreCase);
    // Graphics card encoders first (NVIDIA, AMD, Intel), each in its constant-quality mode. When none of them works
    // (FFmpeg's NVENC needs a newer NVIDIA driver than many computers have), x264 in the bundled mpv encodes on the
    // processor: constant quality (crf), far sharper than the Windows encoders, and it writes the colour tags of the
    // picture it gets (BT.709, limited range for HD video). Media Foundation (Windows' way to the graphics driver's
    // encoder, and Windows' own software encoder) stays as the last resort: FFmpeg passes it no colour range or
    // matrix, so the encoder guesses them, and Windows' software encoder gave copies that were paler as a whole and
    // soft. Options a driver does not know are skipped.
    // Bitrate: the encoder also gets b= (bits per second, see BitrateFor). Media Foundation always passes a mean
    // bitrate to the driver, FFmpeg's default of 200 kbit/s unless one is set, and a driver without a quality mode
    // encodes with it: copies came out blurred and dull. Quick Sync falls back to it the same way.
    static readonly (string Name, string Codec, string Options, bool Bitrate)[] Encoders =
    [
        ("NVIDIA NVENC", "h264_nvenc", "preset=p5,tune=hq,rc=vbr,cq=21,b=0,spatial-aq=1", false),
        ("AMD AMF", "h264_amf", "quality=quality,rc=cqp,qp_i=20,qp_p=22,qp_b=24", false),
        ("Intel Quick Sync", "h264_qsv", "preset=slower,global_quality=21", true),
        ("x264 (процессор)", "libx264", "preset=faster,crf=16", false),
        ("Windows (видеокарта)", "h264_mf", "hw_encoding=1,rate_control=u_vbr", true),
        ("Windows (процессор)", "h264_mf", "hw_encoding=0,rate_control=u_vbr", true),
    ];
    // About 0.12 bit per pixel of every frame kept. The copy keeps the original's crisp one-pixel outlines (see
    // ScaleGraph), which take more bits than a softened picture, and the encoders of graphics cards and Windows
    // need more bits than a software encoder for the same picture.
    public static long BitrateFor(Plan plan, double fps) =>
        Math.Max(2_000_000L, (long)Math.Round(plan.Width * (double)plan.Height * (fps > 0 ? fps / plan.Step : 30) * 0.12));
    // Only these files are ever deleted, even when the folder falls back to the shared temporary folder.
    static readonly Regex Own = new(@"^[0-9A-F]{32}(\.test)?\.mp4(\.part|\.log)?$");
    static readonly Regex Noise = new(@"\]\[vd\]|AVHW|hwaccel|no frame!");
    static readonly Regex DriverNeeded = new(@"minimum required Nvidia driver for nvenc is ([0-9.]+)");
    const string Off = "Выключено: все видео играют как есть.";
    const long MinimumFree = 2L << 30;
    // Changes of the way copies are made: failures of an older way are tried again. 2: copies without sound.
    // 3: scaled the way the player draws the original. 4: NV12 for every encoder, x264 before Media Foundation.
    const string Revision = "#4";
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
    // A video that could not be lightened and is to be tried again carefully, asked in the optimizer window.
    string? urgent;
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
    // A video that could not be lightened is tried again at once, carefully (see Careful). A copy being made for
    // another video is stopped and made again later.
    public void RetryNow(string path)
    {
        lock (sync) { urgent = path; pass?.Cancel(); }
        wake.Release();
    }

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
    // CopyVersion: copies of an older way of making them are made again (2: lanczos scaling, enough bitrate;
    // 3: scaled the way the player draws the original, 0.12 bit per pixel; 4: x264 before Windows' encoders).
    const string CopyVersion = "4";
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
        string? retry;
        lock (sync) retry = urgent;
        if (retry != null)
        {
            // It stays asked for until it is done, so a pass stopped in the middle tries it again first.
            await Careful(retry, token);
            lock (sync) if (urgent == retry) urgent = null;
        }
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
                    Fail(path, "Нет рабочего кодировщика H.264 для файла " + path, null, "ни один кодировщик не справился даже с одной секундой этого видео");
                    if (++misses < 2) continue;
                    encoder = Encoders.Length;
                }
            }
            if (encoder >= Encoders.Length) { Say("Не удалось облегчить видео: видеокарта и Windows не дали кодировщик H.264. Видео играют как есть." + DriverHint); return; }
            working = path; percent = 0;
            try
            {
                if (await Encode(path, copy, plan, encoder, false, "auto-copy", value => { percent = value; Say($"Облегчение видео {number}: «{name}» — {value}%" + Using); }, token) is not { } reason) Ready?.Invoke(path);
                else Fail(path, $"Кодировщик {Encoders[encoder].Name} не смог облегчить файл {path}: {reason}", Encoders[encoder].Name + Revision, $"{Encoders[encoder].Name}: {reason}");
            }
            finally { working = null; }
        }
        Tidy(keep);
        int failed;
        lock (sync) failed = videos.Count(v => SourceKey(v) is { } k && index.TryGetValue(k, out var p) && p.Failed);
        long size = OwnFiles().Where(keep.Contains).Sum(f => new FileInfo(f).Length);
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        // Failed videos are not among the wanted copies: with nothing else to do there are heavy videos all the same.
        var text = wanted.Count > 0
            ? $"Готово: облегчено {wanted.Count(File.Exists)} видео, копии занимают " + (size >= 1L << 30 ? (size / (double)(1L << 30)).ToString("0.0", ru) + " ГБ." : (size >> 20) + " МБ.")
            : failed == 0 ? "Тяжёлых видео нет: все не больше экрана" + (maxFps > 0 ? $" и не чаще {maxFps} кадров/с." : ".") : "";
        if (failed > 0) text += (text.Length > 0 ? " " : "") + $"Не удалось облегчить: {failed} (они играют как есть; нажмите на такое видео в окне облегчения, чтобы попробовать ещё раз).";
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
        for (int codec = 0; codec < Encoders.Length; codec++)
            if (await Test(path, plan, codec, token)) { encoder = codec; return true; }
        return false;
    }
    // One second of the video with one encoder; why it did not work goes to errors.log.
    async Task<bool> Test(string path, Plan plan, int codec, CancellationToken token)
    {
        var test = Path.Combine(folder, Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + ".test.mp4");
        if (await Encode(path, test, plan, codec, true, "auto-copy", null, token) is not { } reason) return true;
        Program.Log(new IOException($"Кодировщик {Encoders[codec].Name} не подошёл: {reason}"));
        return false;
    }

    // Asked in the optimizer window for a video that could not be lightened. More careful than a pass: the video is
    // read again; every encoder that manages one second of it gets its turn (the one that failed on it last), first
    // with the graphics card decoding the original and then with the processor decoding it, since a file the card's
    // decoder trips over is common; a copy counts only when a helper player opens it and finds the right size and
    // about the length of the original. Each failed attempt goes to errors.log with mpv's own reason.
    async Task Careful(string path, CancellationToken token)
    {
        var name = Path.GetFileName(path);
        if (SourceKey(path) is not { } key) return;
        working = path; percent = 0;
        try
        {
            Say($"Повторное облегчение «{name}»: проверка видео…");
            Probe? old;
            lock (sync) index.TryGetValue(key, out old);
            var probe = await Read(path, token);
            if (old != null) probe.Software = old.Software;
            lock (sync) { index[key] = probe; SaveIndex(); }
            if (probe.Failed)
            {
                Fail(path, "Повторно: проигрыватель не смог открыть видео " + path, null, "проигрыватель не смог открыть видео: файл повреждён или его формат не поддерживается");
                Say($"«{name}» не облегчено: проигрыватель не смог открыть видео.");
                return;
            }
            if (CopyFor(path, out var plan) is not { } copy) { Say($"«{name}» облегчать не нужно: оно не больше экрана и не чаще ограничения кадров."); return; }
            var failedLast = old?.FailedWith;
            var order = Enumerable.Range(0, Encoders.Length).OrderBy(c => failedLast == Encoders[c].Name + Revision ? 1 : 0).ToList();
            var reasons = new List<(string Attempt, string Reason)>();
            foreach (var codec in order)
            {
                var encoderName = Encoders[codec].Name;
                Say($"Повторное облегчение «{name}»: проверка кодировщика {encoderName}…");
                if (!await Test(path, plan, codec, token)) continue;
                if (encoder < 0 || encoder >= Encoders.Length) encoder = codec;
                foreach (var (hwdec, how) in new[] { ("auto-copy", "декодирует видеокарта"), ("no", "декодирует процессор") })
                {
                    percent = 0;
                    var reason = await Encode(path, copy, plan, codec, false, hwdec, value => { percent = value; Say($"Повторное облегчение «{name}» ({encoderName}, {how}): {value}%"); }, token)
                        ?? await Verify(copy, plan, probe.Duration, token);
                    if (reason == null)
                    {
                        Say($"«{name}» облегчено со второй попытки: {encoderName}, {how}.");
                        Ready?.Invoke(path);
                        return;
                    }
                    try { File.Delete(copy); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    Program.Log(new IOException($"Повторно, {encoderName}, {how}: не удалось облегчить файл {path}: {reason}"));
                    reasons.Add(($"{encoderName}, {how}", reason));
                }
            }
            var summary = Summary(reasons);
            Fail(path, $"Повторно не удалось облегчить файл {path}: {summary}", encoder >= 0 && encoder < Encoders.Length ? Encoders[encoder].Name + Revision : null, summary);
            Say($"«{name}» не облегчено и со второй попытки: оно играет как есть. Подробности в errors.log.");
        }
        finally { working = null; }
    }
    // What went wrong on a careful retry: one reason when every attempt ended the same way.
    internal static string Summary(List<(string Attempt, string Reason)> attempts) =>
        attempts.Count == 0 ? "ни один кодировщик не справился даже с одной секундой этого видео"
        : attempts.Select(a => a.Reason).Distinct().Count() == 1 ? attempts[0].Reason + (attempts.Count > 1 ? $" (так при всех попытках: {attempts.Count})" : $" ({attempts[0].Attempt})")
        : string.Join("; ", attempts.Select(a => $"{a.Attempt}: {a.Reason}"));
    // A finished copy is opened by a helper player: it must have the planned size and about the original's length.
    async Task<string?> Verify(string copy, Plan plan, double duration, CancellationToken token)
    {
        var made = await Read(copy, token);
        if (made.Failed) return "проигрыватель не смог открыть готовую копию";
        if (made.Width != plan.Width || made.Height != plan.Height) return $"у копии размер {made.Width}×{made.Height} вместо {plan.Width}×{plan.Height}";
        return CopyTooShort(made.Duration, duration) ? $"копия короче оригинала: {made.Duration:0.#} с из {duration:0.#} с — возможно, файл видео повреждён" : null;
    }
    // Shorter than the original by more than 2 seconds and 5%: the encode stopped early. A soundtrack longer than the
    // picture makes the original a little longer than its copy, which has no sound.
    internal static bool CopyTooShort(double copy, double original) => original > 0 && copy < original - Math.Max(2, original * 0.05);
    Probe? ProbeOf(string path)
    {
        if (SourceKey(path) is not { } key) return null;
        lock (sync) return index.TryGetValue(key, out var probe) ? probe : null;
    }
    // A file that failed with an encoder other than the one found now.
    bool Retry(string path)
    {
        lock (sync) return SourceKey(path) is { } key && index.TryGetValue(key, out var probe) && probe.Failed && probe.FailedWith != null;
    }
    // message goes to errors.log; reason is shown in the optimizer window.
    void Fail(string path, string message, string? encoderName, string reason)
    {
        Program.Log(new IOException(message));
        lock (sync) { if (SourceKey(path) is { } key && index.TryGetValue(key, out var probe)) { probe.Failed = true; probe.FailedWith = encoderName; probe.Reason = reason; } SaveIndex(); }
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
            string? retry;
            if (SourceKey(path) is { } key) lock (sync) index.TryGetValue(key, out probe);
            lock (sync) retry = urgent;
            bool now = string.Equals(path, working, StringComparison.OrdinalIgnoreCase);
            // A video tried again shows as being worked on from the moment it is asked for.
            var state = now || string.Equals(path, retry, StringComparison.OrdinalIgnoreCase) ? State.Working
                : probe == null ? State.Unknown : probe.Failed ? State.Failed : probe.Keep ? State.Kept
                : CopyFor(path, out _) is not { } copy ? State.Light : File.Exists(copy) ? State.Ready : State.Waiting;
            result.Add(new Item(path, state, now ? percent : 0, probe?.Failed == true ? probe.Reason : null));
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
                    double rotate = await Number(pipe, "current-tracks/video/demux-rotate"), fps = await Number(pipe, "current-tracks/video/demux-fps"), duration = await Number(pipe, "duration");
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
                        Width = (int)width, Height = (int)height, Fps = fps, Duration = duration, Keep = rotate % 360 != 0 || gamma is "pq" or "hlg",
                        Colors = colors.Count == 3 && gamma.Length > 0 && gamma != "auto" ? string.Join(":", colors.Append(gamma)) : ""
                    };
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException && !token.IsCancellationRequested) { }
            }
            return new Probe { Failed = true };
        }
        finally { Kill(process); }
    }
    // The copy gets exactly the pixels the player would draw from the original. The player (mpv, profile "fast")
    // shrinks a video with the graphics card's two-tap bilinear sampling and no anti-aliasing: a thin outline keeps
    // its full darkness and colour, with small steps on slopes. A smoothing filter (lanczos, bicubic) mixes outlines
    // with their surroundings instead, so the copy looked blurred, greyer and duller, the more so the smaller the
    // screen. FFmpeg's fast_bilinear is the same two-tap filter, but its quick 8-bit path samples half a pixel off
    // and on smaller screens falls on every n-th source pixel only; with a 16-bit result it takes the centred filter
    // the graphics card samples with (checked against the card's sampling: at most 1 level apart), and the result
    // is rounded to 8 bits without dither noise. A copy of the original size is not scaled at all.
    // The encoder always gets NV12, the one layout every encoder here takes: Media Foundation accepts only the exact
    // format its encoder offers, and the graphics card's encoder offers NV12 alone (with I420 it ended with "format
    // negotiation failed" and Windows' software encoder was used instead).
    internal static string ScaleGraph(Plan plan, bool resize) =>
        string.Join(",", new[]
        {
            plan.Step > 1 ? $"framestep={plan.Step}" : "",
            resize ? $"scale={plan.Width}:{plan.Height}:flags=fast_bilinear+accurate_rnd,format=yuv420p16le,scale=flags=accurate_rnd:sws_dither=none" : "",
            "format=nv12",
        }.Where(part => part.Length > 0));
    // Encoding mode of mpv: the graphics card decodes (hwdec "auto-copy"; "no" decodes on the processor), every n-th
    // frame is kept and scaled, the encoder writes an MP4 next to the final name, which it gets only when it is
    // complete. A test encodes 1 second. Returns null when the copy is made, otherwise why not (mpv's own errors).
    async Task<string?> Encode(string path, string output, Plan plan, int codec, bool test, string hwdec, Action<int>? progress, CancellationToken token)
    {
        var part = output + ".part";
        var pipe = $"wallsets-encode-{Environment.ProcessId}-{Interlocked.Increment(ref requests)}";
        var probe = ProbeOf(path);
        var graph = ScaleGraph(plan, probe == null || probe.Width != plan.Width || probe.Height != plan.Height);
        // Only the picture: the player takes the sound from the original file. It also avoids an mpv 0.41 hang when
        // a soundtrack starts a little after the picture.
        var args = new List<string> { "--hwdec=" + hwdec, "--sid=no", "--aid=no", "--o=" + part, "--of=mp4", "--ofopts=movflags=+faststart", "--ovc=" + Encoders[codec].Codec, "--ovcopts=" + Encoders[codec].Options + (Encoders[codec].Bitrate ? ",b=" + BitrateFor(plan, probe?.Fps ?? 0) : ""), "--vf=" + Player.FilterChain(graph), "--input-ipc-server=" + pipe };
        // mpv's log gives the reason when the copy cannot be made; it is deleted afterwards.
        var log = output + ".log";
        args.Add("--log-file=" + log);
        if (test) args.Add("--length=1");
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
                var errors = Errors(log);
                var what = stalled ? $"пережатие остановилось на {Math.Max(0, last):0}% и две минуты не продвигалось"
                    : process.ExitCode != 0 ? $"mpv завершился с кодом {process.ExitCode}" : "файл копии не дописан до конца";
                return errors.Length > 0 ? $"{what}: {errors}" : what + ".";
            }
            if (!test) File.Move(part, output, true);
            return null;
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
    // The last errors of mpv's log of a failed encode ("" when there are none), and the NVIDIA driver version NVENC
    // asks for.
    string Errors(string log)
    {
        try
        {
            var lines = ErrorLines(File.Exists(log) ? File.ReadLines(log) : []);
            foreach (var line in lines) if (DriverFrom(line) is { } version) driver = version;
            return string.Join(" | ", lines.TakeLast(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }
    // Errors and fatal messages, without their time. Messages of the video decoder (its hardware part falls back to
    // the processor on its own) are left out.
    internal static List<string> ErrorLines(IEnumerable<string> log) =>
        log.Where(l => (l.Contains("][e][") || l.Contains("][f][")) && !Noise.IsMatch(l))
            .Select(l => l[(l.IndexOf("][", l.IndexOf(']')) + 1)..].Trim()).Distinct().ToList();
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
