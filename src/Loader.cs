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
/// tail mod\sbg_probe.lua (it loads mod\live.lua every ~2 s) and a small public mod\live.lua that loads
/// mod\sbg_loadout.lua and (v2, app 2.0.3) mod\sbg_twogna.lua; a missing one is skipped. Installed by the loadout card
/// and by any preset with "loader": true (ModPreset.UsesLoader, Apply.cs). No game code ships: the game's script comes
/// from the player's own dump.
/// Rules: the player's MIO must dump and replace Lua and the dump must be Lua 5.2 bytecode (Problem), else nothing is
/// installed; a missing file is installed; a file of ours already there (the developer's own probe or live.lua) is left
/// alone; any other file is backed up first. Remove takes out only what this app wrote and puts the backups back.
/// </summary>
public static partial class Loader
{
    public const string Ini = @"mod\GOWR_MIO.ini";
    public const string Dump = @"mod\dump\int9\gameart\scripts\characters\heroa00\main.lua";
    public const string VotgReceiptRel = @"mod\_votg_receipt.json";
    public const string ColorDump = @"mod\dump\int9\gameart\scripts\libraries\core\color.lua";
    public const string KratosMain = @"mod\int9\gameart\scripts\characters\heroa00\main.lua";
    static readonly LoaderFile Stub = new(@"versions\probe_stub_v20.lua", KratosMain, "SBG probe");
    /// <summary>The core.color hook (plan votg_switcher_only_plan.md, design B): a library VOTG does not ship, so the probe
    /// starts beside VOTG's own Kratos script without any change to a VOTG file.</summary>
    static readonly LoaderFile Hook = new(@"versions\sbg_hook_color_v1.lua", @"mod\int9\gameart\scripts\libraries\core\color.lua", "SBG hook");
    static readonly LoaderFile Tail = new(@"tools\probe_tail_v115.lua", @"mod\sbg_probe.lua", "SBG probe");
    static readonly LoaderFile Live = new(@"versions\live_public.lua", @"mod\live.lua", "SBG");
    /// <summary>Every file the chain may write (any mode): lists for clash checks and packs.</summary>
    public static readonly LoaderFile[] Files = { Stub, Hook, Tail, Live };

    /// <summary>True when the VOTG launcher's receipt is in the game (read only; VOTG owns Kratos' main.lua then).</summary>
    public static bool VotgActive(string gameRoot) => File.Exists(Path.Combine(gameRoot, VotgReceiptRel));
    static string NoStubMarker => Path.Combine(Dir, "nostub");
    /// <summary>True when the old Kratos stub is part of the chain: MIO without VOTG, and the player did not turn it off (test T1).</summary>
    public static bool StubWanted(string gameRoot) => !VotgActive(gameRoot) && !File.Exists(NoStubMarker);
    public static void SetStub(bool on)
    {
        Directory.CreateDirectory(Dir);
        if (on) { if (File.Exists(NoStubMarker)) File.Delete(NoStubMarker); } else File.WriteAllText(NoStubMarker, "1");
    }
    /// <summary>The files this chain writes in this game folder (the hook is also skipped when core.color has no dump, see Install).</summary>
    public static LoaderFile[] FilesFor(string gameRoot) =>
        StubWanted(gameRoot) ? Files : Files.Where(f => f != Stub).ToArray();
    /// <summary>The game is running: no file of the chain is written.</summary>
    public static bool GameRunning() { try { return System.Diagnostics.Process.GetProcessesByName("GoWR").Length > 0; } catch (InvalidOperationException) { return false; } }
    static readonly byte[] LuaR = { 0x1b, (byte)'L', (byte)'u', (byte)'a', (byte)'R' };

    static string Dir => Path.Combine(AppSettings.Dir, "loader");
    static string StatePath => Path.Combine(Dir, "loader.json");

    /// <summary>The bytes of one loader source: the data root's file when it is there, else the copy built into this exe
    /// (EmbeddedResource in ModSwitcher2.csproj, name "loader/&lt;file name&gt;"). A player's folder has no dev tree, so
    /// without this copy the chain failed with "missing versions\...". Null = neither has it.</summary>
    public static byte[]? SourceBytes(string dataRoot, string src)
    {
        var p = Path.Combine(dataRoot, src);
        if (File.Exists(p)) return File.ReadAllBytes(p);
        using var s = typeof(Loader).Assembly.GetManifestResourceStream("loader/" + Path.GetFileName(src));
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Why the loader cannot work on this game, in plain words; null = it can.</summary>
    public static string? Problem(string gameRoot)
    {
        if (VotgActive(gameRoot))   // VOTG's loader runs our files: read only, nothing of VOTG is changed
        {
            if (VotgReceipt.Read(gameRoot) is { OwnsColor: true } owner) return owner.Text(false);
            return File.Exists(Path.Combine(gameRoot, "winmm.dll")) ? ColorProblem(gameRoot) : @"VOTG is installed but its loader (winmm.dll) is missing.";
        }
        var ini = Path.Combine(gameRoot, Ini);
        if (!File.Exists(ini)) return @"GoWR MIO is not installed (no mod\GOWR_MIO.ini).";
        var text = File.ReadAllText(ini);
        if (!IniOn(text, "dump", "lua")) return @"In mod\GOWR_MIO.ini set lua=1 under [dump], then start the game once.";
        if (!IniOn(text, "replace", "lua")) return @"In mod\GOWR_MIO.ini set lua=1 under [replace].";
        return StubWanted(gameRoot) ? DumpProblem(gameRoot) : ColorProblem(gameRoot);
    }

    /// <summary>Why the game's own core.color (the hook runs it first) cannot be used; null = there (or Install takes it from r_perm.wad).</summary>
    public static string? ColorProblem(string gameRoot)
    {
        if (File.Exists(Path.Combine(gameRoot, ColorDump))) return null;
        return File.Exists(Path.Combine(gameRoot, "exec", "wad", "pc_le", "r_perm.wad")) ? null
            : "The game's core.color is missing (no dump, no r_perm.wad): start the game once with GoWR MIO, then Apply again.";
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
    public static bool Present(string gameRoot)
    {
        bool Has(LoaderFile f) => File.Exists(Path.Combine(gameRoot, f.Game));
        // Kratos' script starts the probe by the stub (MIO) or by the hook (VOTG, or MIO with the stub off): one of them is enough.
        // With VOTG the Kratos main.lua is VOTG's own and never starts our probe: only our hook counts (2.0.5 test, 2026-11-21:
        // the card said "In place" with the hook missing, and Turn loader on was hidden).
        return Has(Tail) && Has(Live) && (Has(Hook) || (!VotgActive(gameRoot) && Has(Stub)));
    }

    /// <summary>True when every chain file is in the game with our exact bytes and no old stub of ours waits to be taken out: Install would write nothing.</summary>
    static bool ChainInPlace(string gameRoot, string dataRoot)
    {
        try
        {
            var root = RootKey(gameRoot);
            if (!StubWanted(gameRoot) && Records().Any(x => x.Root == root && x.Game.Equals(KratosMain, StringComparison.OrdinalIgnoreCase))) return false;
            var hookOk = File.Exists(Path.Combine(gameRoot, ColorDump));   // no core.color dump = no hook (as in Install)
            if (!StubWanted(gameRoot) && !hookOk) return false;                // no starter at all: Install would refuse
            var recs = Records();
            foreach (var f in FilesFor(gameRoot).Where(f => hookOk || f != Hook))
            {
                var src = Path.Combine(dataRoot, f.Src); var dst = Path.Combine(gameRoot, f.Game);
                if (!File.Exists(src) || !File.Exists(dst)) return false;
                var have = File.ReadAllBytes(dst);
                if (File.ReadAllBytes(src).AsSpan().SequenceEqual(have)) continue;
                // a developer's own copy (not our earlier version) is kept by Install: nothing to write either
                bool appWrote = recs.Any(r => r.Root == root && r.Game.Equals(f.Game, StringComparison.OrdinalIgnoreCase) && r.Matches(Sha(have)));
                if (appWrote || !Encoding.UTF8.GetString(have).Contains(f.Ours, StringComparison.Ordinal)) return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Puts the missing loader files in (see the class rules). Returns log lines; throws with the plain reason when it cannot.</summary>
    public static List<string> Install(string gameRoot, string dataRoot)
    {
        var log = new List<string>();
        // a running game blocks only a real change: with the whole chain already in, the loadout can still be written live (review 2026-11-19)
        if (GameRunning())
        {
            if (ChainInPlace(gameRoot, dataRoot)) return log;
            throw new InvalidOperationException("The game is running: close it, then Apply again (nothing was changed).");
        }
        if (Problem(gameRoot) is { } why) throw new InvalidOperationException(why);
        bool stub = StubWanted(gameRoot);
        // a first install beside a VOTG version we did not test asks once (the first Apply says so and stops, the next goes on)
        if (!stub && VotgActive(gameRoot) && VotgReceipt.Read(gameRoot) is { Tested: false } unk && !File.Exists(Path.Combine(gameRoot, Hook.Game))
            && !VotgReceipt.AskedAlready(unk.Payload + "|" + unk.LoaderSha + "|" + unk.ModVersion))
            throw new InvalidOperationException($"VOTG {unk.ModVersion} is not tested with this Switcher. Our mods would use only their own file and change no VOTG file. Press Apply again to install anyway (nothing was changed).");
        // Kratos' dump from the player's own wad when MIO has not saved it yet (slow: callers run off the UI thread)
        if (stub)
            foreach (var f in DumpFill.FillMissing(gameRoot, only: "r_heroa00"))
            {
                if (f.LogLine is { } fl) log.Add(fl);
                if (f.Status != FillStatus.Present && f.Status != FillStatus.Filled) throw new InvalidOperationException((f.LogLine ?? "Kratos' script is missing")
                    + ". Start the game once and load a save: GoWR MIO then saves Kratos' script, then Apply again.");
            }
        // the hook needs the game's own core.color: no dump and no matching r_perm.wad = no hook (nothing breaks)
        bool hookOk = true;
        foreach (var f in DumpFill.FillMissing(gameRoot, only: "r_perm"))
        {
            if (f.LogLine is { } fl) log.Add(fl);
            if (f.Status != FillStatus.Present && f.Status != FillStatus.Filled && f.Status != FillStatus.NoWad) hookOk = false;
        }
        if (!File.Exists(Path.Combine(gameRoot, ColorDump))) hookOk = false;
        if (!hookOk && !stub) log.Add("core.color hook: not installed (the game's core.color could not be taken)");
        if (!hookOk && !stub) throw new InvalidOperationException("The game's core.color could not be taken, and the old Kratos script is off: nothing to install.");
        if (!stub) RetireStub(gameRoot, log);
        var recs = Records();
        var root = RootKey(gameRoot);
        var chain = FilesFor(gameRoot).Where(f => hookOk || f != Hook).ToArray();
        foreach (var f in chain)   // every source first: a missing one must not leave half a chain in the game
            if (SourceBytes(dataRoot, f.Src) == null) throw new FileNotFoundException("missing " + f.Src);
        foreach (var f in chain)
        {
            var want = SourceBytes(dataRoot, f.Src)!;
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

    /// <summary>True when a saved earlier file may be put back. A saved Kratos script that is VOTG's own (it holds VOTG's own function
    /// names) is put back only while VOTG is installed: alone it fails at line 1244 (no VOTG config) and the game closes (T1 run 1, 2026-11-18).</summary>
    static bool CanRestore(string gameRoot, LoaderRecord r, string bak, List<string> log)
    {
        if (!r.Game.Equals(KratosMain, StringComparison.OrdinalIgnoreCase) || VotgActive(gameRoot)) return true;
        if (File.ReadAllBytes(bak).AsSpan().IndexOf("VOTG_"u8) < 0) return true;   // not VOTG's: restore as before
        log.Add($"{r.Game}: the saved earlier file belongs to VOTG, which is not installed: not put back (kept in {bak})");
        return false;
    }

    /// <summary>The stub is off (VOTG, or T1): takes our old Kratos stub out when it is still ours, and puts the file back that was there.</summary>
    static void RetireStub(string gameRoot, List<string> log)
    {
        var recs = Records(); var root = RootKey(gameRoot);
        var r = recs.FirstOrDefault(x => x.Root == root && x.Game.Equals(KratosMain, StringComparison.OrdinalIgnoreCase));
        if (r == null) return;
        var dest = Path.Combine(gameRoot, r.Game);
        if (File.Exists(dest) && !r.Matches(Sha(File.ReadAllBytes(dest)))) { log.Add($"{r.Game}: not ours now (VOTG or another mod), left as it is"); recs.Remove(r); SaveRecords(recs); return; }
        if (File.Exists(dest)) File.Delete(dest);
        var bak = r.Backup == null ? null : Path.Combine(Dir, r.Backup);
        bool back = bak != null && File.Exists(bak) && CanRestore(gameRoot, r, bak, log);
        if (back) File.Copy(bak!, dest, overwrite: true);
        log.Add($"{r.Game}: our old Kratos script taken out" + (back ? ", the earlier file is back" : ""));
        recs.Remove(r); SaveRecords(recs);
    }

    /// <summary>Takes out the files this app wrote (only when they are still unchanged) and puts the backups back.</summary>
    public static List<string> Remove(string gameRoot)
    {
        if (GameRunning()) throw new InvalidOperationException("The game is running: close it, then try again (nothing was changed).");
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
                if (bak != null && File.Exists(bak) && CanRestore(gameRoot, r, bak, log)) { File.Copy(bak, dest, overwrite: true); log.Add($"{r.Game}: your earlier file is back"); }
                else log.Add($"{r.Game}: removed");
            }
            recs.Remove(r);
            SaveRecords(recs);
        }
        return log;
    }
}
