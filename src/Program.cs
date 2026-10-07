using System.Drawing.Text;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Wallsets;

internal static class Program
{
    internal static readonly string Root = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    internal static readonly string Pipe = "Wallsets-control-" + Environment.UserName;
    static string? iconFont;
    // Windows 11 ships Segoe Fluent Icons; Windows 10 has the same glyph codes in Segoe MDL2 Assets.
    internal static string IconFont => iconFont ??= HasFont("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    static bool HasFont(string name)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == name);
    }
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { SelfTests.Run(Root); return; }
        using var mutex = new Mutex(true, "Local\\Wallsets-" + Environment.UserName, out var first);
        if (!first || args.Length > 0 && args[0] is "--stop" or "--next" or "--previous" or "--pause" or "--status" or "--rescan")
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Pipe, PipeDirection.InOut);
                pipe.Connect(2000);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                writer.WriteLine(args.Length > 0 ? args[0] : "--show");
                var result = reader.ReadLine();
                if (args.Contains("--status")) File.WriteAllText(Path.Combine(Root, "status.json"), result ?? "{}");
            }
            catch (Exception ex) { if (args.Contains("--status")) File.WriteAllText(Path.Combine(Root, "status.json"), JsonSerializer.Serialize(new { running = false, error = ex.Message })); }
            return;
        }
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception ?? new Exception("Unhandled error"));
        try { Application.Run(new MainForm(args.Contains("--tray"))); }
        catch (Exception ex) { Log(ex); MessageBox.Show(ex.Message, "Wallsets", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    internal static void Log(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(Root, "errors.log"), DateTime.Now.ToString("s") + " " + ex + Environment.NewLine); } catch { }
    }
}
