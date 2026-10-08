#!/usr/bin/env python3
"""Faux client tablette : se connecte, enregistre la vidéo reçue, envoie quelques gestes."""
import json
import socket
import struct
import sys
import time

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 5555
out = sys.argv[3] if len(sys.argv) > 3 else "received.h264"
seconds = float(sys.argv[4]) if len(sys.argv) > 4 else 4
pin = sys.argv[5] if len(sys.argv) > 5 else ""


def send(s, t, obj):
    p = json.dumps(obj).encode()
    s.sendall(struct.pack(">BI", t, len(p)) + p)


def recv_exact(s, n):
    b = bytearray()
    while len(b) < n:
        c = s.recv(n - len(b))
        if not c:
            raise ConnectionError("fermé")
        b += c
    return bytes(b)


s = socket.create_connection((host, port), timeout=10)
s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
send(s, 0x10, {"v": 1, "w": 2000, "h": 1200, "pin": pin})
frames = keys = 0
first_is_key = None
start = time.time()
sent_touch = False
with open(out, "wb") as f:
    while time.time() - start < seconds:
        t, ln = struct.unpack(">BI", recv_exact(s, 5))
        p = recv_exact(s, ln)
        if t == 0x01:
            info = json.loads(p)
            print("INFO", info)
            if "error" in info:
                sys.exit(f"Refusé par le serveur : {info['error']}")
        elif t == 0x02:
            assert p.startswith(b"\x00\x00\x00\x01"), "pas de start code"
            nal_types = []
            i = 0
            while True:
                j = p.find(b"\x00\x00\x00\x01", i)
                if j < 0:
                    break
                nal_types.append(p[j + 4] & 0x1F)
                i = j + 4
            is_key = 5 in nal_types
            if first_is_key is None:
                first_is_key = is_key
                print("1er paquet : NAL", nal_types)
            frames += 1
            keys += is_key
            f.write(p)
        if not sent_touch and frames > 5:
            for m in ({"a": "click", "x": 0.5, "y": 0.5},
                      {"a": "down", "x": 0.1, "y": 0.1}, {"a": "move", "x": 0.2, "y": 0.2},
                      {"a": "up", "x": 0.2, "y": 0.2}, {"a": "rclick", "x": 0.9, "y": 0.9},
                      {"a": "scroll", "dy": 0.05}):
                send(s, 0x11, m)
            sent_touch = True
dur = time.time() - start
print(f"{frames} images en {dur:.1f}s ({frames / dur:.0f} ips), {keys} images clés, "
      f"1er paquet clé : {first_is_key}")
s.close()
