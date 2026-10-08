using System.Diagnostics;
using System.Text;

namespace SecondEcran;

public static class Tools
{
    public static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory) ?? ".";

    public static (int code, string output) Run(string file, IEnumerable<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, "délai dépassé");
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString().Trim());
    }

    static string? FindInPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), name);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    static string? Glob(string root, string fileName)
    {
        try
        {
            if (!Directory.Exists(root)) return null;
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    public static string? FindFfmpeg()
    {
        var env = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var n in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var p = Path.Combine(AppDir, n);
            if (File.Exists(p)) return p;
        }
        var found = FindInPath("ffmpeg.exe") ?? FindInPath("ffmpeg");
        if (found != null) return found;
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            return Glob(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "ffmpeg.exe")
                ?? Glob(@"C:\ffmpeg", "ffmpeg.exe")
                ?? Glob(Path.Combine(Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData", "chocolatey", "bin"), "ffmpeg.exe");
        }
        return null;
    }

    public static string? FindAdb()
    {
        foreach (var n in new[] { "adb.exe", "adb" })
        {
            var p = Path.Combine(AppDir, n);
            if (File.Exists(p)) return p;
        }
        var found = FindInPath("adb.exe") ?? FindInPath("adb");
        if (found != null) return found;
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            return Glob(Path.Combine(local, "Microsoft", "WinGet", "Packages"), "adb.exe")
                ?? Glob(Path.Combine(local, "Android", "Sdk", "platform-tools"), "adb.exe");
        }
        return null;
    }

    /// <summary>`adb reverse` : la tablette voit le PC en 127.0.0.1 par le câble.</summary>
    public static (bool ok, string message) AdbReverse(int port)
    {
        var adb = FindAdb();
        if (adb == null)
            return (false, "adb introuvable (mets adb.exe à côté de l'exe, ou : winget install Google.PlatformTools)");
        try
        {
            var (code, output) = Run(adb, new[] { "reverse", $"tcp:{port}", $"tcp:{port}" }, 15000);
            if (code != 0)
                return (false, "Appareil non détecté ou non autorisé (" + (output.Length > 0 ? output : "erreur adb") +
                               "). Débogage USB activé ? Accepte la fenêtre sur la tablette.");
            return (true, "Câble USB prêt : sur la tablette, ouvre l'app (ou http://127.0.0.1:" + port + ")");
        }
        catch (Exception e)
        {
            return (false, "adb a échoué : " + e.Message);
        }
    }
}
