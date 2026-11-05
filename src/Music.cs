using System.Diagnostics;
using System.Text.Json;

namespace Wallsets;

// Own music of a wallpaper: an mpv without picture that repeats one file from the folder "Набор музыки". It runs
// apart from the video player, so a song does not start over each time a short video repeats, and it goes on
// when the next wallpaper has the same music.
internal sealed class MusicPlayer : IDisposable
{
    public static readonly HashSet<string> Extensions = new(".mp3 .flac .ogg .opus .m4a .aac .wav .wma .mka".Split(' '), StringComparer.OrdinalIgnoreCase);
    readonly string engine;
    readonly SemaphoreSlim gate = new(1);
    readonly string pipe = "wallsets-music-" + Environment.ProcessId;
    Process? process;
    IntPtr job;
    public string? Track { get; private set; }
    public MusicPlayer(string root) { engine = Path.Combine(root, "engine", "mpv.exe"); }
    bool Running => process is { HasExited: false };

    // Null stops the music. The pause is set before a new file is opened, so a paused wallpaper stays silent.
    public async Task Play(string? track, bool paused, int volume)
    {
        if (track == null) { Stop(); return; }
        if (!Running) await Start(volume);
        await Command("set_property", "pause", paused);
        if (!string.Equals(track, Track, StringComparison.OrdinalIgnoreCase)) { await Command("loadfile", track, "replace"); Track = track; }
    }
    public async Task Pause(bool paused) { if (Running && Track != null) await Command("set_property", "pause", paused); }
    public async Task SetVolume(int value) { if (Running) await Command("set_property", "volume", value); }

    async Task Start(int volume)
    {
        Stop();
        job = Native.KillOnCloseJob();
        var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true };
        string[] args = ["--no-config", "--idle=yes", "--vid=no", "--force-window=no", "--loop-file=inf", "--volume=" + volume, "--volume-max=100", "--audio-client-name=Wallsets",
            "--terminal=no", "--msg-level=all=warn", "--osc=no", "--input-default-bindings=no", "--load-scripts=no", "--ytdl=no", "--load-auto-profiles=no", "--load-stats-overlay=no",
            "--load-console=no", "--load-select=no", "--load-positioning=no", "--load-commands=no", "--media-controls=no", "--input-media-keys=no", "--input-ipc-server=" + pipe];
        foreach (var arg in args) start.ArgumentList.Add(arg);
        process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить проигрыватель музыки.");
        if (job != IntPtr.Zero) Native.AssignProcessToJobObject(job, process.Handle);
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        Native.Efficiency(process.Handle);
        for (int i = 0; i < 30; i++)
        {
            if (process.HasExited) throw new InvalidOperationException("Проигрыватель музыки завершился. Проверьте файл engine/mpv.exe.");
            try { await Command("get_property", "mpv-version"); return; }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException) { await Task.Delay(100); }
        }
        Stop();
        throw new TimeoutException("Проигрыватель музыки не отвечает.");
    }
    async Task<JsonElement> Command(params object[] command)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { return await Player.Send(pipe, command).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public void Stop()
    {
        Track = null;
        if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
        try { if (process is { HasExited: false }) { process.Kill(); process.WaitForExit(2000); } } catch { }
        process?.Dispose(); process = null;
    }
    public void Dispose() => Stop();
}
