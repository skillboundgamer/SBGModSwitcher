using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ModSwitcher2;

/// <summary>
/// Reads one file out of a game wad (exec\wad\pc_le\*.wad) without unpacking the whole wad into memory: the LZ4 frame is
/// decoded block by block (64 KB history kept for linked blocks) into a small sliding window that is searched as it goes.
/// r_heroa00.wad is 98 MB packed / 172 MB unpacked; this holds a few MB at a time. Read-only on the wad.
/// The layouts (research/switcher2/facts.md, 2026-10-25):
///  - a visual script is a 'zZ' blob: 16-byte prefix ending ab ab ab ab, magic 7a da 7a 5a, 00 00 02 00; its game path
///    (gameart\...) is inside the first 0x4000 bytes; its size = the size of the TOC entry named 0x%016X of SmsHash(path);
///  - a Lua script is a record: 0x100 path field (z:/int9/gameart/...), 0x100 name field, 4 zero bytes, then the Lua
///    chunk (\x1bLuaR); its size = the TOC entry (named like the file, no extension) minus 0x204.
/// </summary>
public static class WadFile
{
    static readonly byte[] Zz = { 0x7a, 0xda, 0x7a, 0x5a };
    static readonly byte[] ZzTail = { 0x00, 0x00, 0x02, 0x00 };
    static readonly byte[] PrefixEnd = { 0xab, 0xab, 0xab, 0xab };
    static readonly byte[] LuaR = { 0x1b, (byte)'L', (byte)'u', (byte)'a', (byte)'R' };
    const int PathSpan = 0x4000;       // the path of a visual script sits in its first 16 KB (tools/gowx_core.py blobs)
    const int LuaHead = 0x204;         // 0x100 path + 0x100 name + 4 zero bytes
    const int Look = PathSpan + 0x400; // bytes a hit may need after it before it is judged
    const int Chunk = 1 << 20;         // decoded bytes searched per step

    /// <summary>64-bit Jenkins one-at-a-time over the UPPERCASE string, no final mix (the game's StringHash).</summary>
    public static ulong SmsHash(string s)
    {
        ulong h = 0;
        foreach (byte c in Encoding.Latin1.GetBytes(s.ToUpperInvariant())) { h += c; h += h << 10; h ^= h >> 6; }
        return h;
    }

    /// <summary>The visual script with game path <paramref name="gamePath"/> (gameart\visualscripts\...), or null + why.</summary>
    public static byte[]? VisualScript(string wadPath, string gamePath, out string why)
    {
        var path = Encoding.Latin1.GetBytes(Norm(gamePath));
        return Find(wadPath, $"0x{SmsHash(gamePath.Replace('/', '\\')):X16}", Zz, out why, (w, h, size) =>
        {
            if (h < 16 || !w.Has(h - 16, 24)) return -1;
            if (!w.Span(h + 4, 4).SequenceEqual(ZzTail) || !w.Span(h - 4, 4).SequenceEqual(PrefixEnd)) return -1;
            long end = Math.Min(h + PathSpan, w.End);
            var region = w.Span(h + 8, (int)(end - h - 8));
            // stop at the next blob, as tools/gowx_core.py does
            for (int at = 0; ;)
            {
                int k = region[at..].IndexOf(Zz);
                if (k < 0) break;
                k += at;
                if (k + 8 <= region.Length && region.Slice(k + 4, 4).SequenceEqual(ZzTail)) { region = region[..k]; break; }
                at = k + 1;
            }
            var low = new byte[region.Length];
            for (int i = 0; i < low.Length; i++) { byte b = region[i]; low[i] = b == '/' ? (byte)'\\' : b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b; }
            return low.AsSpan().IndexOf(path) >= 0 ? h - 16 : -1;
        }, size => size);
    }

    /// <summary>The Lua chunk of the record whose path field is <paramref name="recordPath"/> (z:/int9/gameart/...), or null + why.</summary>
    public static byte[]? LuaScript(string wadPath, string recordPath, string tocName, out string why)
    {
        var pat = Encoding.Latin1.GetBytes(recordPath + "\0");
        var name = Encoding.Latin1.GetBytes(tocName + "\0");
        return Find(wadPath, tocName, pat, out why, (w, h, size) =>
        {
            if (size <= LuaHead || !w.Has(h, LuaHead + LuaR.Length)) return -1;
            if (!w.Span(h + 0x100, name.Length).SequenceEqual(name)) return -1;
            if (w.Span(h + 0x200, 4).IndexOfAnyExcept((byte)0) >= 0) return -1;
            return w.Span(h + LuaHead, LuaR.Length).SequenceEqual(LuaR) ? h + LuaHead : -1;
        }, size => size - LuaHead);
    }

    static string Norm(string p) => p.Replace('/', '\\').ToLowerInvariant();

    /// <summary>
    /// TOC entry <paramref name="tocName"/> gives the size; then every hit of <paramref name="pat"/> is judged by
    /// <paramref name="match"/> (window, hit offset, TOC size) -> start offset of the file, or -1. The first match wins.
    /// </summary>
    static byte[]? Find(string wadPath, string tocName, byte[] pat, out string why,
                        Func<Window, long, int, long> match, Func<int, int> length)
    {
        using var r = new Lz4FrameReader(wadPath);
        var w = new Window(r);
        if (!w.Ensure(0x40) || !w.Span(0, 4).SequenceEqual("WTOC"u8)) { why = "no table of contents"; return null; }
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(w.Span(8, 4));
        if (n > 4_000_000 || !w.Ensure(0x40 + n * 0x90L)) { why = "table of contents cut short"; return null; }
        int size = -1, found = 0;
        var want = Encoding.Latin1.GetBytes(tocName);
        for (long i = 0; i < n; i++)
        {
            long o = 0x40 + i * 0x90;
            var nm = w.Span(o + 0x18, 0x50);
            int z = nm.IndexOf((byte)0);
            if ((z < 0 ? nm : nm[..z]).SequenceEqual(want)) { found++; size = (int)BinaryPrimitives.ReadUInt32LittleEndian(w.Span(o + 4, 4)); }
        }
        if (found != 1) { why = found == 0 ? $"no entry {tocName}" : $"{found} entries {tocName}"; return null; }
        int len = length(size);

        long scan = 0;
        while (true)
        {
            w.Keep(Math.Max(0, scan - 16));
            w.Ensure(scan + Chunk + Look);
            long limit = w.Eof ? w.End : w.End - Look;
            if (limit > scan)
            {
                long spanEnd = Math.Min(w.End, limit + pat.Length - 1);
                var hay = w.Span(scan, (int)(spanEnd - scan));   // the window does not move until a match returns
                for (int at = 0; ;)
                {
                    int k = hay[at..].IndexOf(pat);
                    if (k < 0) break;
                    long h = scan + at + k;
                    long start = match(w, h, size);
                    if (start >= 0)
                    {
                        if (!w.Ensure(start + len)) { why = "file cut short in the wad"; return null; }
                        why = "";
                        return w.Span(start, len).ToArray();
                    }
                    at += k + 1;
                }
                scan = limit;
            }
            if (w.Eof && scan >= w.End) break;
        }
        why = "not found in the wad";
        return null;
    }

    /// <summary>Decoded bytes [Start, End) of the wad, read on demand; older bytes are dropped by Keep.</summary>
    sealed class Window
    {
        readonly Lz4FrameReader _r;
        byte[] _b = new byte[4 << 20];
        long _start;
        int _len;
        public bool Eof { get; private set; }
        public long End => _start + _len;
        public Window(Lz4FrameReader r) { _r = r; }

        public bool Has(long abs, int n) => abs >= _start && abs + n <= End;
        public ReadOnlySpan<byte> Span(long abs, int n) => _b.AsSpan(checked((int)(abs - _start)), n);

        /// <summary>Reads until the window reaches <paramref name="absEnd"/> (or the wad ends).</summary>
        public bool Ensure(long absEnd)
        {
            if (absEnd - _start > _b.Length) Array.Resize(ref _b, checked((int)Math.Max(absEnd - _start, (long)_b.Length * 2)));
            while (End < absEnd && !Eof)
            {
                int got = _r.Read(_b, _len, _b.Length - _len);
                if (got == 0) Eof = true; else _len += got;
            }
            return End >= absEnd;
        }

        /// <summary>Drops the bytes before <paramref name="absFrom"/>.</summary>
        public void Keep(long absFrom)
        {
            int drop = (int)Math.Min(Math.Max(0, absFrom - _start), _len);
            if (drop == 0) return;
            _b.AsSpan(drop, _len - drop).CopyTo(_b);
            _start += drop; _len -= drop;
        }
    }
}

/// <summary>
/// An LZ4 frame (or a plain file) read as a stream of decoded bytes. The game's wads: frame v1, 64 KB independent blocks,
/// content size + content checksum, no block checksum (research/switcher2/facts.md); linked blocks and the other block
/// sizes are read too. The content checksum is not checked: the caller checks the sha256 of what it takes out.
/// </summary>
public sealed class Lz4FrameReader : IDisposable
{
    const uint Magic = 0x184D2204;
    const int Hist = 65536;
    readonly Stream _s;
    readonly bool _raw;
    bool _indep = true, _blockSum, _end;
    int _blockMax, _histLen, _prevLen;
    byte[] _in = Array.Empty<byte>();
    byte[] _out;
    int _pos, _lim;

    public Lz4FrameReader(string path)
    {
        _s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        try
        {
            var h = new byte[4];
            int got = ReadFull(h, 4);
            if (got == 4 && BinaryPrimitives.ReadUInt32LittleEndian(h) == Magic)
            {
                var fb = new byte[2];
                if (ReadFull(fb, 2) != 2) throw new InvalidDataException("LZ4 header cut short");
                byte flg = fb[0], bd = fb[1];
                if (flg >> 6 != 1) throw new InvalidDataException("LZ4 frame version " + (flg >> 6));
                if ((flg & 0x01) != 0) throw new InvalidDataException("LZ4 frame with a dictionary");
                _indep = (flg & 0x20) != 0;
                _blockSum = (flg & 0x10) != 0;
                _blockMax = ((bd >> 4) & 7) switch { 4 => 1 << 16, 5 => 1 << 18, 6 => 1 << 20, 7 => 1 << 22, _ => throw new InvalidDataException("LZ4 block size") };
                Skip(((flg & 0x08) != 0 ? 8 : 0) + 1);   // content size, header checksum
                _in = new byte[_blockMax];
                _out = new byte[Hist + _blockMax];
                _pos = _lim = Hist;
            }
            else
            {
                _raw = true;
                _out = new byte[1 << 16];
                h.AsSpan(0, got).CopyTo(_out);
                _lim = got;
            }
        }
        catch { _s.Dispose(); throw; }
    }

    public void Dispose() => _s.Dispose();

    /// <summary>Up to <paramref name="count"/> decoded bytes; 0 = the end.</summary>
    public int Read(byte[] buf, int off, int count)
    {
        while (_pos == _lim)
        {
            if (_end) return 0;
            if (_raw)
            {
                _pos = 0; _lim = _s.Read(_out, 0, _out.Length);
                if (_lim == 0) { _end = true; return 0; }
            }
            else NextBlock();
        }
        int k = Math.Min(count, _lim - _pos);
        _out.AsSpan(_pos, k).CopyTo(buf.AsSpan(off));
        _pos += k;
        return k;
    }

    void NextBlock()
    {
        var w = new byte[4];
        if (ReadFull(w, 4) != 4) throw new InvalidDataException("LZ4 frame cut short");
        uint bs = BinaryPrimitives.ReadUInt32LittleEndian(w);
        if (bs == 0) { _end = true; return; }   // end mark (the content checksum after it is not read)
        bool stored = (bs & 0x80000000u) != 0;
        int n = (int)(bs & 0x7FFFFFFFu);
        if (n > _blockMax) throw new InvalidDataException("LZ4 block too big");
        if (_indep) _histLen = 0;
        else
        {
            // linked blocks: the last 64 KB decoded stay just before the new block
            int keep = Math.Min(Hist, _histLen + _prevLen);
            _out.AsSpan(Hist + _prevLen - keep, keep).CopyTo(_out.AsSpan(Hist - keep));
            _histLen = keep;
        }
        int len;
        if (stored)
        {
            if (ReadFull(_out.AsSpan(Hist, n)) != n) throw new InvalidDataException("LZ4 block cut short");
            len = n;
        }
        else
        {
            if (ReadFull(_in.AsSpan(0, n)) != n) throw new InvalidDataException("LZ4 block cut short");
            len = DecodeBlock(_in, n, _out, Hist, Hist + _blockMax, Hist - _histLen);
        }
        if (_blockSum) Skip(4);
        _prevLen = len;
        _pos = Hist; _lim = Hist + len;
    }

    /// <summary>One LZ4 block into dst[d0..dEnd); matches may reach back to dst[lowest]. Returns the decoded length.</summary>
    static int DecodeBlock(byte[] src, int n, byte[] dst, int d0, int dEnd, int lowest)
    {
        int p = 0, d = d0;
        while (true)
        {
            if (p >= n) throw new InvalidDataException("LZ4 block: bad sequence");
            int token = src[p++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { if (p >= n) throw new InvalidDataException("LZ4 block: bad length"); b = src[p++]; lit += b; } while (b == 255); }
            if (lit > n - p || lit > dEnd - d) throw new InvalidDataException("LZ4 block: literals out of range");
            src.AsSpan(p, lit).CopyTo(dst.AsSpan(d));
            p += lit; d += lit;
            if (p == n) break;   // the last sequence has literals only
            if (p + 2 > n) throw new InvalidDataException("LZ4 block: bad offset");
            int off = src[p] | (src[p + 1] << 8); p += 2;
            int ml = token & 15;
            if (ml == 15) { int b; do { if (p >= n) throw new InvalidDataException("LZ4 block: bad length"); b = src[p++]; ml += b; } while (b == 255); }
            ml += 4;
            int m = d - off;
            if (off == 0 || m < lowest || ml > dEnd - d) throw new InvalidDataException("LZ4 block: match out of range");
            if (off >= ml) dst.AsSpan(m, ml).CopyTo(dst.AsSpan(d));
            else for (int i = 0; i < ml; i++) dst[d + i] = dst[m + i];   // overlapping: byte by byte
            d += ml;
        }
        return d - d0;
    }

    int ReadFull(byte[] b, int n) => ReadFull(b.AsSpan(0, n));
    int ReadFull(Span<byte> b)
    {
        int t = 0;
        while (t < b.Length) { int k = _s.Read(b[t..]); if (k == 0) break; t += k; }
        return t;
    }
    void Skip(int n) { var b = new byte[n]; if (ReadFull(b, n) != n) throw new InvalidDataException("LZ4 header cut short"); }
}
