using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class GowrTools
{
    // ---------- StringHash: 64-bit Jenkins one-at-a-time over the UPPERCASED string, no final mix ----------
    public static ulong SmsHash(string s)
    {
        ulong h = 0;
        foreach (byte c0 in Encoding.ASCII.GetBytes(s.ToUpperInvariant()))
        {
            h += c0; h += (h << 10); h ^= (h >> 6);
        }
        return h;
    }

    // ---------- xxHash32 (LZ4 frame checksums) ----------
    const uint P1 = 2654435761U, P2 = 2246822519U, P3 = 3266489917U, P4 = 668265263U, P5 = 374761393U;
    static uint Rotl(uint x, int r) { return (x << r) | (x >> (32 - r)); }
    public static uint XXH32(byte[] d, int off, int len, uint seed)
    {
        int p = off, end = off + len; uint h;
        if (len >= 16)
        {
            uint v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
            int limit = end - 16;
            do
            {
                v1 = Rotl(v1 + BitConverter.ToUInt32(d, p) * P2, 13) * P1; p += 4;
                v2 = Rotl(v2 + BitConverter.ToUInt32(d, p) * P2, 13) * P1; p += 4;
                v3 = Rotl(v3 + BitConverter.ToUInt32(d, p) * P2, 13) * P1; p += 4;
                v4 = Rotl(v4 + BitConverter.ToUInt32(d, p) * P2, 13) * P1; p += 4;
            } while (p <= limit);
            h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
        }
        else h = seed + P5;
        h += (uint)len;
        while (p + 4 <= end) { h += BitConverter.ToUInt32(d, p) * P3; h = Rotl(h, 17) * P4; p += 4; }
        while (p < end) { h += d[p] * P5; h = Rotl(h, 11) * P1; p++; }
        h ^= h >> 15; h *= P2; h ^= h >> 13; h *= P3; h ^= h >> 16;
        return h;
    }

    // ---------- LZ4 frame: decompress ----------
    public static byte[] Lz4Read(string path) { return Lz4Decompress(File.ReadAllBytes(path)); }
    public static byte[] Lz4Decompress(byte[] src)
    {
        if (BitConverter.ToUInt32(src, 0) != 0x184D2204) throw new Exception("not an LZ4 frame");
        byte flg = src[4];
        int p = 6; ulong contentSize = 0;
        if ((flg & 0x08) != 0) { contentSize = BitConverter.ToUInt64(src, p); p += 8; }
        if ((flg & 0x01) != 0) p += 4;
        p += 1; // header checksum
        bool blockChecksum = (flg & 0x10) != 0;
        var outp = new MemoryStream(contentSize > 0 ? (int)contentSize : src.Length * 3);
        while (true)
        {
            uint bs = BitConverter.ToUInt32(src, p); p += 4;
            if (bs == 0) break;
            bool raw = (bs & 0x80000000) != 0; int n = (int)(bs & 0x7FFFFFFF);
            if (raw) outp.Write(src, p, n);
            else DecodeBlock(src, p, n, outp);
            p += n; if (blockChecksum) p += 4;
        }
        return outp.ToArray();
    }
    static void DecodeBlock(byte[] s, int p, int n, MemoryStream o)
    {
        int end = p + n;
        byte[] buf = o.GetBuffer();
        while (p < end)
        {
            int token = s[p++];
            int lit = token >> 4;
            if (lit == 15) { int b; do { b = s[p++]; lit += b; } while (b == 255); }
            o.Write(s, p, lit); p += lit;
            if (p >= end) break;
            int off = s[p] | (s[p + 1] << 8); p += 2;
            int ml = token & 15;
            if (ml == 15) { int b; do { b = s[p++]; ml += b; } while (b == 255); }
            ml += 4;
            long start = o.Length - off;
            for (int i = 0; i < ml; i++) { buf = o.GetBuffer(); o.WriteByte(buf[start + i]); }
        }
    }

    // ---------- LZ4 frame: compress (64 KB independent blocks, content size + content checksum = game settings) ----------
    public static void Lz4Write(string path, byte[] data) { SafeWrite(path, Lz4Compress(data)); }

    // ---------- crash-safe file write: <path>.sbgtmp in the same folder, flushed to disk, then swapped in ----------
    // Works on .NET Framework (the Switcher runs in Windows PowerShell 5.1): File.Replace when the target exists, else File.Move.
    // On any error the .sbgtmp is deleted and the target is left as it was.
    public static void SafeWrite(string path, byte[] bytes)
    {
        string full = Path.GetFullPath(path);
        string tmp = full + ".sbgtmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(full)) File.Replace(tmp, full, null);
            else File.Move(tmp, full);
        }
        catch (IOException e) { throw new IOException(full + ": " + e.Message, e); }
        catch (UnauthorizedAccessException e) { throw new UnauthorizedAccessException(full + ": " + e.Message, e); }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public static byte[] Lz4Compress(byte[] data)
    {
        var o = new MemoryStream();
        var bw = new BinaryWriter(o);
        bw.Write(0x184D2204u);
        byte flg = 0x6C, bd = 0x40;
        var hdr = new byte[10]; hdr[0] = flg; hdr[1] = bd;
        BitConverter.GetBytes((ulong)data.Length).CopyTo(hdr, 2);
        bw.Write(hdr);
        bw.Write((byte)((XXH32(hdr, 0, 10, 0) >> 8) & 0xFF));
        const int BLK = 65536;
        for (int p = 0; p < data.Length; p += BLK)
        {
            int n = Math.Min(BLK, data.Length - p);
            byte[] c = CompressBlock(data, p, n);
            if (c.Length >= n) { bw.Write((uint)n | 0x80000000u); bw.Write(data, p, n); }
            else { bw.Write((uint)c.Length); bw.Write(c); }
        }
        bw.Write(0u);
        bw.Write(XXH32(data, 0, data.Length, 0));
        bw.Flush();
        return o.ToArray();
    }
    static byte[] CompressBlock(byte[] s, int start, int n)
    {
        var o = new MemoryStream(n);
        int end = start + n, mflimit = end - 12, matchlimit = end - 5;
        int[] table = new int[1 << 16];
        for (int i = 0; i < table.Length; i++) table[i] = -1;
        int anchor = start, p = start;
        if (n >= 13)
        {
            while (p < mflimit)
            {
                uint seq = BitConverter.ToUInt32(s, p);
                int h = (int)((seq * 2654435761U) >> 16);
                int r = table[h]; table[h] = p;
                if (r >= start && p - r < 65536 && BitConverter.ToUInt32(s, r) == seq)
                {
                    int ml = 4;
                    while (p + ml < matchlimit && s[r + ml] == s[p + ml]) ml++;
                    EmitSeq(o, s, anchor, p - anchor, p - r, ml);
                    p += ml; anchor = p;
                }
                else p++;
            }
        }
        // last literals
        int lit = end - anchor;
        int tok = Math.Min(lit, 15) << 4;
        o.WriteByte((byte)tok);
        if (lit >= 15) { int l = lit - 15; while (l >= 255) { o.WriteByte(255); l -= 255; } o.WriteByte((byte)l); }
        o.Write(s, anchor, lit);
        return o.ToArray();
    }
    static void EmitSeq(MemoryStream o, byte[] s, int litStart, int lit, int off, int ml)
    {
        int m = ml - 4;
        int tok = (Math.Min(lit, 15) << 4) | Math.Min(m, 15);
        o.WriteByte((byte)tok);
        if (lit >= 15) { int l = lit - 15; while (l >= 255) { o.WriteByte(255); l -= 255; } o.WriteByte((byte)l); }
        o.Write(s, litStart, lit);
        o.WriteByte((byte)(off & 0xFF)); o.WriteByte((byte)(off >> 8));
        if (m >= 15) { int l = m - 15; while (l >= 255) { o.WriteByte(255); l -= 255; } o.WriteByte((byte)l); }
    }

    // ---------- gbl_wadsizetable.dcb ----------
    // Header (checked on the real file 2026-10-15, n = 1836): two (u64 self-relative pointer, u64 count) pairs.
    //   0x310 ptr (0x40)   -> hash array at 0x350: u64 SmsHash("WAD_<name>") per wad;   0x318 count
    //   0x320 ptr (0x3990) -> size array at 0x3cb0: u32 slot size per wad (same order); 0x328 count
    //   0x340 ptr (0x5620) -> third array at 0x5960 = end of the size array, n * 4 bytes (all zero, meaning unknown,
    //                         never written); 0x348 count
    // The arrays are found through the pointers. A header that does not add up is refused before anything is changed.
    const int WstHashPtr = 0x310, WstHashN = 0x318, WstSizePtr = 0x320, WstSizeN = 0x328, WstHeaderEnd = 0x330;
    const int WstThirdPtr = 0x340, WstThirdN = 0x348, WstHeaderEnd3 = 0x350;
    public static long[] WadSizeLayout(byte[] st) // { count, hash array offset, size array offset }
    {
        if (st.Length < WstHeaderEnd) throw WstBad("file is only " + st.Length + " bytes");
        ulong len = (ulong)st.Length;
        ulong n = BitConverter.ToUInt64(st, WstHashN), n2 = BitConverter.ToUInt64(st, WstSizeN);
        if (n != n2) throw WstBad("hash count " + n + " (0x318) differs from size count " + n2 + " (0x328)");
        ulong ph = BitConverter.ToUInt64(st, WstHashPtr), ps = BitConverter.ToUInt64(st, WstSizePtr);
        if (ph == 0 || ps == 0) throw WstBad("null array pointer");
        if (n > len || ph > len || ps > len) throw WstBad("count or pointer past the end of the file");
        ulong h0 = WstHashPtr + ph, h1 = h0 + 8 * n, s0 = WstSizePtr + ps, s1 = s0 + 4 * n;
        if (h0 < WstHeaderEnd3 || s0 < WstHeaderEnd3) throw WstBad("an array starts inside the header (0x0..0x350)");
        if (h1 > len) throw WstBad("hash array 0x" + h0.ToString("x") + "..0x" + h1.ToString("x") + " runs past the end 0x" + len.ToString("x"));
        if (s1 > len) throw WstBad("size array 0x" + s0.ToString("x") + "..0x" + s1.ToString("x") + " runs past the end 0x" + len.ToString("x"));
        if (n > 0 && h0 < s1 && s0 < h1) throw WstBad("hash array and size array overlap");
        if (st.Length < WstHeaderEnd3) throw WstBad("file is only " + st.Length + " bytes");
        ulong n3 = BitConverter.ToUInt64(st, WstThirdN), p3 = BitConverter.ToUInt64(st, WstThirdPtr);
        if (n3 != n) throw WstBad("third count " + n3 + " (0x348) differs from hash count " + n + " (0x318)");
        if (p3 > len || WstThirdPtr + p3 != s1) throw WstBad("third pointer (0x340) does not land at the end of the size array 0x" + s1.ToString("x"));
        if (s1 + 4 * n > len) throw WstBad("third array 0x" + s1.ToString("x") + "..0x" + (s1 + 4 * n).ToString("x") + " runs past the end 0x" + len.ToString("x"));
        return new long[] { (long)n, (long)h0, (long)s0 };
    }
    static Exception WstBad(string why) { return new Exception("gbl_wadsizetable header does not add up (" + why + "): refused, nothing written"); }
    public static uint GetWadSize(byte[] st, string wadName)
    {
        long[] L = WadSizeLayout(st);
        int i = SizeIndex(st, L, wadName); return BitConverter.ToUInt32(st, (int)L[2] + 4 * i);
    }
    public static void SetWadSize(byte[] st, string wadName, uint size)
    {
        long[] L = WadSizeLayout(st);
        int i = SizeIndex(st, L, wadName); BitConverter.GetBytes(size).CopyTo(st, (int)L[2] + 4 * i);
    }
    static int SizeIndex(byte[] st, long[] L, string wadName)
    {
        int n = (int)L[0], h0 = (int)L[1];
        ulong h = SmsHash("WAD_" + wadName);
        for (int i = 0; i < n; i++) if (BitConverter.ToUInt64(st, h0 + 8 * i) == h) return i;
        throw new Exception("wad '" + wadName + "' not found in gbl_wadsizetable");
    }

    // ---------- wad header heap fields (must equal the slot size) ----------
    public static int SetWadHeap(byte[] w, uint oldSize, uint newSize)
    {
        if (BitConverter.ToUInt32(w, 0xc) != oldSize) throw new Exception("wad header size is not the expected " + oldSize);
        int count = (int)BitConverter.ToUInt32(w, 8);
        int baseOff = 0x40 + count * 0x90;
        int changed = 0;
        BitConverter.GetBytes(newSize).CopyTo(w, 0xc); changed++;
        for (int o = Math.Max(0x10, baseOff - 0x100); o < Math.Min(w.Length - 4, baseOff + 0x100); o += 4)
            if (BitConverter.ToUInt32(w, o) == oldSize) { BitConverter.GetBytes(newSize).CopyTo(w, o); changed++; }
        if (changed != 3) throw new Exception("expected 3 heap fields, found " + changed);
        return changed;
    }

    // ---------- King Hrolf banter speaker (King's Grave level wad; tools/kingvoice.py has the full story) ----------
    // Every banter line record starts with LineMarker; its speaker hash is at +0x20. A real character speaker resolves
    // to the first matching creature (King 1); the Source placeholder resolves to the King who started the line.
    const ulong LineMarker = 0x00004ef0aaf53521UL, KingSpeaker = 0xfcb7e07b1524298fUL;
    static readonly ulong SourceSpeaker = SmsHash("Source");
    static readonly ulong KingFightStart = SmsHash("XPL_WarriorSouls_s130_KingHrolfrFightStart"); // intro, no source: keep
    static int KingVoiceScan(byte[] w, int mode) // mode 0: count King, 1: count Source, 2: King->Source, 3: Source->King
    {
        int n = 0; ulong conv = 0;
        for (int i = 0x20; i + 0x28 <= w.Length; i += 8)
        {
            if (BitConverter.ToUInt64(w, i) != LineMarker) continue;
            if (BitConverter.ToUInt64(w, i - 0x20) == BitConverter.ToUInt64(w, i - 0x10) && BitConverter.ToUInt64(w, i - 8) == 0)
                conv = BitConverter.ToUInt64(w, i - 0x20);
            ulong spk = BitConverter.ToUInt64(w, i + 0x20);
            if (mode == 0 && spk == KingSpeaker) n++;
            else if (mode == 1 && spk == SourceSpeaker) n++;
            else if (mode == 2 && spk == KingSpeaker && conv != KingFightStart) { BitConverter.GetBytes(SourceSpeaker).CopyTo(w, i + 0x20); n++; }
            else if (mode == 3 && spk == SourceSpeaker) { BitConverter.GetBytes(KingSpeaker).CopyTo(w, i + 0x20); n++; }
        }
        return n;
    }
    public static int[] KingVoiceCount(byte[] w) { return new int[] { KingVoiceScan(w, 0), KingVoiceScan(w, 1) }; }
    public static int KingVoiceSet(byte[] w, bool on) { return KingVoiceScan(w, on ? 2 : 3); }

    // ---------- visual script: rewrite a StringHash node's name in place ----------
    const ulong StringHashType = 0x99918f2b0491b355UL;
    public static void SetStringHash(byte[] vs, int nodeOff, string name)
    {
        if (BitConverter.ToUInt64(vs, nodeOff) != StringHashType) throw new Exception("not a StringHash node at 0x" + nodeOff.ToString("x"));
        int so = nodeOff + 24 + (int)(BitConverter.ToUInt64(vs, nodeOff + 24) & 0xffffffffffffffUL);
        int cap = (int)BitConverter.ToUInt32(vs, so + 4); // buffer size incl. terminator
        int co = so + 8 + (int)(BitConverter.ToUInt64(vs, so + 8) & 0xffffffffffffffUL);
        byte[] nb = Encoding.ASCII.GetBytes(name);
        if (nb.Length + 1 > cap) throw new Exception("name '" + name + "' too long for slot (max " + (cap - 1) + ")");
        BitConverter.GetBytes(SmsHash(name)).CopyTo(vs, nodeOff + 16);
        BitConverter.GetBytes((uint)nb.Length).CopyTo(vs, so);
        BitConverter.GetBytes((uint)(nb.Length + 1)).CopyTo(vs, so + 4);
        for (int i = 0; i < cap; i++) vs[co + i] = i < nb.Length ? nb[i] : (byte)0;
    }
    public static void SetU32(byte[] b, int off, uint v) { BitConverter.GetBytes(v).CopyTo(b, off); }
    public static uint GetU32(byte[] b, int off) { return BitConverter.ToUInt32(b, off); }
    public static float GetF32(byte[] b, int off) { return BitConverter.ToSingle(b, off); }
    public static void SetF32(byte[] b, int off, float v) { BitConverter.GetBytes(v).CopyTo(b, off); }
}
