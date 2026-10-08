#!/usr/bin/env python3
"""
Second écran - application PC (Windows).

Capture un écran (idéalement un écran virtuel) avec ffmpeg et l'envoie à la tablette :
  - soit à l'app Android (flux H.264, protocole binaire),
  - soit à un navigateur (page web servie par ce programme, flux H.264 via WebCodecs, sinon JPEG).
Mode USB uniquement : le serveur n'écoute que sur 127.0.0.1 et `adb reverse` relie la tablette
au PC par le câble. Le tactile reçu est injecté comme souris sur le PC.

Usage :
    python secondscreen.py                 # interface graphique
    python secondscreen.py --cli           # sans interface
    python secondscreen.py --list-monitors
"""
import argparse
import base64
import hashlib
import hmac
import json
import os
import queue
import shutil
import socket
import struct
import subprocess
import sys
import threading
import time
import urllib.parse

from webpage import INDEX_HTML

PORT = 5555

# App Android - messages : [type:1 octet][longueur:4 octets big-endian][payload]
T_INFO = 0x01    # PC -> tablette : JSON {"w","h","fps"} ou {"error": "..."}
T_VIDEO = 0x02   # PC -> tablette : une image H.264 (Annex B)
T_HELLO = 0x10   # tablette -> PC : JSON {"v","w","h","pin"}
T_TOUCH = 0x11   # tablette -> PC : JSON {"a": ..., "x": ..., "y": ...}

START4 = b"\x00\x00\x00\x01"
WS_GUID = b"258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)


def log(*a):
    print(time.strftime("%H:%M:%S"), *a, flush=True)


# --------------------------------------------------------------------------
# Écrans
# --------------------------------------------------------------------------
def enable_dpi_awareness():
    if sys.platform != "win32":
        return
    import ctypes
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


def list_monitors():
    """Retourne [{'x','y','w','h','primary'}] en pixels physiques."""
    if sys.platform != "win32":
        return [{"x": 0, "y": 0, "w": 1280, "h": 720, "primary": True}]
    import ctypes
    from ctypes import wintypes

    class MONITORINFOEX(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.DWORD), ("rcMonitor", wintypes.RECT),
                    ("rcWork", wintypes.RECT), ("dwFlags", wintypes.DWORD),
                    ("szDevice", wintypes.WCHAR * 32)]

    user32 = ctypes.windll.user32
    user32.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(MONITORINFOEX)]
    proc_t = ctypes.WINFUNCTYPE(wintypes.BOOL, ctypes.c_void_p, ctypes.c_void_p,
                                ctypes.POINTER(wintypes.RECT), ctypes.c_void_p)
    found = []

    def cb(hmon, hdc, rect, lparam):
        info = MONITORINFOEX()
        info.cbSize = ctypes.sizeof(info)
        user32.GetMonitorInfoW(hmon, ctypes.byref(info))
        r = info.rcMonitor
        found.append({"x": r.left, "y": r.top, "w": r.right - r.left,
                      "h": r.bottom - r.top, "primary": bool(info.dwFlags & 1)})
        return True

    user32.EnumDisplayMonitors(None, None, proc_t(cb), 0)
    return found


def default_monitor_index(mons):
    """Par défaut : le dernier écran non principal (= l'écran virtuel), sinon le principal."""
    for i in range(len(mons) - 1, -1, -1):
        if not mons[i]["primary"]:
            return i
    return 0


# --------------------------------------------------------------------------
# adb (câble USB)
# --------------------------------------------------------------------------
def find_adb():
    here = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
    for name in ("adb.exe", "adb"):
        q = os.path.join(here, name)
        if os.path.isfile(q):
            return q
    p = shutil.which("adb")
    if p:
        return p
    if sys.platform == "win32":
        import glob
        la = os.environ.get("LOCALAPPDATA", "")
        for pat in (os.path.join(la, "Microsoft", "WinGet", "Links", "adb.exe"),
                    os.path.join(la, "Microsoft", "WinGet", "Packages", "Google.PlatformTools*",
                                 "**", "adb.exe"),
                    os.path.join(la, "Android", "Sdk", "platform-tools", "adb.exe"),
                    r"C:\platform-tools\adb.exe"):
            for m in glob.glob(pat, recursive=True):
                if os.path.isfile(m):
                    return m
    return None


def adb_reverse(port):
    """Lance `adb reverse` ; retourne (ok, message)."""
    adb = find_adb()
    if not adb:
        return False, ("adb introuvable : winget install Google.PlatformTools "
                       "(puis rouvre le terminal)")
    try:
        r = subprocess.run([adb, "reverse", f"tcp:{port}", f"tcp:{port}"], capture_output=True,
                           text=True, timeout=15, creationflags=NO_WINDOW)
    except Exception as e:
        return False, f"adb a échoué : {e}"
    out = (r.stdout + r.stderr).strip()
    if r.returncode != 0:
        return False, ("Appareil non détecté ou non autorisé (" + (out or "adb erreur") + "). "
                       "Débogage USB activé ? Accepte la fenêtre sur la tablette.")
    return True, f"Câble USB prêt : sur la tablette, ouvre http://127.0.0.1:{port}"


# --------------------------------------------------------------------------
# ffmpeg
# --------------------------------------------------------------------------
def find_ffmpeg():
    """Cherche ffmpeg : variable FFMPEG_PATH, à côté du programme, PATH, puis emplacements Windows usuels."""
    env = os.environ.get("FFMPEG_PATH")
    if env and os.path.isfile(env):
        return env
    here = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
    for name in ("ffmpeg.exe", "ffmpeg"):
        p = os.path.join(here, name)
        if os.path.isfile(p):
            return p
    found = shutil.which("ffmpeg")
    if found:
        return found
    if sys.platform == "win32":
        # winget/choco/scoop ajoutent ffmpeg au PATH, mais un terminal ouvert avant l'installation
        # ne le sait pas : on regarde directement dans leurs dossiers.
        import glob
        local = os.environ.get("LOCALAPPDATA", "")
        pf = os.environ.get("ProgramFiles", r"C:\Program Files")
        pdata = os.environ.get("ProgramData", r"C:\ProgramData")
        home = os.path.expanduser("~")
        patterns = [
            os.path.join(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"),
            os.path.join(local, "Microsoft", "WinGet", "Packages", "Gyan.FFmpeg*", "**", "bin", "ffmpeg.exe"),
            os.path.join(local, "Microsoft", "WinGet", "Packages", "*FFmpeg*", "**", "ffmpeg.exe"),
            os.path.join(pdata, "chocolatey", "bin", "ffmpeg.exe"),
            os.path.join(home, "scoop", "shims", "ffmpeg.exe"),
            os.path.join(pf, "ffmpeg", "bin", "ffmpeg.exe"),
            r"C:\ffmpeg\bin\ffmpeg.exe",
        ]
        for pat in patterns:
            for m in glob.glob(pat, recursive=True):
                if os.path.isfile(m):
                    return m
    return None


# Pour chaque encodeur : (pix_fmt, [variantes d'options à essayer])
ENCODERS = {
    "h264_nvenc": ("nv12", [
        ["-preset", "p1", "-tune", "ll", "-rc", "cbr", "-zerolatency", "1", "-bf", "0"],
        ["-preset", "llhp", "-rc", "cbr", "-bf", "0"],
        [],
    ]),
    "h264_qsv": ("nv12", [
        ["-preset", "veryfast", "-look_ahead", "0", "-bf", "0"],
        [],
    ]),
    "h264_amf": ("nv12", [
        ["-usage", "ultralowlatency", "-quality", "speed"],
        [],
    ]),
    "libx264": ("yuv420p", [
        ["-preset", "ultrafast", "-tune", "zerolatency",
         "-x264-params", "bframes=0:slices=1:repeat-headers=1"],
    ]),
}
AUTO_ORDER = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"]


def test_encoder(ffmpeg, enc, extra):
    pix = ENCODERS[enc][0]
    cmd = [ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30",
           "-frames:v", "5", "-c:v", enc, *extra, "-pix_fmt", pix, "-f", "null", "-"]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=20, creationflags=NO_WINDOW)
        return r.returncode == 0
    except Exception:
        return False


def pick_encoder(ffmpeg, wanted="auto"):
    """Retourne (nom, options) du premier encodeur qui fonctionne vraiment."""
    order = AUTO_ORDER if wanted == "auto" else [wanted]
    for enc in order:
        for extra in ENCODERS[enc][1]:
            if test_encoder(ffmpeg, enc, extra):
                return enc, extra
    return None, None


def even(n):
    return max(2, int(n) // 2 * 2)


def build_ffmpeg_cmd(ffmpeg, mon, out_w, out_h, fps, bitrate_kbps, enc, extra,
                     test_source=False, mode="h264", web_quality=5):
    if test_source:
        inp = ["-re", "-f", "lavfi", "-i", f"testsrc2=size={mon['w']}x{mon['h']}:rate={fps}"]
    else:
        inp = ["-f", "gdigrab", "-framerate", str(fps), "-draw_mouse", "1",
               "-offset_x", str(mon["x"]), "-offset_y", str(mon["y"]),
               "-video_size", f"{mon['w']}x{mon['h']}", "-i", "desktop"]
    vf = []
    if (out_w, out_h) != (mon["w"], mon["h"]):
        vf.append(f"scale={out_w}:{out_h}")
    cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-fflags", "nobuffer"] + inp
    if vf:
        cmd += ["-vf", ",".join(vf)]
    if mode == "mjpeg":
        # navigateur : une image JPEG indépendante par image (facile à jeter si le réseau rame)
        cmd += ["-c:v", "mjpeg", "-q:v", str(web_quality), "-pix_fmt", "yuvj420p",
                "-f", "image2pipe", "pipe:1"]
    else:
        cmd += ["-c:v", enc, *extra, "-pix_fmt", ENCODERS[enc][0],
                "-b:v", f"{bitrate_kbps}k", "-maxrate", f"{bitrate_kbps}k",
                "-bufsize", f"{max(bitrate_kbps // 2, 500)}k",
                "-g", str(fps * 2),
                "-bsf:v", "dump_extra=freq=keyframe",
                "-f", "h264", "pipe:1"]
    return cmd


def iter_access_units(stream):
    """
    Lit un flux H.264 Annex B et produit (is_key, bytes) : une image complète par élément
    (toutes ses tranches), avec SPS/PPS inclus devant les images clés. Sans B-frames.
    Une image commence quand une tranche a first_mb_in_slice == 0 (premier bit à 1).
    """
    buf = bytearray()
    pending = []        # SPS/PPS/SEI en attente de la prochaine image
    cur = []            # NAL de l'image en cours
    cur_key = False
    while True:
        chunk = stream.read(65536)
        if not chunk:
            break
        buf += chunk
        pos = []
        i = 0
        while True:
            j = buf.find(b"\x00\x00\x01", i)
            if j < 0:
                break
            pos.append(j)
            i = j + 3
        if len(pos) < 2:
            continue
        for k in range(len(pos) - 1):
            nal = bytes(buf[pos[k] + 3:pos[k + 1]]).rstrip(b"\x00")
            if not nal:
                continue
            t = nal[0] & 0x1F
            if t == 9:               # délimiteur d'accès : inutile
                continue
            if t in (1, 5):          # tranche d'image
                first_slice = len(nal) > 1 and (nal[1] & 0x80) != 0
                if first_slice:
                    if cur:          # l'image précédente est complète
                        yield cur_key, b"".join(START4 + n for n in cur)
                    cur = pending + [nal]
                    pending = []
                    cur_key = (t == 5)
                else:
                    cur.append(nal)
            else:                    # SPS, PPS, SEI...
                pending.append(nal)
        del buf[:pos[-1]]


def iter_jpegs(stream):
    """Lit un flux de JPEG concaténés et produit (True, bytes) pour chaque image (SOI..EOI)."""
    buf = bytearray()
    while True:
        chunk = stream.read(65536)
        if not chunk:
            break
        buf += chunk
        while True:
            s = buf.find(b"\xff\xd8")
            if s < 0:
                del buf[:-1]          # garde un éventuel 0xFF coupé en deux
                break
            e = buf.find(b"\xff\xd9", s + 2)
            if e < 0:
                if s > 0:
                    del buf[:s]
                break
            yield True, bytes(buf[s:e + 2])
            del buf[:e + 2]


# --------------------------------------------------------------------------
# Injection souris / tactile
# --------------------------------------------------------------------------
def clamp01(v):
    return max(0.0, min(1.0, float(v)))


class LogInput:
    def __init__(self, mon):
        self.mon = mon

    def handle(self, m):
        log("[tactile]", m)


class WinInput:
    LDOWN, LUP, RDOWN, RUP, WHEEL = 0x0002, 0x0004, 0x0008, 0x0010, 0x0800

    def __init__(self, mon):
        import ctypes
        self.mon = mon
        self.user32 = ctypes.windll.user32

    def _move(self, nx, ny):
        x = self.mon["x"] + int(clamp01(nx) * (self.mon["w"] - 1))
        y = self.mon["y"] + int(clamp01(ny) * (self.mon["h"] - 1))
        self.user32.SetCursorPos(x, y)

    def _btn(self, flag):
        self.user32.mouse_event(flag, 0, 0, 0, 0)

    def handle(self, m):
        a = m.get("a")
        if a in ("move", "down", "up", "click", "rclick"):
            self._move(m.get("x", 0), m.get("y", 0))
        if a == "down":
            self._btn(self.LDOWN)
        elif a == "up":
            self._btn(self.LUP)
        elif a == "click":
            self._btn(self.LDOWN)
            self._btn(self.LUP)
        elif a == "rclick":
            self._btn(self.RDOWN)
            self._btn(self.RUP)
        elif a == "scroll":
            # dy > 0 : les doigts descendent -> le contenu descend -> molette vers le haut
            delta = int(float(m.get("dy", 0)) * 1500)
            if delta:
                self.user32.mouse_event(self.WHEEL, 0, 0, delta, 0)


# --------------------------------------------------------------------------
# Réseau : app Android (binaire) et navigateur (HTTP + WebSocket)
# --------------------------------------------------------------------------
def send_msg(sock, mtype, payload):
    sock.sendall(struct.pack(">BI", mtype, len(payload)) + payload)


def recv_exact(sock, n):
    data = bytearray()
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            raise ConnectionError("connexion fermée")
        data += chunk
    return bytes(data)


class WS:
    """WebSocket côté serveur (RFC 6455), minimal : texte, binaire, ping/pong, fermeture."""

    def __init__(self, sock):
        self.sock = sock
        self.lock = threading.Lock()

    def _send(self, opcode, payload):
        n = len(payload)
        if n < 126:
            head = struct.pack(">BB", 0x80 | opcode, n)
        elif n < 65536:
            head = struct.pack(">BBH", 0x80 | opcode, 126, n)
        else:
            head = struct.pack(">BBQ", 0x80 | opcode, 127, n)
        with self.lock:
            self.sock.sendall(head + payload)

    def send_text(self, s):
        self._send(0x1, s.encode("utf-8"))

    def send_binary(self, b):
        self._send(0x2, b)

    def recv(self):
        """Retourne (opcode, payload) d'un message complet texte/binaire."""
        msg = bytearray()
        first_op = None
        while True:
            b0, b1 = recv_exact(self.sock, 2)
            fin = b0 & 0x80
            op = b0 & 0x0F
            n = b1 & 0x7F
            if n == 126:
                n = struct.unpack(">H", recv_exact(self.sock, 2))[0]
            elif n == 127:
                n = struct.unpack(">Q", recv_exact(self.sock, 8))[0]
            if n > 1_000_000:
                raise ConnectionError("message trop grand")
            mask = recv_exact(self.sock, 4) if (b1 & 0x80) else None
            data = recv_exact(self.sock, n) if n else b""
            if mask:
                data = bytes(c ^ mask[i & 3] for i, c in enumerate(data))
            if op == 0x8:
                raise ConnectionError("fermeture")
            if op == 0x9:
                self._send(0xA, data)
                continue
            if op == 0xA:
                continue
            if op in (0x1, 0x2):
                first_op = op
                msg = bytearray(data)
            elif op == 0x0:
                msg += data
            if fin:
                return first_op, bytes(msg)


class Config:
    def __init__(self, monitor_index=None, fps=30, bitrate_kbps=8000, max_width=0,
                 encoder="auto", port=PORT, test_source=False, pin="", web_quality=5):
        self.monitor_index = monitor_index
        self.fps = fps
        self.bitrate_kbps = bitrate_kbps
        self.max_width = max_width
        self.encoder = encoder
        self.port = port
        self.test_source = test_source
        self.pin = pin or ""
        self.web_quality = web_quality  # qualité JPEG ffmpeg : 2 (très bon) .. 15 (léger)


class Server:
    MAX_QUEUE = 20  # H.264 : au-delà (~0,7 s à 30 ips) on jette et on attend une image clé
    MAX_QUEUE_WEB = 2  # JPEG : on garde seulement les toutes dernières images

    def __init__(self, cfg, say=log):
        self.cfg = cfg
        self.say = say
        self.stop_event = threading.Event()
        self.listener = None
        self.busy = threading.Lock()   # un seul appareil à la fois
        self.ffmpeg = self.mon = self.inp = None
        self.enc = self.extra = None
        self.out_w = self.out_h = 0

    def stop(self):
        self.stop_event.set()
        try:
            if self.listener:
                self.listener.close()
        except Exception:
            pass

    # ---- démarrage -------------------------------------------------------------------
    def serve_forever(self):
        cfg = self.cfg
        self.ffmpeg = find_ffmpeg()
        if not self.ffmpeg:
            self.say("ERREUR : ffmpeg introuvable. S'il est déjà installé (winget), FERME et rouvre "
                     "ton terminal pour qu'il le voie ; sinon : winget install Gyan.FFmpeg. "
                     "Autre option : mets ffmpeg.exe à côté de secondscreen.py, ou définis "
                     "la variable FFMPEG_PATH.")
            return
        self.say(f"ffmpeg : {self.ffmpeg}")
        mons = list_monitors()
        idx = cfg.monitor_index if cfg.monitor_index is not None else default_monitor_index(mons)
        if not (0 <= idx < len(mons)):
            self.say(f"ERREUR : écran {idx} inexistant ({len(mons)} écran(s) détecté(s)).")
            return
        self.mon = mon = mons[idx]
        self.say("Test des encodeurs...")
        self.enc, self.extra = pick_encoder(self.ffmpeg, cfg.encoder)
        if not self.enc:
            self.say("ATTENTION : aucun encodeur H.264 ne marche -> l'app Android est indisponible, "
                     "le navigateur reste utilisable.")
        out_w, out_h = even(mon["w"]), even(mon["h"])
        if cfg.max_width and out_w > cfg.max_width:
            ratio = cfg.max_width / out_w
            out_w, out_h = even(cfg.max_width), even(out_h * ratio)
        self.out_w, self.out_h = out_w, out_h
        self.say(f"Écran {idx} : {mon['w']}x{mon['h']} -> envoi en {out_w}x{out_h}, {cfg.fps} ips, "
                 f"encodeur app : {self.enc or 'aucun'}, qualité navigateur : {cfg.web_quality}")
        if cfg.pin:
            self.say("Code d'accès activé.")

        if sys.platform == "win32" and not cfg.test_source:
            self.inp = WinInput(mon)
        else:
            self.inp = LogInput(mon)

        self.listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        try:
            self.listener.bind(("127.0.0.1", cfg.port))
        except OSError as e:
            self.say(f"ERREUR : port {cfg.port} indisponible ({e}).")
            return
        self.listener.listen(5)
        self.say(f"En attente sur 127.0.0.1:{cfg.port} (mode USB).")
        if not cfg.test_source:
            ok, text = adb_reverse(cfg.port)
            self.say(("✓ " if ok else "⚠ ") + text)

        while not self.stop_event.is_set():
            try:
                conn, addr = self.listener.accept()
            except OSError:
                break
            threading.Thread(target=self._handle_conn, args=(conn, addr), daemon=True).start()
        self.say("Serveur arrêté.")

    # ---- aiguillage ------------------------------------------------------------------
    def _handle_conn(self, conn, addr):
        try:
            conn.settimeout(10)
            first = conn.recv(1, socket.MSG_PEEK)
            if not first:
                return
            if first[0] == T_HELLO:
                self._app_conn(conn, addr)
            else:
                self._http_conn(conn, addr)
        except Exception as e:
            self.say(f"Connexion {addr[0]} : {e}")
        finally:
            try:
                conn.close()
            except Exception:
                pass

    def pin_ok(self, given):
        if not self.cfg.pin:
            return True
        return hmac.compare_digest(str(given or ""), self.cfg.pin)

    def _log_tablet(self, who, hello):
        if hello.get("w"):
            self.say(f"{who} : écran {hello.get('w')}x{hello.get('h')} "
                     f"(idéal pour la résolution de l'écran virtuel)")

    def _touch(self, payload):
        try:
            self.inp.handle(json.loads(payload.decode()))
        except Exception as e:
            self.say(f"tactile ignoré : {e}")

    # ---- app Android -----------------------------------------------------------------
    def _app_conn(self, conn, addr):
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        mtype, ln = struct.unpack(">BI", recv_exact(conn, 5))
        hello = json.loads(recv_exact(conn, ln).decode()) if mtype == T_HELLO else {}

        def refuse(text):
            send_msg(conn, T_INFO, json.dumps({"error": text}).encode())
            self.say(f"App {addr[0]} refusée : {text}")

        if not self.pin_ok(hello.get("pin")):
            return refuse("Code incorrect")
        if not self.enc:
            return refuse("Pas d'encodeur H.264 sur le PC (utilise le navigateur)")
        if not self.busy.acquire(blocking=False):
            return refuse("Déjà utilisé par un autre appareil")
        try:
            self.say(f"App Android connectée : {addr[0]}")
            self._log_tablet("Tablette", hello)
            conn.settimeout(None)
            send_msg(conn, T_INFO, json.dumps(
                {"w": self.out_w, "h": self.out_h, "fps": self.cfg.fps}).encode())

            def send_item(au):
                send_msg(conn, T_VIDEO, au)

            def recv_loop(done):
                while not done.is_set():
                    t, n = struct.unpack(">BI", recv_exact(conn, 5))
                    payload = recv_exact(conn, n)
                    if t == T_TOUCH:
                        self._touch(payload)

            self._stream("h264", conn, send_item, recv_loop)
        finally:
            self.busy.release()
            self.say("App déconnectée. En attente...")

    # ---- navigateur ------------------------------------------------------------------
    def _http_conn(self, conn, addr):
        data = b""
        while b"\r\n\r\n" not in data:
            chunk = conn.recv(4096)
            if not chunk:
                return
            data += chunk
            if len(data) > 16384:
                return
        head = data.split(b"\r\n\r\n")[0].decode("latin-1")
        lines = head.split("\r\n")
        parts = lines[0].split(" ")
        if len(parts) < 2:
            return
        target = parts[1]
        headers = {}
        for line in lines[1:]:
            k, _, v = line.partition(":")
            headers[k.strip().lower()] = v.strip()
        path, _, query = target.partition("?")
        params = urllib.parse.parse_qs(query)

        if path == "/ws" and headers.get("upgrade", "").lower() == "websocket":
            self._ws_conn(conn, addr, headers, params)
        elif path in ("/", "/index.html"):
            self.say(f"Page demandée par {addr[0]}")
            body = INDEX_HTML.encode("utf-8")
            conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                         b"Cache-Control: no-store\r\nConnection: close\r\n"
                         b"Content-Length: %d\r\n\r\n" % len(body) + body)
        else:
            conn.sendall(b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")

    def _ws_conn(self, conn, addr, headers, params):
        key = headers.get("sec-websocket-key", "")
        accept = base64.b64encode(hashlib.sha1(key.encode() + WS_GUID).digest())
        conn.sendall(b"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
                     b"Connection: Upgrade\r\nSec-WebSocket-Accept: " + accept + b"\r\n\r\n")
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        ws = WS(conn)

        def refuse(text, need_code=False):
            ws.send_text(json.dumps({"error": text, "needCode": need_code}))
            if text != "Code requis":
                self.say(f"Navigateur {addr[0]} refusé : {text}")

        code = (params.get("code") or [""])[0]
        if not self.pin_ok(code):
            return refuse("Code requis" if not code else "Code incorrect", need_code=True)
        if not self.busy.acquire(blocking=False):
            return refuse("Déjà utilisé par un autre appareil")
        try:
            self.say(f"Navigateur connecté : {addr[0]}")
            conn.settimeout(None)
            want = (params.get("fmt") or ["jpeg"])[0]
            fmt = "h264" if (want == "h264" and self.enc) else "jpeg"
            ws.send_text(json.dumps({"w": self.out_w, "h": self.out_h, "fps": self.cfg.fps,
                                     "fmt": fmt}))
            self.say(f"Format navigateur : {'H.264 (WebCodecs)' if fmt == 'h264' else 'JPEG'}")

            def send_item(item):
                if fmt == "h264":
                    ws.send_binary((b"\x01" if item[0] else b"\x00") + item[1])
                else:
                    ws.send_binary(item)

            def recv_loop(done):
                while not done.is_set():
                    op, payload = ws.recv()
                    if op != 0x1:
                        continue
                    try:
                        m = json.loads(payload.decode())
                    except Exception:
                        continue
                    if m.get("a") == "hello":
                        self._log_tablet("Navigateur", m)
                    else:
                        self.inp.handle(m)

            if fmt == "h264":
                self._stream("h264", conn, send_item, recv_loop, tagged=True, max_q=6)
            else:
                self._stream("mjpeg", conn, send_item, recv_loop)
        finally:
            self.busy.release()
            self.say("Navigateur déconnecté. En attente...")

    # ---- pipeline commun : ffmpeg -> file -> réseau ----------------------------------
    def _stream(self, mode, conn, send_item, recv_loop, tagged=False, max_q=None):
        cfg = self.cfg
        cmd = build_ffmpeg_cmd(self.ffmpeg, self.mon, self.out_w, self.out_h, cfg.fps,
                               cfg.bitrate_kbps, self.enc, self.extra, cfg.test_source,
                               mode, cfg.web_quality)
        splitter = iter_access_units if mode == "h264" else iter_jpegs
        if max_q is None:
            max_q = self.MAX_QUEUE if mode == "h264" else self.MAX_QUEUE_WEB
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                bufsize=0, creationflags=NO_WINDOW)
        done = threading.Event()
        q = queue.Queue()

        def drain_stderr():
            for line in iter(proc.stderr.readline, b""):
                self.say("ffmpeg:", line.decode(errors="replace").rstrip())

        def read_video():
            skipping = False
            try:
                for is_key, item in splitter(proc.stdout):
                    if done.is_set():
                        break
                    if skipping and not is_key:
                        continue
                    if q.qsize() > max_q:
                        with q.mutex:
                            q.queue.clear()
                        skipping = not is_key
                    else:
                        skipping = False
                    q.put((is_key, item) if tagged else item)
            finally:
                q.put(None)

        def send_video():
            try:
                while True:
                    item = q.get()
                    if item is None:
                        break
                    send_item(item)
            except Exception:
                pass
            finally:
                done.set()

        def read_input():
            try:
                recv_loop(done)
            except Exception:
                pass
            finally:
                done.set()
                q.put(None)

        for f in (drain_stderr, read_video, send_video, read_input):
            threading.Thread(target=f, daemon=True).start()
        try:
            while not done.is_set() and not self.stop_event.is_set():
                if proc.poll() is not None:
                    self.say("ffmpeg s'est arrêté.")
                    break
                done.wait(0.3)
        finally:
            done.set()
            try:
                proc.kill()          # pas besoin d'un arrêt propre : on libère la session vite
                proc.wait(timeout=3)
            except Exception:
                pass
            try:
                conn.shutdown(socket.SHUT_RDWR)
            except Exception:
                pass
            q.put(None)


# --------------------------------------------------------------------------
# Interface graphique
# --------------------------------------------------------------------------
def run_gui():
    import tkinter as tk
    from tkinter import ttk

    root = tk.Tk()
    root.title("Second écran")
    root.geometry("820x640")
    msgs = queue.Queue()
    state = {"server": None}

    mons = list_monitors()
    labels = [f"Écran {i} : {m['w']}x{m['h']}" + (" (principal)" if m["primary"] else "")
              for i, m in enumerate(mons)]
    root.columnconfigure(0, weight=1)
    root.rowconfigure(1, weight=1)
    top = ttk.Frame(root, padding=10)
    top.grid(row=0, column=0, sticky="ew")
    top.columnconfigure(0, weight=1)

    # ---- réglages (gauche) ---------------------------------------------------------------
    frm = ttk.Frame(top)
    frm.grid(row=0, column=0, sticky="nw")
    frm.columnconfigure(1, weight=1)

    def row(r, text, widget):
        ttk.Label(frm, text=text).grid(row=r, column=0, sticky="w", pady=3, padx=(0, 10))
        widget.grid(row=r, column=1, sticky="ew", pady=3)

    v_mon = tk.StringVar(value=labels[default_monitor_index(mons)])
    row(0, "Écran à envoyer", ttk.Combobox(frm, textvariable=v_mon, values=labels, state="readonly", width=26))
    v_fps = tk.StringVar(value="60")
    row(1, "Images / seconde", ttk.Combobox(frm, textvariable=v_fps, values=["24", "30", "60"], state="readonly"))
    v_br = tk.StringVar(value="8")
    row(2, "Débit app (Mb/s)", ttk.Combobox(frm, textvariable=v_br, values=["3", "5", "8", "12", "20"]))
    q_labels = {"Haute (3)": 3, "Normale (5)": 5, "Légère (8)": 8}
    v_q = tk.StringVar(value="Normale (5)")
    row(3, "Qualité navigateur", ttk.Combobox(frm, textvariable=v_q, values=list(q_labels), state="readonly"))
    v_w = tk.StringVar(value="Natif")
    row(4, "Largeur max", ttk.Combobox(frm, textvariable=v_w, values=["Natif", "2560", "1920", "1280"], state="readonly"))
    v_enc = tk.StringVar(value="auto")
    row(5, "Encodeur app", ttk.Combobox(frm, textvariable=v_enc, values=["auto"] + AUTO_ORDER, state="readonly"))
    v_port = tk.StringVar(value=str(PORT))
    row(6, "Port", ttk.Entry(frm, textvariable=v_port))
    v_pin = tk.StringVar(value="")   # inutile en USB (le serveur n'écoute qu'en local)

    # ---- connexion (droite) : câble USB ------------------------------------------------------
    side = ttk.LabelFrame(top, text="Connexion par câble USB", padding=10)
    side.grid(row=0, column=1, sticky="n", padx=(20, 0))
    ttk.Label(side, justify="left", text=(
        "1. Débogage USB activé sur la tablette\n"
        "2. Branche le câble, accepte la fenêtre\n"
        "3. Clique ▶ Démarrer (adb se branche seul)\n"
        "4. Tablette, Chrome : l'adresse ci-dessous")).grid(row=0, column=0, sticky="w")
    v_url = tk.StringVar(value=f"http://127.0.0.1:{PORT}")
    ttk.Entry(side, textvariable=v_url, state="readonly", width=30).grid(row=1, column=0, pady=6, sticky="ew")

    def usb_again():
        try:
            port = int(v_port.get())
        except ValueError:
            return
        def work():
            ok, text = adb_reverse(port)
            say(("✓ " if ok else "⚠ ") + text)
        threading.Thread(target=work, daemon=True).start()

    ttk.Button(side, text="🔌 Rebrancher le câble", command=usb_again).grid(row=2, column=0, sticky="ew")

    def upd_url(*_):
        v_url.set(f"http://127.0.0.1:{v_port.get() or PORT}")
    v_port.trace_add("write", upd_url)

    # ---- bas : bouton + journal --------------------------------------------------------------
    bottom = ttk.Frame(root, padding=(10, 0, 10, 10))
    bottom.grid(row=1, column=0, sticky="nsew")
    bottom.columnconfigure(0, weight=1)
    bottom.rowconfigure(1, weight=1)
    btn = ttk.Button(bottom, text="▶ Démarrer")
    btn.grid(row=0, column=0, sticky="ew", pady=6)
    txt = tk.Text(bottom, height=10, state="disabled", wrap="word")
    txt.grid(row=1, column=0, sticky="nsew")

    def say(*a):
        msgs.put(" ".join(str(x) for x in a))

    def pump():
        try:
            while True:
                line = msgs.get_nowait()
                txt.configure(state="normal")
                txt.insert("end", line + "\n")
                txt.see("end")
                txt.configure(state="disabled")
        except queue.Empty:
            pass
        root.after(150, pump)

    def toggle():
        if state["server"]:
            state["server"].stop()
            state["server"] = None
            btn.configure(text="▶ Démarrer")
            return
        try:
            cfg = Config(
                monitor_index=labels.index(v_mon.get()),
                fps=int(v_fps.get()),
                bitrate_kbps=int(float(v_br.get()) * 1000),
                max_width=0 if v_w.get() == "Natif" else int(v_w.get()),
                encoder=v_enc.get(),
                port=int(v_port.get()),
                pin=v_pin.get().strip(),
                web_quality=q_labels[v_q.get()],
            )
        except (ValueError, KeyError):
            say("Paramètres invalides.")
            return
        srv = Server(cfg, say=say)
        state["server"] = srv
        threading.Thread(target=srv.serve_forever, daemon=True).start()
        btn.configure(text="■ Arrêter")

    btn.configure(command=toggle)

    def on_close():
        if state["server"]:
            state["server"].stop()
        root.destroy()

    root.protocol("WM_DELETE_WINDOW", on_close)
    pump()
    root.mainloop()


def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass
    enable_dpi_awareness()
    ap = argparse.ArgumentParser(description="Second écran : serveur PC (app Android + navigateur)")
    ap.add_argument("--cli", action="store_true", help="sans interface graphique")
    ap.add_argument("--list-monitors", action="store_true")
    ap.add_argument("--monitor", type=int, default=None)
    ap.add_argument("--fps", type=int, default=60)
    ap.add_argument("--bitrate", type=int, default=8000, help="app, en kb/s")
    ap.add_argument("--web-quality", type=int, default=5, help="navigateur : 2 (très bon) à 15 (léger)")
    ap.add_argument("--max-width", type=int, default=0)
    ap.add_argument("--encoder", default="auto", choices=["auto"] + AUTO_ORDER)
    ap.add_argument("--port", type=int, default=PORT)
    ap.add_argument("--pin", default="", help="code d'accès (inutile en USB)")
    ap.add_argument("--test-source", action="store_true",
                    help="mire de test au lieu de la capture d'écran (pour tester)")
    args = ap.parse_args()

    if args.list_monitors:
        for i, m in enumerate(list_monitors()):
            print(i, m)
        return
    if args.cli or args.test_source:
        cfg = Config(args.monitor, args.fps, args.bitrate, args.max_width, args.encoder,
                     args.port, args.test_source, args.pin, args.web_quality)
        srv = Server(cfg)
        try:
            srv.serve_forever()
        except KeyboardInterrupt:
            srv.stop()
        return
    run_gui()


if __name__ == "__main__":
    main()
