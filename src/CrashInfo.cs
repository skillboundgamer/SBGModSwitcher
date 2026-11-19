using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ModSwitcher2;

/// <summary>
/// The crash section of the problem report (2.0.5 B2): reads the newest game crash dump (<c>.crashdata\reports\*.dmp</c>)
/// by hand, no extra module: time, exception code, crash address as module+offset, the modules found on the crashing
/// thread's stack (each tagged by owner) and a plain fact line for a known address. No verdict text.
/// The minidump layout follows the dev tool crashpeek.py (same streams, same offsets). Read only.
/// </summary>
public static class CrashInfo
{
    /// <summary>Known crash addresses in GoWR.exe (offset -> plain fact). Grows over time.</summary>
    public static readonly Dictionary<long, string> KnownAddresses = new()
    {
        [0x4fbf3b] = "known address: Lua out of memory (a script did too much work in one frame)",
    };

    public sealed record Module(ulong Base, uint Size, string Path, string Name);

    public sealed record Result(DateTime Time, string File, uint Code, string Where, string? AccessText,
                                List<(string Module, string Tag, int Count)> StackModules, string? Known);

    public const string ReportsRel = @".crashdata\reports";

    /// <summary>The newest .dmp in the game's crash folder, or null.</summary>
    public static FileInfo? Newest(string? gameDir)
    {
        try
        {
            if (string.IsNullOrEmpty(gameDir)) return null;
            var dir = new DirectoryInfo(System.IO.Path.Combine(gameDir, ReportsRel));
            if (!dir.Exists) return null;
            return dir.EnumerateFiles("*.dmp", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception) { return null; }
    }

    static uint U32(byte[] d, long o) => BitConverter.ToUInt32(d, (int)o);
    static ulong U64(byte[] d, long o) => BitConverter.ToUInt64(d, (int)o);

    /// <summary>Parse one minidump. <paramref name="tagOf"/> gives the owner tag of a module by its full path.
    /// Throws on a file that is not a minidump (the caller prints "could not read").</summary>
    public static Result Read(string path, Func<string, string> tagOf)
    {
        var d = File.ReadAllBytes(path);
        if (d.Length < 32 || d[0] != 'M' || d[1] != 'D' || d[2] != 'M' || d[3] != 'P') throw new InvalidDataException("not a minidump");
        uint nStreams = U32(d, 8), dirRva = U32(d, 12);
        var time = DateTimeOffset.FromUnixTimeSeconds(U32(d, 20)).LocalDateTime;
        var streams = new Dictionary<uint, (uint Rva, uint Size)>();
        for (uint i = 0; i < nStreams; i++)
        {
            long o = dirRva + 12 * i;
            streams[U32(d, o)] = (U32(d, o + 8), U32(d, o + 4));
        }

        var mods = new List<Module>();
        if (streams.TryGetValue(4, out var ms))
        {
            uint n = U32(d, ms.Rva);
            for (uint i = 0; i < n; i++)
            {
                long o = ms.Rva + 4 + 108 * i;
                uint nameRva = U32(d, o + 20), len = U32(d, nameRva);
                var full = Encoding.Unicode.GetString(d, (int)nameRva + 4, (int)len);
                mods.Add(new Module(U64(d, o), U32(d, o + 8), full, full.Split('\\').Last()));
            }
        }
        Module? ModOf(ulong a) => mods.FirstOrDefault(m => m.Base <= a && a < m.Base + m.Size);
        string Where(ulong a) { var m = ModOf(a); return m == null ? $"0x{a:x} (no module)" : $"{m.Name}+0x{a - m.Base:x}"; }

        if (!streams.TryGetValue(6, out var ex)) throw new InvalidDataException("no exception stream");
        uint code = U32(d, ex.Rva + 8);
        ulong addr = U64(d, ex.Rva + 24);
        uint nParam = U32(d, ex.Rva + 32);
        string? access = null;
        if (code == 0xC0000005 && nParam >= 2)
        {
            ulong kind = U64(d, ex.Rva + 40), target = U64(d, ex.Rva + 48);
            access = $"access violation: {(kind == 0 ? "read" : kind == 1 ? "write" : kind == 8 ? "execute" : "?")} address 0x{target:x}";
        }
        uint ctxRva = U32(d, ex.Rva + 164);
        ulong rsp = U64(d, ctxRva + 0x98);

        // memory ranges: Memory64List (9) and MemoryList (5)
        var ranges = new List<(ulong Start, ulong Size, ulong Off)>();
        if (streams.TryGetValue(9, out var m64))
        {
            ulong n = U64(d, m64.Rva), off = U64(d, m64.Rva + 8);
            for (ulong i = 0; i < n; i++)
            {
                ulong s = U64(d, m64.Rva + 16 + 16 * (long)i), sz = U64(d, m64.Rva + 24 + 16 * (long)i);
                ranges.Add((s, sz, off)); off += sz;
            }
        }
        if (streams.TryGetValue(5, out var m32))
        {
            uint n = U32(d, m32.Rva);
            for (uint i = 0; i < n; i++)
            {
                long o = m32.Rva + 4 + 16 * i;
                ranges.Add((U64(d, o), U32(d, o + 8), U32(d, o + 12)));
            }
        }

        // modules on the stack: the crash address first, then every 8-byte stack value that points into a module (first 64 KB)
        var counts = new Dictionary<Module, int>();
        void Count(ulong a) { var m = ModOf(a); if (m != null) counts[m] = counts.GetValueOrDefault(m) + 1; }
        Count(addr);
        foreach (var (start, size, off) in ranges)
        {
            if (rsp < start || rsp >= start + size) continue;
            ulong from = off + (rsp - start), end = Math.Min(off + size, from + 0x10000);
            for (ulong o = from; o + 8 <= end && o + 8 <= (ulong)d.Length; o += 8) Count(U64(d, (long)o));
            break;
        }
        var stack = counts.OrderByDescending(kv => kv.Value)
                          .Select(kv => (kv.Key.Name, tagOf(kv.Key.Path), kv.Value)).ToList();

        string? known = null;
        var cm = ModOf(addr);
        if (cm != null && cm.Name.Equals("GoWR.exe", StringComparison.OrdinalIgnoreCase)
            && KnownAddresses.TryGetValue((long)(addr - cm.Base), out var k)) known = k;
        return new Result(time, System.IO.Path.GetFileName(path), code, Where(addr), access, stack, known);
    }

    /// <summary>Owner tag of a module path: game, system, MIO winmm, VOTG winmm, ours, other.</summary>
    public static string TagModule(string modulePath, string? gameDir, bool votgLoader)
    {
        var name = System.IO.Path.GetFileName(modulePath).ToLowerInvariant();
        bool inGame = !string.IsNullOrEmpty(gameDir) &&
                      modulePath.StartsWith(gameDir!.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        if (inGame && name == "winmm.dll") return votgLoader ? "VOTG winmm" : "MIO winmm";
        if (inGame && name == "version.dll") return "ours";
        if (name == "gowr.exe" || inGame) return "game";
        if (modulePath.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase)) return "system";
        return "other";
    }
}
