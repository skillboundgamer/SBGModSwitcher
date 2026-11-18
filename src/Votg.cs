using System.IO;

namespace ModSwitcher2;

/// <summary>What the check found: Found = another mod loader's backup folder is there; Clash = files it saved that our mods also change.</summary>
public sealed record VotgResult(bool Found, List<string> Clash, bool StubInPlace, VotgInfo? Info = null, bool HookInPlace = false)
{
    public const string NoClashText = "VOTG found, no clash with our mods.";

    /// <summary>The row text; null = nothing to show (no VOTG). Never claims steps: removal is VOTG's own uninstall.</summary>
    public string? Text()
    {
        if (!Found) return null;
        var shown = string.Join(", ", Clash.Select(Path.GetFileName).Take(6)) + (Clash.Count > 6 ? $" and {Clash.Count - 6} more" : "");
        if (Info != null)   // a VOTG receipt: the version-based text (design B, our hook beside VOTG)
            return Info.Text(HookInPlace) + (Clash.Count == 0 ? "" : $" VOTG also changed files our mods write: {shown}.");
        if (Clash.Count == 0) return NoClashText;
        var t = $"VOTG found. These files clash with our mods: {shown}. To remove VOTG, use VOTG's own uninstall.";
        if (StubInPlace) t += " Our Kratos script is in place now: our plug-ins run, VOTG's own Kratos script does not.";
        return t;
    }
}

/// <summary>
/// Finds the VOTG mod loader and warns about files it shares with our mods (plan 2026-11-17, batch C). READ ONLY: it never
/// moves, deletes or edits a file. VOTG's installer keeps the originals of the files it replaces in
/// <c>mod\_votg_backup\&lt;date&gt;\...</c>; a file in that list that our presets or the loader chain also write is a clash.
/// ASSUMPTION: only that backup folder tells that VOTG is installed (not the winmm.dll, MIO has no known hash).
/// ASSUMPTION: how a player uninstalls VOTG is not checked, so the text only says "use VOTG's own uninstall".
/// </summary>
public static class Votg
{
    public const string BackupDir = @"mod\_votg_backup";
    const string KratosMain = @"mod\int9\gameart\scripts\characters\heroa00\main.lua";

    static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>The game file a preset step writes (lower case; null = none). Layouts: recipe [k, recipe, dest] · restore/rm [k, dest] · put [k, src, dest] · slot [k, wad, rel, mb] · kingvo [k, rel, mode].</summary>
    public static string? StepDest(string[] s)
    {
        if (s.Length == 0) return null;
        var dest = s[0] switch
        {
            "recipe" or "reciperestart" or "put" or "putrestart" or "slot" => s.Length > 2 ? s[2] : null,
            "restore" or "restorerestart" or "rm" or "kingvo" => s.Length > 1 ? s[1] : null,
            _ => null,
        };
        return dest != null && (dest.Contains('\\') || dest.Contains('/')) ? Norm(dest) : null;
    }

    /// <summary>Every game file our mods.json presets, installed packs and the loader chain may write (lower case, backslashes).</summary>
    public static HashSet<string> OurFiles(string dataRoot)
    {
        var set = new HashSet<string>();
        foreach (var f in Loader.Files) set.Add(Norm(f.Game));
        void Take(ModCatalog cat)
        {
            foreach (var g in cat.Groups) foreach (var p in g.Presets) foreach (var s in p.Steps)
            {
                if (StepDest(s) is { } dest) set.Add(dest);
            }
        }
        try { Take(ModCatalog.Load(DataRoot.CatalogPath(dataRoot))); } catch (Exception ex) when (ex is IOException or InvalidDataException) { }
        try { foreach (var p in Packs.List()) Take(PackManifest.FromFolder(p.Root, PackManifest.AppVersion).Catalog); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or PackError or UnauthorizedAccessException) { }
        return set;
    }

    /// <summary>
    /// The files VOTG changed (lower case, relative to the game): every backup folder under <c>_votg_backup</c>
    /// (<c>&lt;date&gt;</c>, <c>gamedata\&lt;date&gt;</c>, <c>legacy\&lt;date&gt;</c>; <c>legacy2018</c> holds no game paths) plus the file list
    /// of the receipt. null = VOTG leaves no sign.
    /// </summary>
    public static HashSet<string>? BackupList(string gameRoot)
    {
        var root = Path.Combine(gameRoot, BackupDir);
        var rec = VotgReceipt.Files(gameRoot);
        if (!Directory.Exists(root) && rec == null) return null;
        var set = new HashSet<string>();
        // the receipt always lists VOTG's own Kratos script; our hook design leaves it alone, so it is no clash by itself
        if (rec != null) foreach (var f in rec) if (Norm(f) != Norm(KratosMain)) set.Add(Norm(f));
        if (!Directory.Exists(root)) return set;
        try
        {
            foreach (var top in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(top);
                if (name.Equals("legacy2018", StringComparison.OrdinalIgnoreCase)) continue;   // .bin files, not game paths
                bool group = name.Equals("gamedata", StringComparison.OrdinalIgnoreCase) || name.Equals("legacy", StringComparison.OrdinalIgnoreCase);
                foreach (var day in group ? Directory.EnumerateDirectories(top) : new[] { top })
                    foreach (var f in Directory.EnumerateFiles(day, "*", SearchOption.AllDirectories))
                        set.Add(Norm(Path.GetRelativePath(day, f)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return set;
    }

    /// <summary>B2: the game files this preset writes that VOTG also changed (sorted, lower case); empty = no clash or no VOTG. Reads files only.</summary>
    public static List<string> ClashFor(string gameRoot, ModPreset p)
    {
        var votg = BackupList(gameRoot);
        if (votg == null || votg.Count == 0) return new();
        return p.Steps.Select(StepDest).Where(d => d != null && votg.Contains(d)).Select(d => d!).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads files only. Runs off the UI thread, once per game folder.</summary>
    public static VotgResult Check(string gameRoot, string? dataRoot)
    {
        var backup = BackupList(gameRoot);
        var info = VotgReceipt.Exists(gameRoot) ? VotgReceipt.Read(gameRoot) : null;
        if (backup == null && info == null) return new(false, new(), false);
        var ours = dataRoot == null ? new HashSet<string>(Loader.Files.Select(f => Norm(f.Game))) : OurFiles(dataRoot);
        var clash = (backup ?? new()).Where(ours.Contains).OrderBy(x => x, StringComparer.Ordinal).ToList();
        bool hook = File.Exists(Path.Combine(gameRoot, Loader.Files.First(f => f.Ours == "SBG hook").Game));
        return new(true, clash, clash.Contains(Norm(KratosMain)) && StubInPlace(gameRoot, dataRoot), info, hook);
    }

    /// <summary>True when the Kratos script in the game is our stub (same bytes as the app's copy).</summary>
    static bool StubInPlace(string gameRoot, string? dataRoot)
    {
        try
        {
            var stub = Loader.Files.First(f => Norm(f.Game) == Norm(KratosMain));
            if (dataRoot == null) return false;
            var src = Path.Combine(dataRoot, stub.Src); var dst = Path.Combine(gameRoot, KratosMain);
            return File.Exists(src) && File.Exists(dst) && File.ReadAllBytes(src).AsSpan().SequenceEqual(File.ReadAllBytes(dst));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Repair on every Switcher start (plan 5.3), off the UI thread. Only when this app installed its chain here and VOTG is
    /// there: VOTG now owns core.color = say so and touch nothing; our hook missing = put the chain back (Loader.Install:
    /// dump fill, record first). Never writes a VOTG file; never writes while the game runs. Returns log lines (empty = nothing to do).
    /// </summary>
    public static List<string> Repair(string gameRoot, string? dataRoot)
    {
        var log = new List<string>();
        if (!Loader.VotgActive(gameRoot) || dataRoot == null || Loader.Mine(gameRoot).Count == 0 || Loader.GameRunning()) return log;
        var info = VotgReceipt.Read(gameRoot);
        if (info is { OwnsColor: true }) { log.Add("VOTG now ships the file our hook uses: our Kratos mods stay off. " + info.Text(false)); return log; }
        var hook = Path.Combine(gameRoot, Loader.Files.First(f => f.Ours == "SBG hook").Game);
        if (File.Exists(hook) && Loader.Present(gameRoot)) return log;
        try { log.AddRange(Loader.Install(gameRoot, dataRoot)); log.Insert(0, "Repair: our Kratos hook was missing and was put back."); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { log.Add("Repair: not done: " + ex.Message); }
        return log;
    }

    /// <summary>--votg --game D [--data D] [--appdata D] : "found / clash / stub / text" lines (tests).</summary>
    public static int Cli(string game, string? data, TextWriter o)
    {
        var r = Check(game, data);
        o.WriteLine("found\t" + (r.Found ? "yes" : "no"));
        o.WriteLine("clash\t" + string.Join(";", r.Clash));
        o.WriteLine("stub\t" + (r.StubInPlace ? "yes" : "no"));
        o.WriteLine("receipt\t" + (r.Info == null ? "no" : "yes"));
        o.WriteLine("tested\t" + (r.Info == null ? "-" : r.Info.Tested ? "yes" : "no"));
        o.WriteLine("hook\t" + (r.HookInPlace ? "yes" : "no"));
        o.WriteLine("text\t" + (r.Text() ?? "-"));
        return 0;
    }
}
