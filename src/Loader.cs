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
        return DumpProblem(gameRoot);
    }

    /// <summary>Why Kratos' saved script cannot be used (missing, or not Lua 5.2 bytecode); null = fine.</summary>
    public static string? DumpProblem(string gameRoot)
    {
        var dump = Path.Combine(gameRoot, Dump);
        // missing but the player's wad is there: Install takes it from that wad first (DumpFill)
        if (!File.Exists(dump) && File.Exists(Path.Combine(gameRoot, "exec", "wad", "pc_le", "r_heroa00.wad"))) return null;
        if (!File.Exists(dump)) return "Kratos' script is missing (and r_heroa00.wad is not in the game): start the game once and load a save, GoWR MIO then saves it.";
        var head = new byte[LuaR.Length];
        using (var f = File.OpenRead(dump)) if (f.Read(head, 0, head.Length) != head.Length || !head.AsSpan().SequenceEqual(LuaR))
            return "Kratos' saved script is not the kind this version knows (game update?): the loadout stays off.";
        return null;
    }

    /// <summary>True when the four MIO switches the app needs are on: visualscript and lua under [dump] and [replace].</summary>
    public static bool IniAllOn(string text) =>
        IniKeys.All(k => IniOn(text, k.Section, k.Key));

    static readonly (string Section, string Key)[] IniKeys =
        { ("dump", "visualscript"), ("dump", "lua"), ("replace", "visualscript"), ("replace", "lua") };

    /// <summary>Sets <paramref name="key"/>=1 under [<paramref name="section"/>]: a 0 value is changed in place, a missing key is
    /// added right under the section line, a missing section is added at the end. Every other byte stays (comments, order,
    /// CRLF or LF). Pure text in, text out.</summary>
    public static string IniSet(string text, string section, string key)
    {
        string eol = text.Contains("\r\n") ? "\r\n" : "\n";
        string cr = eol == "\r\n" ? "\r" : "";
        var lines = text.Split('\n').ToList();   // a line keeps its own '\r'
        int head = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (i == 0 && t.StartsWith("ï»¿")) t = t[3..];   // a UTF-8 BOM read as Latin1; the bytes stay
            if (t.Length == 0 || t[0] == ';') continue;
            if (t[0] == '[')
            {
                if (head >= 0) break;                                  // the next section: the key is not in ours
                if (t.Trim('[', ']').Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) head = i;
                continue;
            }
            if (head < 0) continue;
            int eq = lines[i].IndexOf('=');
            if (eq <= 0 || !lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = lines[i][(eq + 1)..];
            int semi = rest.IndexOf(';');                                 // an inline comment stays
            var val = semi < 0 ? rest : rest[..semi];
            if (val.Trim() == "1") return text;                          // already on
            int ws = 0;
            while (ws < rest.Length && (rest[ws] == ' ' || rest[ws] == '\t')) ws++;
            // the space before the comment (or the CR) after the value stays
            lines[i] = lines[i][..(eq + 1)] + rest[..ws] + "1" + val[Math.Max(ws, val.TrimEnd().Length)..] + (semi < 0 ? "" : rest[semi..]);
            return string.Join("\n", lines);
        }
        if (head >= 0)
        {
            if (cr.Length > 0 && !lines[head].EndsWith('\r')) lines[head] += "\r";
            bool wasLast = head == lines.Count - 1;                    // header is the last line, no final newline
            lines.Insert(head + 1, key + "=1" + cr);
            if (wasLast) lines.Add("");                                // end with a proper EOL, not a lone CR
            return string.Join("\n", lines);
        }
        var sb = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) sb.Append(eol);
        sb.Append('[').Append(section).Append(']').Append(eol).Append(key).Append("=1").Append(eol);
        return sb.ToString();
    }

    static string IniPath(string gameRoot) => Path.Combine(gameRoot, Ini);
    static string IniBeside(string gameRoot) => IniPath(gameRoot) + ".sbgbak";
    static string IniCopy(string gameRoot) =>
        Path.Combine(Dir, "ini-" + Sha(Encoding.UTF8.GetBytes(RootKey(gameRoot)))[..10] + ".bak");
    static string IniWritten(string gameRoot) => IniCopy(gameRoot) + ".sha";

    /// <summary>Turns the four MIO switches on (see <see cref="IniSet"/>), after one backup of the player's ini. Returns plain text.</summary>
    public static string IniFix(string gameRoot)
    {
        var path = IniPath(gameRoot);
        if (!File.Exists(path)) return "Install GoWR MIO first";
        var raw = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(raw);     // Latin1 round-trips every byte
        var fixedText = text;
        foreach (var (sec, key) in IniKeys) fixedText = IniSet(fixedText, sec, key);
        if (fixedText == text) return "GoWR MIO is already set";
        if (!File.Exists(IniBeside(gameRoot))) File.WriteAllBytes(IniBeside(gameRoot), raw);   // never over an older backup
        if (!File.Exists(IniCopy(gameRoot)))
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(IniCopy(gameRoot), raw);
        }
        var tmp = path + ".sbgtmp";
        var written = Encoding.Latin1.GetBytes(fixedText);
        File.WriteAllBytes(tmp, written);
        File.Move(tmp, path, overwrite: true);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(IniWritten(gameRoot), Sha(written));   // Restore checks the ini is still ours
        return "GoWR MIO is set: dump and replace are on. Start the game once.";
    }

    /// <summary>True when the app changed the ini and the player's first version is still saved.</summary>
    public static bool IniBackupExists(string gameRoot) => File.Exists(IniBeside(gameRoot)) || File.Exists(IniCopy(gameRoot));

    /// <summary>Puts the player's first ini back (the backup beside it, else the app's copy) and removes the backups. Null = no backup.</summary>
    public static string? IniRestore(string gameRoot)
    {
        var src = File.Exists(IniBeside(gameRoot)) ? IniBeside(gameRoot) : File.Exists(IniCopy(gameRoot)) ? IniCopy(gameRoot) : null;
        if (src == null) return null;
        var mark = IniWritten(gameRoot);
        if (File.Exists(mark) && File.Exists(IniPath(gameRoot))
            && Sha(File.ReadAllBytes(IniPath(gameRoot))) != File.ReadAllText(mark).Trim())
            return "GoWR_MIO.ini changed since Fix it, left as it is";
        File.Copy(src, IniPath(gameRoot), overwrite: true);
        try { File.Delete(IniBeside(gameRoot)); File.Delete(IniCopy(gameRoot)); File.Delete(mark); } catch (IOException) { }
        return "GoWR_MIO.ini is back as it was";
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
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                var v = line[(eq + 1)..]; int semi = v.IndexOf(';');
                return (semi < 0 ? v : v[..semi]).Trim() == "1";
            }
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
        // Kratos' dump from the player's own wad when MIO has not saved it yet (slow: callers run off the UI thread)
        foreach (var f in DumpFill.FillMissing(gameRoot, only: "r_heroa00"))
        {
            if (f.LogLine is { } fl) log.Add(fl);
            if (f.Status != FillStatus.Present && f.Status != FillStatus.Filled) throw new InvalidOperationException((f.LogLine ?? "Kratos' script is missing")
                + ". Start the game once and load a save: GoWR MIO then saves Kratos' script, then Apply again.");
        }
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
