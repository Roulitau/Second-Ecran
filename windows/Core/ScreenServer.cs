using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SecondEcran;

public sealed class ServerConfig
{
    public int MonitorIndex = -1;          // -1 = automatique
    public int Fps = 60;
    public int BitrateKbps = 8000;
    public int MaxWidth = 0;               // 0 = natif
    public string Encoder = "auto";
    public int Port = 5555;
    public bool TestSource = false;
    public int WebQuality = 5;             // JPEG ffmpeg : 2 (très bon) .. 15 (léger)
}

/// <summary>
/// Capture un écran avec ffmpeg et l'envoie à la tablette, sur les réseaux privés
/// (câble USB : partage de connexion USB de la tablette, ou `adb reverse` si le débogage est actif) :
///  - app Android : protocole binaire [type:1][longueur:4][charge] avec H.264,
///  - navigateur : page web + WebSocket (H.264 WebCodecs, ou JPEG en secours).
/// </summary>
public sealed class ScreenServer
{
    const int T_INFO = 0x01, T_VIDEO = 0x02, T_HELLO = 0x10, T_TOUCH = 0x11;
    const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    readonly ServerConfig _cfg;
    readonly SemaphoreSlim _busy = new(1, 1);
    volatile bool _stop;
    TcpListener? _listener;
    Thread? _thread;

    string _ffmpeg = "";
    MonitorInfo _mon = new(0, 0, 1280, 720, true);
    string? _enc;
    string[] _extra = Array.Empty<string>();
    int _outW, _outH;
    MouseInput? _input;
    byte[]? _page;

    public event Action<string>? Log;
    public event Action? Stopped;
    public bool Running => _thread != null && _thread.IsAlive;

    public ScreenServer(ServerConfig cfg) { _cfg = cfg; }

    void Say(string s) => Log?.Invoke(s);

    public void Start()
    {
        _stop = false;
        _thread = new Thread(Serve) { IsBackground = true, Name = "serveur" };
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        try { _listener?.Stop(); } catch { }
    }

    // ---- démarrage -----------------------------------------------------------------------
    void Serve()
    {
        try
        {
            var ff = Tools.FindFfmpeg();
            if (ff == null)
            {
                Say("⚠ ffmpeg introuvable : mets ffmpeg.exe à côté de ce programme (ou winget install Gyan.FFmpeg).");
                return;
            }
            _ffmpeg = ff;
            Say("ffmpeg : " + ff);

            var mons = Monitors.List();
            int idx = _cfg.MonitorIndex >= 0 ? _cfg.MonitorIndex : Monitors.DefaultIndex(mons);
            if (idx < 0 || idx >= mons.Count)
            {
                Say($"⚠ Écran {idx} inexistant ({mons.Count} écran(s) détecté(s)).");
                return;
            }
            _mon = mons[idx];

            Say("Test des encodeurs…");
            (_enc, _extra) = Encoders.Pick(_ffmpeg, _cfg.Encoder);
            if (_enc == null) Say("⚠ Aucun encodeur H.264 ne marche : H.264 indisponible, JPEG utilisé.");

            int ow = Encoders.Even(_mon.W), oh = Encoders.Even(_mon.H);
            if (_cfg.MaxWidth > 0 && ow > _cfg.MaxWidth)
            {
                double ratio = (double)_cfg.MaxWidth / ow;
                ow = Encoders.Even(_cfg.MaxWidth);
                oh = Encoders.Even((int)(oh * ratio));
            }
            _outW = ow; _outH = oh;
            Say($"Écran {idx} : {_mon.W}x{_mon.H} -> envoi en {ow}x{oh}, {_cfg.Fps} ips, encodeur : {_enc ?? "aucun"}");

            _input = new MouseInput(_mon, Say);
            _page = LoadPage();

            _listener = new TcpListener(IPAddress.Any, _cfg.Port);
            try { _listener.Start(5); }
            catch (SocketException e)
            {
                Say($"⚠ Port {_cfg.Port} indisponible ({e.Message}).");
                return;
            }
            Say($"En attente sur le port {_cfg.Port} (câble USB, réseau privé uniquement).");
            foreach (var a in LocalAddresses()) Say($"  Adresse du PC : {a}:{_cfg.Port}");
            if (OperatingSystem.IsWindows())
            {
                // le PC ne se met pas en veille tant que le serveur tourne (la capture s'arrêterait)
                Native.SetThreadExecutionState(0x80000000u | 0x00000001u);
                Say("PC maintenu éveillé tant que le serveur tourne.");
            }
            if (!_cfg.TestSource)
            {
                var (ok, msg) = Tools.AdbReverse(_cfg.Port);
                Say(ok ? "✓ " + msg : "ℹ Débogage USB non utilisé (partage de connexion USB OK).");
                new Thread(() => KeepUsbAlive(ok)) { IsBackground = true, Name = "usb" }.Start();
            }

            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { break; }
                if (!IsPrivate(c.Client.RemoteEndPoint as IPEndPoint)) { try { c.Close(); } catch { } continue; }
                new Thread(() => HandleConn(c)) { IsBackground = true }.Start();
            }
        }
        catch (Exception e)
        {
            Say("⚠ Erreur : " + e.Message);
        }
        finally
        {
            if (OperatingSystem.IsWindows()) Native.SetThreadExecutionState(0x80000000u);
            try { _listener?.Stop(); } catch { }
            Say("Serveur arrêté.");
            Stopped?.Invoke();
        }
    }

    /// <summary>
    /// Après une veille du PC ou un câble rebranché, `adb reverse` est perdu : on le remet
    /// tout seul toutes les 4 s, en ne signalant que les changements d'état.
    /// </summary>
    void KeepUsbAlive(bool lastOk)
    {
        while (!_stop)
        {
            for (int i = 0; i < 8 && !_stop; i++) Thread.Sleep(500);
            if (_stop) break;
            var (ok, _) = Tools.AdbReverse(_cfg.Port);
            if (ok != lastOk)
            {
                if (ok) Say("✓ Câble USB reconnecté (débogage).");
                lastOk = ok;
            }
        }
    }

    /// <summary>Seuls le PC lui-même et les réseaux privés (câble/partage, Wi-Fi maison) sont acceptés.</summary>
    static bool IsPrivate(IPEndPoint? ep)
    {
        if (ep == null) return false;
        var ip = ep.Address;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    public static List<string> LocalAddresses()
    {
        var r = new List<string>();
        try
        {
            foreach (var n in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                        r.Add(u.Address.ToString());
            }
        }
        catch { }
        return r;
    }

    static byte[] LoadPage()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("index.html")
                      ?? throw new InvalidOperationException("index.html absent");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- aiguillage ----------------------------------------------------------------------
    void HandleConn(TcpClient c)
    {
        try
        {
            c.NoDelay = true;
            c.ReceiveTimeout = 10000;
            var peek = new byte[1];
            if (c.Client.Receive(peek, SocketFlags.Peek) <= 0) return;
            c.ReceiveTimeout = 0;
            var ns = c.GetStream();
            if (peek[0] == T_HELLO) AppConn(ns);
            else HttpConn(ns);
        }
        catch (Exception e)
        {
            if (!_stop) Say("Connexion : " + e.Message);
        }
        finally
        {
            try { c.Close(); } catch { }
        }
    }

    static void SendMsg(Stream s, int type, byte[] payload)
    {
        var all = new byte[5 + payload.Length];
        all[0] = (byte)type;
        BinaryPrimitives.WriteUInt32BigEndian(all.AsSpan(1), (uint)payload.Length);
        Buffer.BlockCopy(payload, 0, all, 5, payload.Length);
        s.Write(all, 0, all.Length);
        s.Flush();
    }

    string InfoJson(string? fmt = null)
        => fmt == null
            ? JsonSerializer.Serialize(new { w = _outW, h = _outH, fps = _cfg.Fps })
            : JsonSerializer.Serialize(new { w = _outW, h = _outH, fps = _cfg.Fps, fmt });

    // ---- app Android -----------------------------------------------------------------------
    void AppConn(NetworkStream ns)
    {
        var head = Net.ReadExact(ns, 5);
        int len = (int)BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(1));
        if (len < 0 || len > 100000) return;
        var hello = Net.ReadExact(ns, len);
        string who = "";
        try
        {
            using var d = JsonDocument.Parse(hello);
            if (d.RootElement.TryGetProperty("w", out var w) && d.RootElement.TryGetProperty("h", out var h))
                who = $" (écran tablette {w}x{h})";
        }
        catch { }

        void Refuse(string text)
        {
            SendMsg(ns, T_INFO, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = text })));
            Say("App refusée : " + text);
        }

        if (_enc == null) { Refuse("Pas d'encodeur H.264 sur le PC (utilise le navigateur)"); return; }
        if (!_busy.Wait(0)) { Refuse("Déjà utilisé par un autre appareil"); return; }
        try
        {
            Say("App Android connectée" + who);
            SendMsg(ns, T_INFO, Encoding.UTF8.GetBytes(InfoJson()));
            RunStream("h264", ns,
                f => SendMsg(ns, T_VIDEO, f.Data),
                done =>
                {
                    while (!done.IsSet)
                    {
                        var h = Net.ReadExact(ns, 5);
                        int n = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(1));
                        if (n < 0 || n > 1_000_000) throw new IOException("paquet invalide");
                        var payload = Net.ReadExact(ns, n);
                        if (h[0] == T_TOUCH) Touch(payload);
                    }
                }, 20);
        }
        finally
        {
            _busy.Release();
            Say("App déconnectée. En attente…");
        }
    }

    void Touch(byte[] payload)
    {
        try { _input?.Handle(payload); }
        catch (Exception e) { Say("tactile ignoré : " + e.Message); }
    }

    // ---- navigateur ------------------------------------------------------------------------
    void HttpConn(NetworkStream ns)
    {
        var data = new List<byte>();
        var one = new byte[1];
        // lecture de l'en-tête jusqu'à \r\n\r\n
        while (true)
        {
            int r = ns.Read(one, 0, 1);
            if (r <= 0) return;
            data.Add(one[0]);
            int c = data.Count;
            if (c >= 4 && data[c - 1] == '\n' && data[c - 2] == '\r' && data[c - 3] == '\n' && data[c - 4] == '\r') break;
            if (c > 16384) return;
        }
        var lines = Encoding.Latin1.GetString(data.ToArray()).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2) return;
        string target = parts[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int i = line.IndexOf(':');
            if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
        }
        string path = target, query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }
        var prm = new Dictionary<string, string>();
        foreach (var kv in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = kv.Split('=', 2);
            prm[p[0]] = p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "";
        }

        if (path == "/ws" && headers.TryGetValue("Upgrade", out var up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            WsConn(ns, headers, prm);
        }
        else if (path == "/" || path == "/index.html")
        {
            Say("Page demandée par le navigateur");
            var head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\nConnection: close\r\nContent-Length: " + _page!.Length + "\r\n\r\n");
            ns.Write(head, 0, head.Length);
            ns.Write(_page, 0, _page.Length);
            ns.Flush();
        }
        else
        {
            var r404 = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            ns.Write(r404, 0, r404.Length);
        }
    }

    void WsConn(NetworkStream ns, Dictionary<string, string> headers, Dictionary<string, string> prm)
    {
        headers.TryGetValue("Sec-WebSocket-Key", out var key);
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes((key ?? "") + WsGuid)));
        var resp = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n" +
            "Connection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n");
        ns.Write(resp, 0, resp.Length);
        ns.Flush();
        var ws = new WebSocketLite(ns);

        if (!_busy.Wait(0))
        {
            ws.SendText(JsonSerializer.Serialize(new { error = "Déjà utilisé par un autre appareil", needCode = false }));
            Say("Navigateur refusé : déjà utilisé par un autre appareil");
            return;
        }
        try
        {
            Say("Navigateur connecté");
            string want = prm.TryGetValue("fmt", out var f) ? f : "jpeg";
            string fmt = want == "h264" && _enc != null ? "h264" : "jpeg";
            ws.SendText(InfoJson(fmt));
            Say("Format navigateur : " + (fmt == "h264" ? "H.264 (WebCodecs)" : "JPEG"));

            void RecvLoop(ManualResetEventSlim done)
            {
                while (!done.IsSet)
                {
                    var (op, payload) = ws.Receive();
                    if (op != 0x1) continue;
                    try
                    {
                        using var d = JsonDocument.Parse(payload);
                        if (d.RootElement.TryGetProperty("a", out var a) && a.GetString() == "hello")
                        {
                            if (d.RootElement.TryGetProperty("w", out var w) && d.RootElement.TryGetProperty("h", out var h))
                                Say($"Navigateur : écran {w}x{h}");
                            continue;
                        }
                    }
                    catch { continue; }
                    Touch(payload);
                }
            }

            if (fmt == "h264")
                RunStream("h264", ns, fr => ws.SendBinary(Prefix(fr)), RecvLoop, 6);
            else
                RunStream("mjpeg", ns, fr => ws.SendBinary(fr.Data), RecvLoop, 2);
        }
        finally
        {
            _busy.Release();
            Say("Navigateur déconnecté. En attente…");
        }
    }

    static byte[] Prefix(Frame f)
    {
        var o = new byte[f.Data.Length + 1];
        o[0] = f.Key ? (byte)1 : (byte)0;
        Buffer.BlockCopy(f.Data, 0, o, 1, f.Data.Length);
        return o;
    }

    // ---- pipeline commun : ffmpeg -> file -> réseau ---------------------------------------
    void RunStream(string mode, NetworkStream ns, Action<Frame> send, Action<ManualResetEventSlim> recvLoop, int maxQ)
    {
        var args = Encoders.BuildArgs(_mon, _outW, _outH, _cfg.Fps, _cfg.BitrateKbps, _enc, _extra,
                                      _cfg.TestSource, mode, _cfg.WebQuality);
        var psi = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) Say("ffmpeg: " + e.Data); };
        proc.BeginErrorReadLine();

        var done = new ManualResetEventSlim(false);
        var q = new FrameQueue();
        var source = proc.StandardOutput.BaseStream;

        new Thread(() =>
        {
            try
            {
                bool skipping = false;
                var frames = mode == "h264" ? Splitters.AccessUnits(source) : Splitters.Jpegs(source);
                foreach (var f in frames)
                {
                    if (done.IsSet) break;
                    if (skipping && !f.Key) continue;
                    if (q.Count > maxQ) { q.Clear(); skipping = !f.Key; }
                    else skipping = false;
                    q.Put(f);
                }
            }
            catch { }
            finally { q.Complete(); }
        }) { IsBackground = true }.Start();

        new Thread(() =>
        {
            try { while (q.Take(out var f)) send(f); }
            catch { }
            finally { done.Set(); }
        }) { IsBackground = true }.Start();

        new Thread(() =>
        {
            try { recvLoop(done); }
            catch { }
            finally { done.Set(); q.Complete(); }
        }) { IsBackground = true }.Start();

        try
        {
            while (!done.IsSet && !_stop)
            {
                if (proc.HasExited) { Say("ffmpeg s'est arrêté."); break; }
                done.Wait(300);
            }
        }
        finally
        {
            done.Set();
            try { proc.Kill(true); proc.WaitForExit(3000); } catch { }
            try { ns.Close(); } catch { }
            q.Complete();
        }
    }
}
