namespace SecondEcran;

public static class Splitters
{
    static readonly byte[] Start4 = { 0, 0, 0, 1 };

    /// <summary>
    /// Flux H.264 Annex B -> une image complète par élément (toutes ses tranches),
    /// SPS/PPS inclus devant les images clés. Une image commence quand une tranche a
    /// first_mb_in_slice == 0 (premier bit à 1).
    /// </summary>
    public static IEnumerable<Frame> AccessUnits(Stream stream)
    {
        byte[] buf = new byte[1 << 20];
        int len = 0;
        var chunk = new byte[65536];
        var pending = new List<byte[]>();
        var cur = new List<byte[]>();
        bool curKey = false;
        var pos = new List<int>();

        while (true)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            if (len + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + n));
            Buffer.BlockCopy(chunk, 0, buf, len, n);
            len += n;

            pos.Clear();
            for (int i = 0; i + 2 < len; i++)
            {
                if (buf[i + 2] > 1) { i += 2; continue; }
                if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 1) { pos.Add(i); i += 2; }
            }
            if (pos.Count < 2) continue;

            for (int k = 0; k < pos.Count - 1; k++)
            {
                int s = pos[k] + 3, e = pos[k + 1];
                while (e > s && buf[e - 1] == 0) e--;
                if (e <= s) continue;
                var nal = new byte[e - s];
                Buffer.BlockCopy(buf, s, nal, 0, nal.Length);
                int t = nal[0] & 0x1F;
                if (t == 9) continue;                       // délimiteur d'accès
                if (t == 1 || t == 5)
                {
                    bool first = nal.Length > 1 && (nal[1] & 0x80) != 0;
                    if (first)
                    {
                        if (cur.Count > 0) yield return new Frame(curKey, Join(cur));
                        cur = new List<byte[]>(pending) { nal };
                        pending.Clear();
                        curKey = t == 5;
                    }
                    else cur.Add(nal);
                }
                else pending.Add(nal);                      // SPS, PPS, SEI...
            }
            int keep = pos[^1];
            Buffer.BlockCopy(buf, keep, buf, 0, len - keep);
            len -= keep;
        }
    }

    static byte[] Join(List<byte[]> nals)
    {
        int total = 0;
        foreach (var n in nals) total += 4 + n.Length;
        var o = new byte[total];
        int p = 0;
        foreach (var n in nals)
        {
            Buffer.BlockCopy(Start4, 0, o, p, 4);
            p += 4;
            Buffer.BlockCopy(n, 0, o, p, n.Length);
            p += n.Length;
        }
        return o;
    }

    /// <summary>JPEG concaténés -> un élément par image (SOI..EOI).</summary>
    public static IEnumerable<Frame> Jpegs(Stream stream)
    {
        byte[] buf = new byte[1 << 20];
        int len = 0;
        var chunk = new byte[65536];
        while (true)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0) break;
            if (len + n > buf.Length) Array.Resize(ref buf, Math.Max(buf.Length * 2, len + n));
            Buffer.BlockCopy(chunk, 0, buf, len, n);
            len += n;
            while (true)
            {
                int s = IndexOf(buf, len, 0xFF, 0xD8, 0);
                if (s < 0)
                {
                    if (len > 1) { buf[0] = buf[len - 1]; len = 1; }
                    break;
                }
                int e = IndexOf(buf, len, 0xFF, 0xD9, s + 2);
                if (e < 0)
                {
                    if (s > 0) { Buffer.BlockCopy(buf, s, buf, 0, len - s); len -= s; }
                    break;
                }
                var jpg = new byte[e + 2 - s];
                Buffer.BlockCopy(buf, s, jpg, 0, jpg.Length);
                yield return new Frame(true, jpg);
                Buffer.BlockCopy(buf, e + 2, buf, 0, len - (e + 2));
                len -= e + 2;
            }
        }
    }

    static int IndexOf(byte[] b, int len, byte a1, byte a2, int from)
    {
        for (int i = from; i + 1 < len; i++)
            if (b[i] == a1 && b[i + 1] == a2) return i;
        return -1;
    }
}
