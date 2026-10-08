#!/usr/bin/env python3
"""Faux navigateur : teste la page, le WebSocket, le flux JPEG, les gestes, le code d'accès."""
import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 5555
seconds = float(sys.argv[3]) if len(sys.argv) > 3 else 4
code = sys.argv[4] if len(sys.argv) > 4 else ""
outdir = sys.argv[5] if len(sys.argv) > 5 else "."
GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"


def recv_exact(s, n):
    b = bytearray()
    while len(b) < n:
        c = s.recv(n - len(b))
        if not c:
            raise ConnectionError("fermé")
        b += c
    return bytes(b)


def http_get(path):
    s = socket.create_connection((host, port), timeout=10)
    s.sendall(f"GET {path} HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n".encode())
    data = b""
    while True:
        c = s.recv(65536)
        if not c:
            break
        data += c
    s.close()
    return data


def ws_connect(query=""):
    s = socket.create_connection((host, port), timeout=10)
    key = base64.b64encode(os.urandom(16)).decode()
    s.sendall((f"GET /ws{query} HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\n"
               f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n").encode())
    resp = b""
    while b"\r\n\r\n" not in resp:
        resp += s.recv(1)
    expected = base64.b64encode(hashlib.sha1((key + GUID).encode()).digest()).decode()
    assert b"101" in resp.split(b"\r\n")[0], resp
    assert expected.encode() in resp, "Sec-WebSocket-Accept incorrect"
    return s


def ws_send_text(s, obj):
    p = json.dumps(obj).encode()
    mask = os.urandom(4)
    n = len(p)
    head = bytes([0x81, 0x80 | n]) if n < 126 else bytes([0x81, 0x80 | 126]) + struct.pack(">H", n)
    s.sendall(head + mask + bytes(c ^ mask[i & 3] for i, c in enumerate(p)))


def ws_recv(s):
    b0, b1 = recv_exact(s, 2)
    n = b1 & 0x7F
    if n == 126:
        n = struct.unpack(">H", recv_exact(s, 2))[0]
    elif n == 127:
        n = struct.unpack(">Q", recv_exact(s, 8))[0]
    return b0 & 0x0F, recv_exact(s, n)


# 1. La page
page = http_get("/")
assert page.startswith(b"HTTP/1.1 200"), page[:80]
assert b"<canvas" in page and b"WebSocket" in page
print("OK page servie :", len(page), "octets")
assert http_get("/nimportequoi").startswith(b"HTTP/1.1 404")
print("OK 404 sur chemin inconnu")

# 2. Code d'accès
if code:
    s = ws_connect("?code=")
    op, p = ws_recv(s)
    m = json.loads(p)
    assert m.get("error") and m.get("needCode"), m
    print("OK sans code :", m)
    s.close()
    s = ws_connect("?code=faux")
    op, p = ws_recv(s)
    assert "incorrect" in json.loads(p)["error"].lower(), p
    print("OK mauvais code refusé")
    s.close()
    time.sleep(0.3)

# 3. Flux
s = ws_connect(f"?code={code}")
s.settimeout(10)
ws_send_text(s, {"a": "hello", "w": 2000, "h": 1200})
op, p = ws_recv(s)
info = json.loads(p)
print("INFO", info)
assert "w" in info, info

# 4. Un 2e navigateur doit être refusé (déjà utilisé)
s2 = ws_connect(f"?code={code}")
op2, p2 = ws_recv(s2)
assert "déjà" in json.loads(p2)["error"].lower(), p2
print("OK 2e appareil refusé :", json.loads(p2)["error"])
s2.close()

frames = bad = 0
start = time.time()
sent = False
saved = 0
while time.time() - start < seconds:
    op, p = ws_recv(s)
    if op == 0x2:
        frames += 1
        if not (p[:2] == b"\xff\xd8" and p[-2:] == b"\xff\xd9"):
            bad += 1
        if saved < 2 and frames in (3, 20):
            open(os.path.join(outdir, f"web_frame_{frames}.jpg"), "wb").write(p)
            saved += 1
    if not sent and frames > 5:
        for m in ({"a": "click", "x": 0.5, "y": 0.5}, {"a": "down", "x": 0.1, "y": 0.1},
                  {"a": "move", "x": 0.2, "y": 0.2}, {"a": "up", "x": 0.2, "y": 0.2},
                  {"a": "rclick", "x": 0.9, "y": 0.9}, {"a": "scroll", "dy": -0.05}):
            ws_send_text(s, m)
        sent = True
dur = time.time() - start
print(f"{frames} images JPEG en {dur:.1f}s ({frames / dur:.0f} ips), images invalides : {bad}")
assert frames > 10 and bad == 0
s.close()
print("OK")
