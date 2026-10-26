using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace ModSwitcher2;

/// <summary>One MIO dump file the app can take from the player's own installed wad.</summary>
/// <param name="Wad">wad name in exec\wad\pc_le (no extension)</param>
/// <param name="DumpRel">where GoWR MIO writes the dump, relative to the game folder</param>
/// <param name="Lua">true = a Lua record (heroa00 main.lua), false = a 'zZ' visual script blob</param>
public sealed record DumpTarget(string Wad, string DumpRel, string Sha, int Size, bool Lua)
{
    /// <summary>The game path inside the wad: mod\dump\int9\gameart\... -> gameart\...</summary>
    public string GamePath => DumpRel[@"mod\dump\int9\".Length..].ToLowerInvariant();
    public string Name => Path.GetFileName(DumpRel);
}

public enum FillStatus { Present, Filled, NoWad, NotFound, Differs, Failed }

public sealed record FillResult(DumpTarget Target, FillStatus Status, string? Sha, long Ms, string Why)
{
    /// <summary>A line for the apply log; null = nothing worth saying (already there, or no wad in a test sandbox).</summary>
    public string? LogLine => Status switch
    {
        FillStatus.Filled => $"{Target.Name}: taken from your game's {Target.Wad}.wad (the MIO dump was missing)",
        FillStatus.NotFound or FillStatus.Differs => $"{Target.Name}: not taken from {Target.Wad}.wad, game version differs ({Why})",
        FillStatus.Failed => $"{Target.Name}: not taken from {Target.Wad}.wad: {Why}",
        _ => null,
    };
}

/// <summary>
/// Fills a missing MIO dump from the player's own installed wad, so a recipe or the loader works before the player has
/// started the game with the dump on. Only the player's own bytes are copied on their own PC (no game file ships): the
/// bytes must have the exact sha256 the recipes were made from, else nothing is written ("game version differs").
/// Facts: research/switcher2/facts.md (2026-10-25). Read-only on the wads; a dump already there is never touched.
/// </summary>
public static class DumpFill
{
    const string VsDir = @"mod\int9\gameart\visualscripts\valhalla\levels\valhalla\";

    public static readonly DumpTarget[] Targets =
    {
        // orig of recipes/val_val100_realmhub_door_gate.recipe
        new("val_val100_realmhub", Recipe.DumpPath(VsDir + @"valhalla\val_val100_realmhub\val_val100_realmhub.visualscript"),
            "4450eb155494b1cdefaf828007463a6e0a155d670a4146a2896e3d85b8eaa344", 1226776, false),
        // orig of every recipes/tyrarena_*.recipe
        new("val_grc800_tyrarena", Recipe.DumpPath(VsDir + @"greece\val_grc800_tyrarena\val_grc800_tyrarena.visualscript"),
            "95e5ad76433613c6da8462b1491adfef9e91352c4b80f767827057c6b445cc1d", 302008, false),
        // Kratos' script, read by the loader stub in game (Loader.Dump)
        new("r_heroa00", Loader.Dump, "d063f99df3f859b462e91be3487e8373c77244c901ab8edb724ebb980d905f2c", 239579, true),
        // the Valhalla progression script, read by the loadout item list (ItemsGen.DumpScript); its wad is not r_perm
        new("val010_realmscripts", ItemsGen.DumpScript,
            "bd5a7f4598eb46e6bdfd4a83b27ad7e2a6b90357f0c747397ac67be7a3aa23a9", 998048, false),
    };

    // a wad that did not give the right bytes is not decoded again in this run (same size + time = same file)
    static readonly Dictionary<string, (long Len, DateTime Time)> Refused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For each target whose dump is missing under <paramref name="outRoot"/> (default: the game folder): take it from
    /// gameRoot\exec\wad\pc_le\&lt;wad&gt;.wad, check size + sha256, write it (temp file + move). Never throws for one
    /// target; slow (r_heroa00: ~100 MB read): call it off the UI thread. <paramref name="only"/>: a part of the wad
    /// name (tests).
    /// </summary>
    public static List<FillResult> FillMissing(string gameRoot, string? outRoot = null, string? only = null)
    {
        var res = new List<FillResult>();
        foreach (var t in Targets)
        {
            if (only != null && !t.Wad.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            var sw = Stopwatch.StartNew();
            FillResult R(FillStatus s, string? sha = null, string why = "") => new(t, s, sha, sw.ElapsedMilliseconds, why);
            var dst = Path.GetFullPath(Path.Combine(outRoot ?? gameRoot, t.DumpRel));
            if (File.Exists(dst)) { res.Add(R(FillStatus.Present)); continue; }
            var wad = Path.Combine(gameRoot, "exec", "wad", "pc_le", t.Wad + ".wad");
            try
            {
                var fi = new FileInfo(wad);
                if (!fi.Exists) { res.Add(R(FillStatus.NoWad)); continue; }
                lock (Refused)
                    if (Refused.TryGetValue(wad, out var was) && was == (fi.Length, fi.LastWriteTimeUtc))
                    {
                        res.Add(R(FillStatus.Differs, why: "checked before")); continue;
                    }
                string why;
                var bytes = t.Lua
                    ? WadFile.LuaScript(wad, "z:/int9/" + t.GamePath.Replace('\\', '/'), Path.GetFileNameWithoutExtension(t.DumpRel), out why)
                    : WadFile.VisualScript(wad, t.GamePath, out why);
                if (bytes == null) { Refuse(wad, fi); res.Add(R(FillStatus.NotFound, why: why)); continue; }
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (bytes.Length != t.Size || sha != t.Sha)
                {
                    Refuse(wad, fi);
                    res.Add(R(FillStatus.Differs, sha, $"{bytes.Length} bytes, sha256 {sha[..16]}"));
                    continue;
                }
                res.Add(Write(dst, bytes, t.Sha) ? R(FillStatus.Filled, sha) : R(FillStatus.Present, sha));
            }
            catch (Exception ex)   // one bad wad never stops an apply: the recipe step then says the dump is missing
            {
                try { Refuse(wad, new FileInfo(wad)); } catch (Exception) { }   // a damaged wad is not decoded again on the next apply
                res.Add(R(FillStatus.Failed, why: ex.Message));
            }
        }
        return res;
    }

    static void Refuse(string wad, FileInfo fi) { lock (Refused) Refused[wad] = (fi.Length, fi.LastWriteTimeUtc); }

    /// <summary>
    /// Temp file beside the target, read back and checked, then moved in. False = the dump appeared meanwhile (MIO
    /// wrote it): that one is kept.
    /// </summary>
    static bool Write(string dst, byte[] bytes, string sha)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tmp = dst + ".sbgtmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(tmp))).ToLowerInvariant() != sha)
                throw new IOException("write verify failed for " + dst);
            if (File.Exists(dst)) return false;
            try { File.Move(tmp, dst); }
            catch (IOException) when (File.Exists(dst)) { return false; }
            return true;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
