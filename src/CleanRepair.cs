using System.IO;

namespace ModSwitcher2;

/// <summary>The repair half of the clean-state check (moved out of CleanState.cs, 2.0.7).</summary>
public static partial class CleanState
{
    // ---------- the repair ----------

    public const string RefusedLine = "refused\tclose the game first";

    /// <summary>
    /// Repairs what Check finds. Returns one line per file: "restored\tpath", "removed\tpath" (to the recycle folder),
    /// "skipped\tpath\twhy". A running game = writes nothing, one line <see cref="RefusedLine"/>. Off the UI thread.
    /// gameRunning: tests may pass their own; default = any GoWR process.
    /// </summary>
    public static List<string> Repair(string game, string? data, bool yesVotg, bool yesForeign, Func<bool>? gameRunning = null, ISet<string>? asked = null)
    {
        var running = gameRunning ?? (() => Loader.GameRunning() || GameLocator.IsGameRunningIn(game));
        if (running()) return new() { RefusedLine };
        var r = Check(game, data);
        var log = new List<string>();
        var recycle = ModApplier.DefaultRecycle;
        var updates = new List<CleanItem>();

        void Do(CleanItem it, Func<string> write)
        {
            if (running()) { log.Add($"skipped\t{it.Path}\tthe game was started: close it first"); return; }
            try { log.Add(write()); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            { log.Add($"skipped\t{it.Path}\t{ex.Message}"); }
        }

        foreach (var it in r.Stale)
        {
            if (it.Fix == "update") { updates.Add(it); continue; }
            Do(it, () => it.Fix == "remove" ? TakeOut(game, it, recycle) : PutBack(game, it, recycle));
        }
        if (updates.Count > 0)
        {
            Update(game, data, updates, log, running);
            // the loader install writes the whole chain again: a file logged as removed may be back (its hook)
            for (int i = 0; i < log.Count; i++)
                if (log[i].StartsWith("removed\t") && File.Exists(Wall(game, log[i][8..]))) log[i] = "restored\t" + log[i][8..];
        }

        foreach (var it in r.Foreign)
        {
            if (it.Fix == "none") continue;   // info only (VOTG's leftover backups): never touched, not a repair line
            if (it.Fix != "restore") { log.Add($"skipped\t{it.Path}\tnothing to repair"); continue; }
            bool votg = it.Kind == "votg";
            // a file found only now (not in the list the player saw) is never covered by that Yes
            if (!(votg ? yesVotg : yesForeign) || (asked != null && !asked.Contains(it.Path)))
            {
                log.Add($"skipped\t{it.Path}\t" + (votg ? "a VOTG file: restored only after a Yes" : "not changed by our mods: restored only after a Yes"));
                continue;
            }
            Do(it, () => PutBack(game, it, recycle));
        }
        if (r.Votg.Receipt && r.Votg.Winmm == "mio")
            log.Add("skipped\twinmm.dll\tGoWR MIO's winmm.dll over VOTG: never swapped (use VOTG's launcher)");
        return log;
    }

    /// <summary>Older versions of our loader files with our record: the normal loader install updates them (records, backups).</summary>
    static void Update(string game, string? data, List<CleanItem> items, List<string> log, Func<bool> running)
    {
        if (running()) { foreach (var it in items) log.Add($"skipped\t{it.Path}\tthe game was started: close it first"); return; }
        try { Loader.Install(game, SourceRoot(data)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            foreach (var it in items) log.Add($"skipped\t{it.Path}\t{ex.Message}");
            return;
        }
        foreach (var it in items)
        {
            var f = Loader.Files.First(x => Norm(x.Game) == it.Path);
            var path = Wall(game, it.Path);
            var want = Loader.SourceBytes(SourceRoot(data), f.Src);
            bool now = want != null && File.Exists(path) && Loader.LfSha(File.ReadAllBytes(path)) == Loader.LfSha(want);
            log.Add(now ? $"restored\t{it.Path}" : $"skipped\t{it.Path}\tthe loader install did not update it");
        }
    }

    /// <summary>The wall (as ModApplier.Game): a path outside the game's mod\ and exec\ is refused before anything is written.</summary>
    static string Wall(string game, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(game, rel));
        bool Under(string dir) => full.StartsWith(Path.GetFullPath(Path.Combine(game, dir)).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (!Under("mod") && !Under("exec")) throw new InvalidOperationException($"refused: {rel} is outside the game's mod\\ and exec\\ folders (nothing was changed)");
        return full;
    }

    static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");

    /// <summary>A loader file of ours to the recycle folder (checked there by hash); with our record, the file that was
    /// there before ours goes back (Loader.CanRestore: never VOTG's Kratos script without VOTG), and the record is dropped.</summary>
    static string TakeOut(string game, CleanItem it, string recycle)
    {
        var full = Wall(game, it.Path);
        var sha = FileOwners.Sha(full);
        Directory.CreateDirectory(recycle);
        var kept = Path.Combine(recycle, $"{Path.GetFileName(full)}.{Stamp()}");
        File.Move(full, kept);
        if (File.Exists(full) || FileOwners.Sha(kept) != sha) throw new IOException(it.Path + ": move to the recycle folder failed to verify");
        if (it.Record is { } rec)
        {
            var bak = Loader.RecordBackupPath(rec);
            var notes = new List<string>();
            bool back = bak != null && File.Exists(bak) && Loader.CanRestore(game, rec, bak, notes);
            if (back)
            {
                try
                {
                    File.Copy(bak!, full);
                    if (FileOwners.Sha(full) != FileOwners.Sha(bak!)) throw new IOException("write verify failed");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // our file is already in the recycle folder; say so, the saved earlier file is still in our backup
                    throw new IOException($"our file is in the recycle folder ({kept}), the saved earlier file could not be put back: {ex.Message}");
                }
            }
            Loader.ForgetRecord(game, rec.Game);
            if (back) return $"restored\t{it.Path}";
        }
        return $"removed\t{it.Path}";
    }

    /// <summary>A hashed copy back into the game (our backup, or VOTG's for a VOTG file). The file there first goes to the
    /// recycle folder; the copy is read back and compared.</summary>
    static string PutBack(string game, CleanItem it, string recycle)
    {
        var full = Wall(game, it.Path);
        var src = it.Source ?? throw new InvalidOperationException("no copy to put back");
        if (!File.Exists(src)) throw new FileNotFoundException("the saved copy is gone: " + src);
        var want = FileOwners.Sha(src);
        if (File.Exists(full))
        {
            Directory.CreateDirectory(recycle);
            var kept = Path.Combine(recycle, $"{Path.GetFileName(full)}.{Stamp()}.replaced");
            var had = FileOwners.Sha(full);
            File.Copy(full, kept);
            if (FileOwners.Sha(kept) != had) throw new IOException(it.Path + ": the copy in the recycle folder failed to verify (nothing was changed)");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".sbgtmp";
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, full, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
        if (FileOwners.Sha(full) != want) throw new IOException(it.Path + ": write verify failed");
        return $"restored\t{it.Path}";
    }
}
