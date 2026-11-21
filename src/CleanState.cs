using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ModSwitcher2;

/// <summary>One file the clean-state check found. Path = game-relative, lower case. Fix = restore | remove | update | none.</summary>
public sealed record CleanItem(string Path, string Why, string Fix)
{
    /// <summary>chain (a loader file of ours) | exec (an exec\ file we changed) | foreign (exec\ file, not ours) | votg | leftovers.</summary>
    internal string Kind { get; init; } = "";
    /// <summary>restore: the full path of the copy that goes back.</summary>
    internal string? Source { get; init; }
    /// <summary>chain: our install record that matches the file now (its saved earlier file goes back on remove).</summary>
    internal LoaderRecord? Record { get; init; }
}

/// <summary>VOTG facts of the game folder. Winmm = mio | votg | none | unknown.</summary>
public sealed record CleanVotg(bool Receipt, bool Leftovers, string Winmm, [property: JsonPropertyName("winmm_size")] long WinmmSize);

/// <summary>What the clean-state check found (--clean-check prints it as JSON).</summary>
public sealed record CleanReport(string Version, List<CleanItem> Stale, List<CleanItem> Foreign, CleanVotg Votg, List<string> Hints)
{
    /// <summary>Items Repair can act on. An info-only item (Fix none, e.g. VOTG's leftover backups) is not a problem to repair.</summary>
    [JsonIgnore] public int Actionable => Stale.Count + Foreign.Count(i => i.Fix != "none");
    [JsonIgnore] public bool IsClean => Actionable == 0;
}

/// <summary>
/// The clean-state check and repair (plan research/switcher2/clean_state_plan.md, 2.0.6). Check reads files only and runs off
/// the UI thread. It finds:
///  - stale: Switcher files that are old or left over (a shipped loader file with no install record of ours, an older
///    version of one, our Kratos starter with the rest of the chain gone, our old stub while the stub is off), and exec\
///    files we changed whose bytes are now neither a preset of ours nor our backup;
///  - foreign: exec\ files we did not write (differ from our backup, no preset of ours writes them), files VOTG changed
///    (its receipt is there and its backup copy differs), and VOTG's backup folder left without its receipt.
/// Repair puts back only from hashed copies, never deletes (removed files go to the recycle folder, as Apply does), never
/// writes while the game runs, writes only under the game's mod\ and exec\, reads every write back. VOTG files only with
/// yesVotg, foreign files only with yesForeign. winmm.dll is never written (MIO's over VOTG: a hint only).
/// Files changed in place by a memory slot or King voice step (slot / kingvo) are not judged: one such file can carry the
/// changes of several presets at once, so its bytes match no single preset.
/// </summary>
public static partial class CleanState
{
    /// <summary>Known winmm.dll builds. mio: GoWR MIO's, hashed 2026-11-21 from a game folder with MIO only (the same size
    /// came back after a VOTG uninstall test). votg: the VOTG loader of VOTG 0.15.546 / 0.15.555 (sha and size from its
    /// payload manifest). A file of a known size but another hash still counts as that kind.</summary>
    static readonly (string Kind, long Size, string Sha)[] KnownWinmm =
    {
        ("mio", 480768, "bc721ba5b82d27b4421cb9f7129c39ad98c9e3fec7ba2553bd60bb138b0d8fd4"),
        ("votg", 1832960, "0b3b7acfc65a88f349736c311294e9c543ddfad505fab001da3938b36b737fc3"),
    };

    const string SizeTable = @"exec\dc\pc_le\gbl_wadsizetable.dcb";   // a "slot" step also writes it (Apply.cs SlotSet)

    static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>The loader sources: the data root, else the copies built into the exe (Loader.SourceBytes).</summary>
    static string SourceRoot(string? data) => data ?? Path.Combine(AppSettings.Dir, "no-data");

    static string AppVersion()
    {
        var v = typeof(CleanState).Assembly.GetName().Version;
        return v == null ? "2" : v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---------- the check (read only) ----------

    /// <summary>Reads files only. Call it off the UI thread (big wads are hashed when their size matches).</summary>
    public static CleanReport Check(string game, string? data)
    {
        if (!Directory.Exists(game)) throw new DirectoryNotFoundException("game folder not found: " + game);
        var stale = new List<CleanItem>();
        var foreign = new List<CleanItem>();
        var hints = new List<string>();
        bool receipt = VotgReceipt.Exists(game), leftovers = Votg.Leftovers(game);
        var (winmm, size) = Winmm(game);

        void Safe(string what, Action a)
        {
            try { a(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            { hints.Add($"Could not read {what}: {ex.Message}"); }
        }
        Safe("the loader files", () => CheckChain(game, data, stale));
        var steps = new Steps(game);
        Safe("the mod lists", () => steps.Load(data));
        var done = new HashSet<string>();
        if (receipt) Safe("VOTG's backups", () => CheckVotg(game, data, steps, foreign, done));
        Safe("the backups of game files", () => CheckExec(game, data, steps, stale, foreign, done));
        if (leftovers)
            foreign.Add(new(Norm(Votg.BackupDir), "VOTG's backup folder is still here but VOTG is not installed (no receipt). Repair never removes VOTG's backups.", "none") { Kind = "leftovers" });

        // hints: plain words, nothing to repair by itself
        if (receipt && winmm == "mio")
            hints.Add("VOTG is installed, but winmm.dll is GoWR MIO's (VOTG's own loader is not in place). Reinstall or uninstall VOTG with VOTG's launcher. The Switcher never changes winmm.dll.");
        if (receipt && winmm == "none")
            hints.Add("VOTG is installed, but winmm.dll is missing. Reinstall VOTG with VOTG's launcher.");
        Safe("the MIO dump folder", () =>
        {
            var dump = Path.Combine(game, "mod", "dump");
            if (Directory.Exists(dump) && !Directory.EnumerateFiles(dump, "*", SearchOption.AllDirectories).Any())
                hints.Add("GoWR MIO's dump folder is empty: start the game once and load a save, so MIO saves the game's scripts.");
        });
        if (Loader.GameRunning() || GameLocator.IsGameRunningIn(game)) hints.Add("The game is running: close it before Repair.");

        stale.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        foreign.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new(AppVersion(), stale, foreign, new(receipt, leftovers, winmm, size), hints);
    }

    /// <summary>winmm.dll: a known hash, then the hash VOTG's receipt names, then a known size; else unknown. Missing = none.</summary>
    public static (string Kind, long Size) Winmm(string game)
    {
        var fi = new FileInfo(Path.Combine(game, "winmm.dll"));
        if (!fi.Exists) return ("none", 0);
        string? sha = null;
        try { sha = FileOwners.Sha(fi.FullName); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var k in KnownWinmm) if (sha == k.Sha) return (k.Kind, fi.Length);
        if (sha != null && VotgReceipt.Read(game)?.LoaderSha is { Length: >= 8 } rs && sha.StartsWith(rs.ToLowerInvariant(), StringComparison.Ordinal))
            return ("votg", fi.Length);
        foreach (var k in KnownWinmm) if (fi.Length == k.Size) return (k.Kind, fi.Length);
        return ("unknown", fi.Length);
    }

    /// <summary>The loader chain: only files that are ours (a shipped version, or our install record matches them). Another
    /// mod's file, VOTG's, or a developer's own copy (unknown bytes) is left alone, the same way Loader.Install keeps it.</summary>
    static void CheckChain(string game, string? data, List<CleanItem> stale)
    {
        var root = Loader.RootKey(game);
        var recs = Loader.Records().Where(r => r.Root == root).ToList();
        var tail = Loader.Files.First(f => f.Game == @"mod\sbg_probe.lua");
        bool tailThere = File.Exists(Path.Combine(game, tail.Game));
        foreach (var f in Loader.Files)
        {
            var path = Path.Combine(game, f.Game);
            if (!File.Exists(path)) continue;
            var have = File.ReadAllBytes(path);
            var sha = Loader.Sha(have);
            var rec = recs.FirstOrDefault(r => r.Game.Equals(f.Game, StringComparison.OrdinalIgnoreCase) && r.Matches(sha));
            if (rec == null && !Loader.Shipped(f, have)) continue;
            var want = Loader.SourceBytes(SourceRoot(data), f.Src);
            bool current = want != null && Loader.LfSha(want) == Loader.LfSha(have);
            bool starter = f.Game == Loader.KratosMain || f.Ours == "SBG hook";
            var rel = Norm(f.Game);
            CleanItem Item(string why, string fix) => new(rel, why, fix) { Kind = "chain", Record = rec };
            if (starter && !tailThere)
                stale.Add(Item("Our loader starter is here, but the rest of the loader is gone (left over).", "remove"));
            else if (f.Game == Loader.KratosMain && !Loader.StubWanted(game))
                stale.Add(Item("Our old Kratos script is not used any more (our hook starts the mods now).", "remove"));
            else if (rec == null)
                stale.Add(Item(current ? "Left by an older Switcher (no install record of ours)." : "An older version, left by an older Switcher (no install record of ours).", "remove"));
            else if (!current)
                stale.Add(Item("An older version of our loader file.", "update"));
        }
    }

    /// <summary>The steps of every preset of ours (the data folder's mods.json and the installed packs), per game file.</summary>
    sealed class Steps
    {
        readonly string _game;
        readonly Dictionary<string, ModMatcher> _matchers = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<(string Root, string[] Step)>> ByFile = new();
        /// <summary>Files a slot / kingvo step changes in place: not judged by their bytes.</summary>
        public readonly HashSet<string> InPlace = new();

        public Steps(string game) { _game = game; }

        public void Load(string? data)
        {
            if (data != null && File.Exists(DataRoot.CatalogPath(data))) Take(ModCatalog.Load(DataRoot.CatalogPath(data)), data);
            foreach (var (root, _) in FileOwners.PacksReadOnly())   // read only: Packs.List may write
                try { Take(PackManifest.FromFolder(root, PackManifest.AppVersion).Catalog, root); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or PackError or UnauthorizedAccessException) { }
        }

        void Take(ModCatalog cat, string root)
        {
            foreach (var g in cat.Groups) foreach (var p in g.Presets) foreach (var s in p.Steps)
            {
                if (Votg.StepDest(s) is not { } dest) continue;
                if (s[0] is "slot" or "kingvo") { InPlace.Add(dest); if (s[0] == "slot") InPlace.Add(SizeTable); continue; }
                if (!ByFile.TryGetValue(dest, out var list)) ByFile[dest] = list = new();
                list.Add((root, s));
            }
        }

        public bool Owned(string rel) => ByFile.ContainsKey(rel) || InPlace.Contains(rel);

        /// <summary>True when the game file is as one step of ours makes it (put / recipe / rm). A "restore" step is not
        /// asked: it means "same as our backup", which the caller compares itself.</summary>
        public bool MatchesOurs(string rel)
        {
            if (!ByFile.TryGetValue(rel, out var list)) return false;
            foreach (var (root, s) in list)
            {
                try
                {
                    if (!_matchers.TryGetValue(root, out var m)) _matchers[root] = m = new ModMatcher(_game, root);
                    bool ok = s[0] switch
                    {
                        "put" or "putrestart" => m.SameFile(Path.Combine(root, s[1]), Path.Combine(_game, s[2])),
                        "recipe" or "reciperestart" => m.RecipeMatches(s[1], s[2]),
                        "rm" => !File.Exists(Path.Combine(_game, s[1])),
                        _ => false,
                    };
                    if (ok) return true;
                }
                catch (Exception) { /* a missing source or recipe: this step does not match */ }
            }
            return false;
        }
    }

    /// <summary>Same bytes: size first, the hash only when the sizes match.</summary>
    static bool Same(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        return fa.Exists && fb.Exists && fa.Length == fb.Length && FileOwners.Sha(a) == FileOwners.Sha(b);
    }

    /// <summary>Our backup folders of game files: the data folder's, and the app-level one (releases and packs).</summary>
    static List<string> BackupDirs(string? data)
    {
        var dirs = new List<string>();
        if (data != null) dirs.Add(Path.GetFullPath(DataRoot.BackupDir(data)));
        var app = Path.GetFullPath(Path.Combine(AppSettings.Dir, "originals", "game_files"));
        if (!dirs.Contains(app, StringComparer.OrdinalIgnoreCase)) dirs.Add(app);
        return dirs.Where(Directory.Exists).ToList();
    }

    static string? OurBackup(string? data, string rel) =>
        BackupDirs(data).Select(d => Path.Combine(d, rel)).FirstOrDefault(File.Exists);

    /// <summary>exec\ files VOTG saved before it changed them (receipt there): the earliest copy of each. Folders VOTG's
    /// uninstall made from its own changed files ("...-removed") and legacy2018 (no game paths) are skipped.</summary>
    static Dictionary<string, string> VotgCopies(string game)
    {
        var copies = new Dictionary<string, string>();
        var root = Path.Combine(game, Votg.BackupDir);
        if (!Directory.Exists(root)) return copies;
        var days = new List<string>();
        foreach (var top in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(top);
            if (name.Equals("legacy2018", StringComparison.OrdinalIgnoreCase)) continue;
            bool group = name.Equals("gamedata", StringComparison.OrdinalIgnoreCase) || name.Equals("legacy", StringComparison.OrdinalIgnoreCase);
            days.AddRange(group ? Directory.EnumerateDirectories(top) : new[] { top });
        }
        foreach (var day in days.Where(d => !d.EndsWith("-removed", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal))
            foreach (var f in Directory.EnumerateFiles(day, "*", SearchOption.AllDirectories))
            {
                var rel = Norm(Path.GetRelativePath(day, f));
                if (rel.StartsWith(@"exec\", StringComparison.Ordinal)) copies.TryAdd(rel, f);   // the earliest stamp wins
            }
        return copies;
    }

    /// <summary>VOTG installed: an exec\ file whose bytes differ from VOTG's own backup copy and are not ours = VOTG changed it.</summary>
    static void CheckVotg(string game, string? data, Steps steps, List<CleanItem> foreign, HashSet<string> done)
    {
        foreach (var (rel, copy) in VotgCopies(game))
        {
            var g = Path.Combine(game, rel);
            if (steps.InPlace.Contains(rel) || !File.Exists(g) || Same(g, copy)) continue;
            var ours = OurBackup(data, rel);
            if (ours != null && Same(g, ours)) continue;
            if (steps.MatchesOurs(rel)) continue;
            done.Add(rel);
            foreign.Add(new(rel, "VOTG changed this file (its backup has the earlier copy).", "restore") { Kind = "votg", Source = ours ?? copy });
        }
    }

    /// <summary>exec\ files we have a backup of: same as the backup = fine; as a preset of ours makes it = fine; else stale
    /// (a preset of ours writes it) or foreign (none does).</summary>
    static void CheckExec(string game, string? data, Steps steps, List<CleanItem> stale, List<CleanItem> foreign, HashSet<string> done)
    {
        foreach (var dir in BackupDirs(data))
            foreach (var b in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Norm(Path.GetRelativePath(dir, b));
                if (!rel.StartsWith(@"exec\", StringComparison.Ordinal) || !done.Add(rel) || steps.InPlace.Contains(rel)) continue;
                var g = Path.Combine(game, rel);
                if (Same(g, b) || steps.MatchesOurs(rel)) continue;
                bool there = File.Exists(g);
                if (steps.Owned(rel))
                    stale.Add(new(rel, there ? "Changed: the file is neither one of our mods nor our backup of the original." : "Missing: our backup has the original.", "restore") { Kind = "exec", Source = b });
                else if (there)   // a missing file no mod of ours writes is never created: the player or a tool removed it, or the backup entry is junk
                    foreign.Add(new(rel, "Changed by another mod or tool (no mod of ours writes it); our backup has the original.", "restore") { Kind = "foreign", Source = b });
            }
    }

    // ---------- headless modes (tests) ----------

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>--clean-check --game G [--data D] [--appdata A]: the report as one JSON object.</summary>
    public static int CliCheck(string game, string? data, TextWriter o)
    {
        o.WriteLine(JsonSerializer.Serialize(Check(game, data), Json));
        return 0;
    }

    /// <summary>--repair --game G [--data D] [--appdata A] [--yes-votg] [--yes-foreign]: one line per file; exit 3 = game running.</summary>
    public static int CliRepair(string game, string? data, bool yesVotg, bool yesForeign, TextWriter o)
    {
        var lines = Repair(game, data, yesVotg, yesForeign);
        foreach (var l in lines) o.WriteLine(l);
        return lines.Count == 1 && lines[0] == RefusedLine ? 3 : 0;
    }
}
