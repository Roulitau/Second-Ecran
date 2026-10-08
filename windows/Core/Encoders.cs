namespace SecondEcran;

public readonly record struct Frame(bool Key, byte[] Data);

public static class Encoders
{
    public static readonly Dictionary<string, (string pix, string[][] variants)> All = new()
    {
        ["h264_nvenc"] = ("nv12", new[]
        {
            new[] { "-preset", "p1", "-tune", "ll", "-rc", "cbr", "-zerolatency", "1", "-bf", "0" },
            new[] { "-preset", "llhp", "-rc", "cbr", "-bf", "0" },
            Array.Empty<string>(),
        }),
        ["h264_qsv"] = ("nv12", new[]
        {
            new[] { "-preset", "veryfast", "-look_ahead", "0", "-bf", "0" },
            Array.Empty<string>(),
        }),
        ["h264_amf"] = ("nv12", new[]
        {
            new[] { "-usage", "ultralowlatency", "-quality", "speed" },
            Array.Empty<string>(),
        }),
        ["libx264"] = ("yuv420p", new[]
        {
            new[] { "-preset", "ultrafast", "-tune", "zerolatency", "-x264-params", "bframes=0:slices=1:repeat-headers=1" },
        }),
    };

    public static readonly string[] AutoOrder = { "h264_nvenc", "h264_qsv", "h264_amf", "libx264" };

    static bool Test(string ffmpeg, string enc, string[] extra)
    {
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
                                      "-frames:v", "5", "-c:v", enc };
        args.AddRange(extra);
        args.AddRange(new[] { "-pix_fmt", All[enc].pix, "-f", "null", "-" });
        try { return Tools.Run(ffmpeg, args, 20000).code == 0; }
        catch { return false; }
    }

    /// <summary>Premier encodeur qui fonctionne vraiment (test d'encodage réel).</summary>
    public static (string? name, string[] extra) Pick(string ffmpeg, string wanted)
    {
        var order = wanted == "auto" ? AutoOrder : new[] { wanted };
        foreach (var enc in order)
            foreach (var extra in All[enc].variants)
                if (Test(ffmpeg, enc, extra)) return (enc, extra);
        return (null, Array.Empty<string>());
    }

    public static int Even(int n) => Math.Max(2, n / 2 * 2);

    public static List<string> BuildArgs(MonitorInfo mon, int outW, int outH, int fps, int bitrateKbps,
                                         string? enc, string[] extra, bool testSource, string mode, int webQuality)
    {
        var a = new List<string> { "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer" };
        if (testSource)
            a.AddRange(new[] { "-re", "-f", "lavfi", "-i", $"testsrc2=size={mon.W}x{mon.H}:rate={fps}" });
        else
            a.AddRange(new[] { "-f", "gdigrab", "-framerate", fps.ToString(), "-draw_mouse", "1",
                               "-offset_x", mon.X.ToString(), "-offset_y", mon.Y.ToString(),
                               "-video_size", $"{mon.W}x{mon.H}", "-i", "desktop" });
        if (outW != mon.W || outH != mon.H)
            a.AddRange(new[] { "-vf", $"scale={outW}:{outH}" });
        if (mode == "mjpeg")
        {
            a.AddRange(new[] { "-c:v", "mjpeg", "-q:v", webQuality.ToString(), "-pix_fmt", "yuvj420p",
                               "-f", "image2pipe", "pipe:1" });
        }
        else
        {
            a.AddRange(new[] { "-c:v", enc! });
            a.AddRange(extra);
            a.AddRange(new[] { "-pix_fmt", All[enc!].pix,
                               "-b:v", bitrateKbps + "k", "-maxrate", bitrateKbps + "k",
                               "-bufsize", Math.Max(bitrateKbps / 2, 500) + "k",
                               "-g", (fps * 2).ToString(),
                               "-bsf:v", "dump_extra=freq=keyframe",
                               "-f", "h264", "pipe:1" });
        }
        return a;
    }
}
