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
        string[] args = ["--no-config", "--idle=yes", "--force-window=yes", "--keep-open=yes", "--loop-file=inf", "--aid=" + (Sound ? "auto" : "no"), "--volume=" + Volume, "--volume-max=100", "--audio-client-name=Wallsets", "--hwdec=auto", "--vo=gpu", "--gpu-api=d3d11", "--gpu-context=d3d11", "--profile=fast", "--interpolation=no", "--video-sync=audio", "--osc=no", "--osd-level=0", "--input-default-bindings=no", "--input-vo-keyboard=no", "--input-cursor=no", "--cursor-autohide=no", "--stop-screensaver=no", "--image-display-duration=inf", "--panscan=1", "--terminal=no", "--msg-level=all=warn", "--wid=0", "--input-ipc-server=" + PipeName];
        foreach (var arg in args) start.ArgumentList.Add(arg);
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

    public async Task<JsonElement> Command(params object[] command)
    {
        await gate.WaitAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(500, timeout.Token);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new { command, request_id = 1 }));
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
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
    }
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
