using System.Buffers.Binary;
using System.Text;

namespace SecondEcran;

internal static class Net
{
    public static void ReadExact(Stream s, byte[] buf, int offset, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r = s.Read(buf, offset + got, n - got);
            if (r <= 0) throw new IOException("connexion fermée");
            got += r;
        }
    }

    public static byte[] ReadExact(Stream s, int n)
    {
        var b = new byte[n];
        ReadExact(s, b, 0, n);
        return b;
    }
}

/// <summary>WebSocket côté serveur (RFC 6455), minimal : texte, binaire, ping/pong, fermeture.</summary>
internal sealed class WebSocketLite
{
    readonly Stream _s;
    readonly object _lock = new();

    public WebSocketLite(Stream s) { _s = s; }

    void Send(int opcode, byte[] payload)
    {
        int n = payload.Length;
        byte[] head;
        if (n < 126) head = new byte[] { (byte)(0x80 | opcode), (byte)n };
        else if (n < 65536)
        {
            head = new byte[4];
            head[0] = (byte)(0x80 | opcode);
            head[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(head.AsSpan(2), (ushort)n);
        }
        else
        {
            head = new byte[10];
            head[0] = (byte)(0x80 | opcode);
            head[1] = 127;
            BinaryPrimitives.WriteUInt64BigEndian(head.AsSpan(2), (ulong)n);
        }
        var all = new byte[head.Length + n];
        Buffer.BlockCopy(head, 0, all, 0, head.Length);
        Buffer.BlockCopy(payload, 0, all, head.Length, n);
        lock (_lock) { _s.Write(all, 0, all.Length); _s.Flush(); }
    }

    public void SendText(string text) => Send(0x1, Encoding.UTF8.GetBytes(text));
    public void SendBinary(byte[] data) => Send(0x2, data);

    /// <summary>Message complet texte/binaire : (opcode, charge utile).</summary>
    public (int op, byte[] data) Receive()
    {
        var msg = new List<byte>();
        int firstOp = 0;
        while (true)
        {
            var h = Net.ReadExact(_s, 2);
            bool fin = (h[0] & 0x80) != 0;
            int op = h[0] & 0x0F;
            long n = h[1] & 0x7F;
            if (n == 126) n = BinaryPrimitives.ReadUInt16BigEndian(Net.ReadExact(_s, 2));
            else if (n == 127) n = (long)BinaryPrimitives.ReadUInt64BigEndian(Net.ReadExact(_s, 8));
            if (n > 1_000_000) throw new IOException("message trop grand");
            byte[]? mask = (h[1] & 0x80) != 0 ? Net.ReadExact(_s, 4) : null;
            var data = n > 0 ? Net.ReadExact(_s, (int)n) : Array.Empty<byte>();
            if (mask != null)
                for (int i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
            if (op == 0x8) throw new IOException("fermeture");
            if (op == 0x9) { Send(0xA, data); continue; }
            if (op == 0xA) continue;
            if (op == 0x1 || op == 0x2) { firstOp = op; msg.Clear(); msg.AddRange(data); }
            else if (op == 0x0) msg.AddRange(data);
            if (fin) return (firstOp, msg.ToArray());
        }
    }
}

/// <summary>File d'images : on jette les anciennes si le réseau est en retard.</summary>
internal sealed class FrameQueue
{
    readonly Queue<Frame> _q = new();
    bool _done;

    public int Count { get { lock (_q) return _q.Count; } }

    public void Clear() { lock (_q) _q.Clear(); }

    public void Put(Frame f)
    {
        lock (_q) { _q.Enqueue(f); Monitor.Pulse(_q); }
    }

    public void Complete()
    {
        lock (_q) { _done = true; _q.Clear(); Monitor.PulseAll(_q); }
    }

    public bool Take(out Frame f)
    {
        lock (_q)
        {
            while (_q.Count == 0 && !_done) Monitor.Wait(_q);
            if (_done) { f = default; return false; }
            f = _q.Dequeue();
            return true;
        }
    }
}
