using System.IO;
using System.Security.Cryptography;

namespace ModSwitcher2;

/// <summary>
/// A patch recipe (tools/mkrecipe.py, notes/16 section 3.1): our changed bytes only. The player's own original +
/// the recipe = the patched file, so no game file ships with the app.
/// </summary>
public sealed class Recipe
{
    public string Target = "", Source = "", Pack = "none", Orig = "", Result = "";
    public int Size;
    public readonly List<(int Off, string Donor, int DonorOff, int Len, string Sha)> Inserts = new();
    public readonly List<(int Off, byte[] Data)> Patches = new();

    public static Recipe Parse(string path)
    {
        var r = new Recipe();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (f[0])
            {
                case "target": r.Target = f[1]; break;
                case "source": r.Source = f[1]; break;
                case "pack": r.Pack = f[1]; break;
                case "orig": r.Orig = f[1]; break;
                case "result": r.Result = f[1]; break;
                case "size": r.Size = int.Parse(f[1]); break;
                case "insert": r.Inserts.Add((Hex(f[1]), f[2], Hex(f[3]), int.Parse(f[4]), f[5])); break;
                case "patch": r.Patches.Add((Hex(f[1]), Convert.FromHexString(f[2]))); break;
                default: throw new InvalidDataException($"{Path.GetFileName(path)}: unknown line '{f[0]}'");
            }
        }
        if (r.Target.Length == 0 || r.Orig.Length != 64 || r.Result.Length != 64)
            throw new InvalidDataException($"{Path.GetFileName(path)}: not a complete recipe");
        return r;
        static int Hex(string s) => Convert.ToInt32(s, 16);
    }

    public static readonly byte[] Lz4Magic = { 0x04, 0x22, 0x4d, 0x18 };

    public static byte[] Unpack(byte[] data) =>
        data.Length >= 4 && data.AsSpan(0, 4).SequenceEqual(Lz4Magic) ? GowrTools.Lz4Decompress(data) : data;

    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>
    /// The patched file, ready to write (packed again when the target is an LZ4 file).
    /// original: the raw bytes of the player's untouched file; donor(target) gives the raw bytes of a donor file.
    /// </summary>
    public byte[] Build(byte[] original, Func<string, byte[]> donor)
    {
        var o = Unpack(original);
        if (Sha(o) != Orig)
            throw new InvalidDataException($"{Path.GetFileName(Target)}: the original on this PC does not match the recipe (another game version, or the file was changed by another mod). Steam -> Verify integrity of game files, then try again.");
        var b = new List<byte>(o);
        foreach (var (off, d, doff, len, sha) in Inserts)
        {
            var src = Unpack(donor(d));
            if (doff + len > src.Length) throw new InvalidDataException($"donor {Path.GetFileName(d)} is too short");
            var blk = src.AsSpan(doff, len).ToArray();
            if (Sha(blk) != sha) throw new InvalidDataException($"donor {Path.GetFileName(d)} does not match the recipe");
            b.InsertRange(off, blk);
        }
        var arr = b.ToArray();
        foreach (var (off, data) in Patches)
        {
            if (off + data.Length > arr.Length) throw new InvalidDataException("patch past the end of the file");
            data.CopyTo(arr, off);
        }
        if (Sha(arr) != Result) throw new InvalidDataException($"{Path.GetFileName(Target)}: result hash mismatch");
        return Pack == "lz4" ? GowrTools.Lz4Compress(arr) : arr;
    }

    /// <summary>Where the player's original of a "dump" recipe is: the MIO dump next to the override.</summary>
    public static string DumpPath(string target) =>
        target.StartsWith(@"mod\int9\", StringComparison.OrdinalIgnoreCase) ? @"mod\dump\int9\" + target[@"mod\int9\".Length..] : target;
}
