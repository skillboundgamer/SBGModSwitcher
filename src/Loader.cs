using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ModSwitcher2;

/// <summary>One file of the loader chain: the app's copy (data root), where it goes in the game, and the text that
/// marks a copy of ours already there (a developer's own probe / live.lua: never overwritten).</summary>
public sealed record LoaderFile(string Src, string Game, string Ours);

/// <summary>What the app installed (loader\loader.json): the game file, the sha256 it wrote, its backup of the file that was there.</summary>
public sealed class LoaderRecord
{
    /// <summary>The game folder (full path): records of another folder (a test sandbox, a second copy) are never touched.</summary>
    public string Root { get; set; } = "";
    public string Game { get; set; } = "";
    public string Sha { get; set; } = "";
    /// <summary>During an update only: the sha of our earlier file, so a crash between the record save and the move
    /// still knows the file that is in the game (cleared after the move).</summary>
    public string? PrevSha { get; set; }
    public bool Matches(string sha) => sha == Sha || (PrevSha != null && sha == PrevSha);
    public string? Backup { get; set; }
}

/// <summary>
/// The loader chain the Kratos loadout needs in the game (plan 2026-10-20, notes/state/tyr.md): the probe loader stub
/// (Kratos' main.lua replacement: runs the game's own script from the player's MIO dump, then our tail), the probe
/// tail mod\sbg_probe.lua (it loads mod\live.lua every ~2 s) and a small public mod\live.lua that loads only
/// mod\sbg_loadout.lua. No game code ships: the game's script comes from the player's own dump.
/// Rules: the player's MIO must dump and replace Lua and the dump must be Lua 5.2 bytecode (Problem), else nothing is
/// installed; a missing file is installed; a file of ours already there (the developer's own probe or live.lua) is left
/// alone; any other file is backed up first. Remove takes out only what this app wrote and puts the backups back.
/// </summary>
public static class Loader
{
    public const string Ini = @"mod\GOWR_MIO.ini";
    public const string Dump = @"mod\dump\int9\gameart\scripts\characters\heroa00\main.lua";
    public static readonly LoaderFile[] Files =
    {
        new(@"versions\probe_stub_v20.lua", @"mod\int9\gameart\scripts\characters\heroa00\main.lua", "SBG probe"),
        new(@"tools\probe_tail_v115.lua", @"mod\sbg_probe.lua", "SBG probe"),
        new(@"versions\live_public.lua", @"mod\live.lua", "SBG"),
    };
    static readonly byte[] LuaR = { 0x1b, (byte)'L', (byte)'u', (byte)'a', (byte)'R' };

    static string Dir => Path.Combine(AppSettings.Dir, "loader");
    static string StatePath => Path.Combine(Dir, "loader.json");

    /// <summary>Why the loader cannot work on this game, in plain words; null = it can.</summary>
    public static string? Problem(string gameRoot)
    {
        var ini = Path.Combine(gameRoot, Ini);
        if (!File.Exists(ini)) return @"GoWR MIO is not installed (no mod\GOWR_MIO.ini).";
        var text = File.ReadAllText(ini);
        if (!IniOn(text, "dump", "lua")) return @"In mod\GOWR_MIO.ini set lua=1 under [dump], then start the game once.";
        if (!IniOn(text, "replace", "lua")) return @"In mod\GOWR_MIO.ini set lua=1 under [replace].";
        var dump = Path.Combine(gameRoot, Dump);
        if (!File.Exists(dump)) return "Start the game once and load a save: GoWR MIO then saves Kratos' script, which the loadout needs.";
        var head = new byte[LuaR.Length];
        using (var f = File.OpenRead(dump)) if (f.Read(head, 0, head.Length) != head.Length || !head.AsSpan().SequenceEqual(LuaR))
            return "Kratos' saved script is not the kind this version knows (game update?): the loadout stays off.";
        return null;
    }

    /// <summary>True when <paramref name="key"/>=1 in [<paramref name="section"/>] (MIO: only 1 is "on"; ; starts a comment).</summary>
    public static bool IniOn(string ini, string section, string key)
    {
        string cur = "";
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { cur = line.Trim('[', ']').Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0 || !cur.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) return line[(eq + 1)..].Trim() == "1";
        }
        return false;
    }

    public static List<LoaderRecord> Records()
    {
        try
        {
            if (File.Exists(StatePath))
                return JsonSerializer.Deserialize<List<LoaderRecord>>(File.ReadAllText(StatePath)) ?? new();
        }
        catch (JsonException) { /* broken file: nothing is known to be ours, so Remove removes nothing */ }
        return new();
    }

    static void SaveRecords(List<LoaderRecord> recs)
    {
        Directory.CreateDirectory(Dir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(recs, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, StatePath, overwrite: true);
    }

    /// <summary>The game folder as records hold it: full path, no trailing slash, lower case (Windows paths ignore case).</summary>
    public static string RootKey(string gameRoot) => Path.GetFullPath(gameRoot).TrimEnd('\\', '/').ToLowerInvariant();

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The files this app put into this game folder (what "Remove loader" takes out).</summary>
    public static List<LoaderRecord> Mine(string gameRoot) => Records().Where(r => r.Root == RootKey(gameRoot)).ToList();

    /// <summary>True when every loader file is in the game (ours or the developer's own): the loadout can run.</summary>
    public static bool Present(string gameRoot) => Files.All(f => File.Exists(Path.Combine(gameRoot, f.Game)));

    /// <summary>Puts the missing loader files in (see the class rules). Returns log lines; throws with the plain reason when it cannot.</summary>
    public static List<string> Install(string gameRoot, string dataRoot)
    {
        var log = new List<string>();
        if (Problem(gameRoot) is { } why) throw new InvalidOperationException(why);
        var recs = Records();
        var root = RootKey(gameRoot);
        foreach (var f in Files)   // every source first: a missing one must not leave half a chain in the game
            if (!File.Exists(Path.Combine(dataRoot, f.Src))) throw new FileNotFoundException("missing " + f.Src);
        foreach (var f in Files)
        {
            var want = File.ReadAllBytes(Path.Combine(dataRoot, f.Src));
            var dest = Path.Combine(gameRoot, f.Game);
            var have = File.Exists(dest) ? File.ReadAllBytes(dest) : null;
            if (have != null && have.AsSpan().SequenceEqual(want)) continue;                // already in
            var old = recs.FirstOrDefault(r => r.Root == root && r.Game.Equals(f.Game, StringComparison.OrdinalIgnoreCase));
            bool appWrote = have != null && old != null && old.Matches(Sha(have));        // our earlier version: update it
            if (have != null && !appWrote && Encoding.UTF8.GetString(have).Contains(f.Ours, StringComparison.Ordinal))
            {
                log.Add($"{f.Game}: your own copy is kept");                              // a developer's probe / live.lua
                continue;
            }
            string? backup = appWrote ? old!.Backup : null;                                 // an update keeps the first backup
            if (have != null && !appWrote)                                                  // another mod's file: keep it safe
            {
                // one folder per game folder, and to the millisecond: two game folders never share a backup
                backup = Path.Combine("backup", Sha(Encoding.UTF8.GetBytes(root))[..10],
                                      Path.GetFileName(f.Game) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Dir, backup))!);
                File.WriteAllBytes(Path.Combine(Dir, backup), have);
                log.Add($"{f.Game}: the file there was saved to {Path.Combine(Dir, backup)}");
            }
            // The record (with its backup) goes in BEFORE the game file changes, so a crash mid-write never leaves a
            // replaced file nobody knows about. A write that fails puts the records back as they were (review 10-21).
            var before = recs.ToList();
            var rec = new LoaderRecord { Root = root, Game = f.Game, Sha = Sha(want), Backup = backup,
                                         PrevSha = appWrote ? Sha(have!) : null };
            recs.RemoveAll(r => r.Root == root && r.Game.Equals(f.Game, StringComparison.OrdinalIgnoreCase));
            recs.Add(rec);
            SaveRecords(recs);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var tmp = dest + ".sbgtmp";
            try
            {
                File.WriteAllBytes(tmp, want);
                File.Move(tmp, dest, overwrite: true);
            }
            catch
            {
                // nothing changed in the game: the old record (an update) or none (a fresh file); no orphan backup
                SaveRecords(before);
                if (backup != null && !appWrote) try { File.Delete(Path.Combine(Dir, backup)); } catch (IOException) { }
                throw;
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            var got = File.ReadAllBytes(dest);
            if (!got.AsSpan().SequenceEqual(want))
            {
                rec.Sha = Sha(got);   // the file did change: Remove may still take it out and put the backup back
                SaveRecords(recs);
                throw new IOException(f.Game + ": write verify failed");
            }
            if (rec.PrevSha != null) { rec.PrevSha = null; SaveRecords(recs); }   // the update is in: only the new sha is ours
            log.Add($"{f.Game} <- {f.Src}" + (appWrote ? " (update)" : ""));
        }
        if (log.Any(l => l.Contains(" <- ")))
            log.Add("loader installed: load a checkpoint (or restart the game) once so Kratos' script picks it up");
        return log;
    }

    /// <summary>Takes out the files this app wrote (only when they are still unchanged) and puts the backups back.</summary>
    public static List<string> Remove(string gameRoot)
    {
        var log = new List<string>();
        var recs = Records();
        var root = RootKey(gameRoot);
        foreach (var r in recs.Where(r => r.Root == root).ToList())
        {
            var dest = Path.Combine(gameRoot, r.Game);
            if (File.Exists(dest) && !r.Matches(Sha(File.ReadAllBytes(dest))))
                log.Add($"{r.Game}: changed since the app put it in, left as it is");
            else
            {
                if (File.Exists(dest)) File.Delete(dest);
                var bak = r.Backup == null ? null : Path.Combine(Dir, r.Backup);
                if (bak != null && File.Exists(bak)) { File.Copy(bak, dest, overwrite: true); log.Add($"{r.Game}: your earlier file is back"); }
                else log.Add($"{r.Game}: removed");
            }
            recs.Remove(r);
            SaveRecords(recs);
        }
        return log;
    }
}
