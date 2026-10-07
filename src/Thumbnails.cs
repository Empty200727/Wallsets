using System.Collections.Concurrent;
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
    readonly BlockingCollection<(string Path, TaskCompletionSource<byte[]?> Result)> requests = new();
    readonly string cache = Path.Combine(Program.Root, ".cache", "thumbnails");
    bool disposed;
    public Thumbnails()
    {
        Directory.CreateDirectory(cache);
        var thread = new Thread(Work) { IsBackground = true, Name = "Wallpaper thumbnails", Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
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
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0) return null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            if (factory.GetImage(new SIZE { Width = 384, Height = 216 }, 0x8, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            using var source = Image.FromHbitmap(bitmap);
            using var output = new Bitmap(384, 216);
            using (var g = Graphics.FromImage(output))
            {
                g.Clear(Color.FromArgb(31, 34, 38));
                float scale = Math.Min(384f / source.Width, 216f / source.Height);
                float width = source.Width * scale, height = source.Height * scale;
                g.DrawImage(source, (384 - width) / 2, (216 - height) / 2, width, height);
            }
            using var stream = new MemoryStream(); output.Save(stream, ImageFormat.Jpeg);
            var data = stream.ToArray(); File.WriteAllBytes(destination, data); return data;
        }
        finally { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); Marshal.ReleaseComObject(factory); }
    }
    public void Dispose() { disposed = true; requests.CompleteAdding(); }
}
