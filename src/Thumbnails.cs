using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wallsets;

internal sealed class Thumbnails : IDisposable
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, uint flags, out IntPtr bitmap);
    }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int Width, Height; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    static readonly HashSet<string> Pictures = new(".gif .jpg .jpeg .png .bmp .webp .tif .tiff .avif .heic".Split(' '), StringComparer.OrdinalIgnoreCase);
    readonly BlockingCollection<(string Path, TaskCompletionSource<byte[]?> Result)> requests = new();
    readonly string cache = CacheFolder();
    bool disposed;
    public Thumbnails()
    {
        var thread = new Thread(Work) { IsBackground = true, Name = "Wallpaper thumbnails", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    static string CacheFolder()
    {
        // Next to the program by default; a read-only install folder falls back to the user's profile.
        foreach (var folder in new[] { Path.Combine(Program.Root, ".cache", "thumbnails"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallsets", "thumbnails") })
            try { Directory.CreateDirectory(folder); return folder; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.GetTempPath();
    }
    public Task<byte[]?> Get(string path)
    {
        if (disposed) return Task.FromResult<byte[]?>(null);
        var result = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests.Add((path, result)); return result.Task;
    }
    void Work()
    {
        foreach (var request in requests.GetConsumingEnumerable())
        {
            if (disposed) { request.Result.TrySetResult(null); continue; }
            try { request.Result.TrySetResult(Read(request.Path)); }
            catch { request.Result.TrySetResult(null); }
        }
    }
    byte[]? Read(string path)
    {
        var file = new FileInfo(path);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks)));
        var destination = Path.Combine(cache, key + ".jpg");
        if (File.Exists(destination)) return File.ReadAllBytes(destination);
        using var source = ShellImage(path) ?? EngineImage(path);
        if (source == null) return null;
        using var output = new Bitmap(384, 216);
        using (var g = Graphics.FromImage(output))
        {
            g.Clear(Color.FromArgb(31, 34, 38));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            float scale = Math.Min(384f / source.Width, 216f / source.Height);
            float width = source.Width * scale, height = source.Height * scale;
            g.DrawImage(source, (384 - width) / 2, (216 - height) / 2, width, height);
        }
        using var stream = new MemoryStream(); output.Save(stream, ImageFormat.Jpeg);
        var data = stream.ToArray();
        try { File.WriteAllBytes(destination, data); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return data;
    }
    static Bitmap? ShellImage(string path)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0) return null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            // Thumbnail only: no generic file icon when Windows has no preview for the format.
            if (factory.GetImage(new SIZE { Width = 384, Height = 216 }, 0x8, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            return Image.FromHbitmap(bitmap);
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); Marshal.ReleaseComObject(factory); }
    }
    // Windows has no thumbnails for some formats without extra codecs (HEVC, AV1, WebM, AVIF on Windows 10):
    // the bundled mpv saves one frame of such files instead.
    static Bitmap? EngineImage(string path)
    {
        var engine = Path.Combine(Program.Root, "engine", "mpv.exe");
        if (!File.Exists(engine)) return null;
        var folder = Path.Combine(Path.GetTempPath(), "wallsets-thumbnail-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var start = new ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "--no-config", "--terminal=no", "--msg-level=all=no", "--aid=no", "--sid=no", "--hwdec=no", "--vo=image", "--vo-image-format=jpg", "--vo-image-outdir=" + folder, "--frames=1" }) start.ArgumentList.Add(arg);
            if (!Pictures.Contains(Path.GetExtension(path))) start.ArgumentList.Add("--start=10%");
            start.ArgumentList.Add("--"); start.ArgumentList.Add(path);
            using var process = Process.Start(start);
            if (process == null) return null;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            if (!process.WaitForExit(20000)) { try { process.Kill(); } catch { } return null; }
            var frame = Directory.EnumerateFiles(folder).FirstOrDefault();
            if (frame == null) return null;
            using var stream = new MemoryStream(File.ReadAllBytes(frame));
            using var image = Image.FromStream(stream);
            return new Bitmap(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException) { return null; }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
    public void Dispose() { disposed = true; requests.CompleteAdding(); }
}
