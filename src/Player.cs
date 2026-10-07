using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace Wallsets;

internal sealed class Player : IDisposable
{
    Process? process;
    IntPtr renderer;
    IntPtr job;
    readonly SemaphoreSlim gate = new(1);
    readonly string root;
    public string PipeName { get; } = "wallsets-mpv-" + Environment.ProcessId;
    public int? Pid => process is { HasExited: false } ? process.Id : null;
    public bool Running => Pid != null;
    public bool Attached => renderer != IntPtr.Zero && Native.IsWindow(renderer) && Native.WindowClass(Native.GetParent(renderer)) == "WorkerW";
    public int Starts { get; private set; }
    public object WindowInfo => Native.DescribeWindow(renderer);
    // Applied when the player starts; while it runs they are changed through SetSound and SetVolume.
    public bool Sound { get; set; }
    public int Volume { get; set; } = 50;
    // fit, stretch, fill or tile; applied to every loaded file by ApplyScaling.
    public string Scaling { get; set; } = "fill";
    // Last values sent to mpv, so unchanged options are not sent (and the video is not refiltered) again.
    readonly Dictionary<string, string> applied = [];
    public Player(string root) { this.root = root; }

    public async Task Start()
    {
        if (Running && Attached) return;
        Stop();
        job = Native.CreateJobObject(IntPtr.Zero, null);
        var limits = new Native.JOBINFO { Basic = new Native.BASICLIMIT { Flags = 0x2000 } };
        if (job == IntPtr.Zero || !Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JOBINFO>()))
            throw new InvalidOperationException("Не удалось создать группу процессов обоев.");
        var start = new ProcessStartInfo(Path.Combine(root, "engine", "mpv.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
        string[] args = ["--no-config", "--idle=yes", "--force-window=yes", "--keep-open=yes", "--loop-file=inf", "--aid=" + (Sound ? "auto" : "no"), "--volume=" + Volume, "--volume-max=100", "--audio-client-name=Wallsets", "--hwdec=auto", "--vo=gpu", "--gpu-api=d3d11", "--gpu-context=d3d11", "--profile=fast", "--interpolation=no", "--video-sync=audio", "--osc=no", "--osd-level=0", "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no", "--cursor-autohide=no", "--stop-screensaver=no", "--image-display-duration=inf", "--keepaspect=" + (Scaling == "stretch" ? "no" : "yes"), "--panscan=" + (Scaling == "fill" ? "1" : "0"), "--terminal=no", "--msg-level=all=warn", "--wid=0", "--input-ipc-server=" + PipeName];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        applied.Clear();
        applied["keepaspect"] = Scaling == "stretch" ? "no" : "yes"; applied["panscan"] = Scaling == "fill" ? "1" : "0";
        applied["video-unscaled"] = "no"; applied["video-align-x"] = applied["video-align-y"] = "0"; applied["vf"] = "";
        process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проигрыватель.");
        Starts++;
        if (!Native.AssignProcessToJobObject(job, process.Handle)) { Stop(); throw new InvalidOperationException("Не удалось включить автоматическое завершение проигрывателя."); }
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        for (int i = 0; i < 30; i++)
        {
            if (process.HasExited) throw new InvalidOperationException("Проигрыватель завершился. Проверьте файл engine/mpv.exe.");
            try
            {
                await Command("get_property", "mpv-version");
                renderer = Native.FindDesktopPlayer(process.Id);
                if (Attached) return;
                await Task.Delay(100);
            }
            catch (TimeoutException) { await Task.Delay(100); }
            catch (IOException) { await Task.Delay(100); }
        }
        Stop();
        throw new TimeoutException("Не удалось подключить видео к слою рабочего стола. Воспроизведение остановлено.");
    }

    // Does not need the UI thread, so the shutdown handler can wait for it.
    public async Task<JsonElement> Command(params object[] command)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500, timeout.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = 1 })).ConfigureAwait(false);
            while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var r = json.RootElement;
                if (!r.TryGetProperty("request_id", out _)) continue;
                if (r.GetProperty("error").GetString() != "success") throw new InvalidOperationException(r.GetProperty("error").GetString());
                return r.TryGetProperty("data", out var data) ? data.Clone() : default;
            }
            throw new IOException("Соединение с проигрывателем закрыто.");
        }
        finally { gate.Release(); }
    }

    public async Task Load(string path, bool paused)
    {
        await Start();
        await Command("set_property", "pause", paused);
        await Command("loadfile", path, "replace");
        await ApplyScaling(path);
    }
    // Fit, stretch and fill are mpv options. Tile has none: a picture smaller than the screen is repeated
    // by a filter, a larger one is shown at its own size from the top left corner, like in Windows.
    public async Task ApplyScaling(string? path = null)
    {
        string unscaled = "no", align = "0", vf = "";
        if (Scaling == "tile" && Native.GetWindowRect(renderer, out var screen))
        {
            var (width, height) = await MediaSize(path);
            int screenWidth = screen.Right - screen.Left, screenHeight = screen.Bottom - screen.Top;
            if (width > 0 && height > 0 && screenWidth > 0 && screenHeight > 0)
            {
                if (width >= screenWidth && height >= screenHeight) { unscaled = "yes"; align = "-1"; }
                else vf = TileFilter(width, height, screenWidth, screenHeight);
            }
        }
        await Set("keepaspect", Scaling == "stretch" ? "no" : "yes");
        await Set("panscan", Scaling == "fill" ? "1" : "0");
        await Set("video-unscaled", unscaled);
        await Set("video-align-x", align);
        await Set("video-align-y", align);
        await Set("vf", vf);
    }
    async Task Set(string name, string value)
    {
        if (applied.TryGetValue(name, out var old) && old == value) return;
        await Command("set_property", name, value);
        applied[name] = value;
    }
    async Task<(int Width, int Height)> MediaSize(string? path)
    {
        for (int i = 0; i < 60; i++)
        {
            var current = await OptionalProperty("path");
            if (path == null || current.ValueKind == JsonValueKind.String && current.GetString() == path)
                foreach (var (w, h) in new[] { ("current-tracks/video/demux-w", "current-tracks/video/demux-h"), ("width", "height") })
                {
                    var width = await OptionalProperty(w); var height = await OptionalProperty(h);
                    if (width.ValueKind == JsonValueKind.Number && height.ValueKind == JsonValueKind.Number && width.GetInt32() > 0 && height.GetInt32() > 0)
                        return (width.GetInt32(), height.GetInt32());
                }
            await Task.Delay(50);
        }
        return (0, 0);
    }
    // lavfi graph: copies side by side, then rows of them, cut to the screen size. The graph has
    // brackets, so it is passed with mpv's %length% quoting.
    public static string TileFilter(int width, int height, int screenWidth, int screenHeight)
    {
        int columns = Math.Clamp((screenWidth + width - 1) / width, 1, 64), rows = Math.Clamp((screenHeight + height - 1) / height, 1, 64);
        var graph = new StringBuilder();
        if (columns > 1)
        {
            var labels = string.Concat(Enumerable.Range(0, columns).Select(i => $"[c{i}]"));
            graph.Append($"split={columns}{labels};{labels}hstack=inputs={columns}");
        }
        if (rows > 1)
        {
            var labels = string.Concat(Enumerable.Range(0, rows).Select(i => $"[r{i}]"));
            graph.Append(graph.Length > 0 ? "," : "").Append($"split={rows}{labels};{labels}vstack=inputs={rows}");
        }
        graph.Append(graph.Length > 0 ? "," : "").Append($"crop={Math.Min(screenWidth, columns * width)}:{Math.Min(screenHeight, rows * height)}:0:0");
        return $"lavfi=graph=%{Encoding.UTF8.GetByteCount(graph.ToString())}%{graph}";
    }
    // Current frame as shown, after scaling filters (used as the Windows background).
    public Task SaveFrame(string file) => Command("screenshot-to-file", file, "video");
    public Task Pause(bool paused) => Command("set_property", "pause", paused);
    // "auto" picks the file's soundtrack again, also for the files loaded later; "no" closes the audio output.
    public Task SetSound(bool on) => Command("set_property", "aid", on ? "auto" : "no");
    // mpv's own software volume: it does not change the Windows volume or other programs.
    public Task SetVolume(int value) => Command("set_property", "volume", value);
    public async Task<JsonElement> OptionalProperty(string name)
    {
        try { return await Command("get_property", name); }
        catch (InvalidOperationException ex) when (ex.Message == "property unavailable") { return default; }
    }
    public void Stop()
    {
        if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
        try { if (process is { HasExited: false }) { process.Kill(); process.WaitForExit(2000); } } catch { }
        process?.Dispose(); process = null;
        renderer = IntPtr.Zero;
    }
    public void Dispose() => Stop();
}
