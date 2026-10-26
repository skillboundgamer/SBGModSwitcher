using System.IO;
using System.Text.Json;

namespace ModSwitcher2;

/// <summary>The journal of the install that runs now (packs\.journal.json): which step it reached, and its folders.</summary>
public sealed class PackJournal
{
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>unpack, unpacked, off, swap, swapped, undo (see <see cref="PackInstaller"/>).</summary>
    public string Step { get; set; } = "";
    /// <summary>Folder names inside packs\ (never full paths): the unpacked new version, the old version moved away.</summary>
    public string? Tmp { get; set; }
    public string? Old { get; set; }
}

/// <summary>
/// The one installer of a mod pack (plan 2026-10-26, batch A): check -> preview -> unpack to a hidden folder -> the old
/// version's mods off -> swap -> show. Each step is written to packs\.journal.json before it runs; a failure undoes what
/// was done, and at the next start <see cref="Recover"/> finishes a swap that went through or undoes anything else. So
/// a pack is never half-installed: either the old version (its mods off, when that step ran) or the new one is there.
/// The hooks run on the caller's thread (the UI); the file work runs on the thread pool.
/// Hidden test switch: env SBG_PACK_CRASH = unpacked | off | swap-mid | swapped ends the process right after that point.
/// </summary>
public sealed class PackInstaller
{
    public const string JournalName = ".journal.json";
    static string JournalPath => Path.Combine(Packs.Dir, JournalName);
    static readonly string? CrashAt = Environment.GetEnvironmentVariable("SBG_PACK_CRASH");

    /// <summary>The preview: the checked pack and the game files it may change. False = cancelled, nothing written.</summary>
    public Func<PackManifest, Task<bool>>? Confirm;
    /// <summary>An update: the installed version's mods to vanilla. False = stop; the old version stays.</summary>
    public Func<PackManifest, Task<bool>>? TurnOldOff;
    /// <summary>Add the new pack's cards. A throw undoes the install (the old version comes back).</summary>
    public Func<InstalledPack, PackManifest, Task>? Show;
    /// <summary>Step index (0 check, 1 unpack, 2 old off, 3 put in place, 4 show) and the bar's share at its start.</summary>
    public Action<int, double>? Step;
    public IProgress<double>? UnpackProgress;
    /// <summary>Group id -> (pack id, title) of the installed packs: a group of another pack is refused.</summary>
    public IReadOnlyDictionary<string, (string PackId, string Title)>? Taken;

    /// <summary>Install a zip. Null = cancelled at the preview. Throws PackError (or an IO error) after undoing.</summary>
    public async Task<InstalledPack?> InstallAsync(string zip, Version app)
    {
        Step?.Invoke(0, 0.02);
        var man = await Task.Run(() => PackManifest.FromZip(zip, app));
        if (Taken != null) man.CheckGroupsFree(Taken);
        if (Confirm != null && !await Confirm(man)) return null;

        var id = man.Info.Id;
        var target = Path.Combine(Packs.Dir, id);
        var j = new PackJournal { Id = id, Version = man.Info.Version, Step = "unpack" };
        // an older interrupted install first, outside the undo below: when it fails, its journal must stay (review 10-26 HIGH)
        await Task.Run(Recover);
        try
        {
            await Task.Run(() => Write(j));
            Step?.Invoke(1, 0.2);
            var tmp = await Task.Run(() => Packs.Unpack(zip, man.Info, UnpackProgress));
            j.Tmp = Path.GetFileName(tmp);
            j.Step = "unpacked";
            await Task.Run(() => Write(j));
            Crash("unpacked");

            Step?.Invoke(2, 0.55);
            bool hasOld = Directory.Exists(target);
            if (hasOld && TurnOldOff != null && !await TurnOldOff(man))
                throw new PackError("Some of the old version's mods could not be turned off (see Activity). The old version is still installed; the new one was not.");
            j.Step = "off";
            await Task.Run(() => Write(j));
            Crash("off");

            Step?.Invoke(3, 0.75);
            j.Old = hasOld ? ".old-" + id + "-" + Guid.NewGuid().ToString("N") : null;
            j.Step = "swap";
            await Task.Run(() =>
            {
                Write(j);
                if (j.Old != null) Directory.Move(target, Path.Combine(Packs.Dir, j.Old));
                Crash("swap-mid");
                Directory.Move(tmp, target);
                j.Step = "swapped";
                Write(j);
            });
            Crash("swapped");

            Step?.Invoke(4, 0.88);
            var pack = new InstalledPack(man.Info, target);
            if (Show != null) await Show(pack, man);
        }
        catch (Exception ex)
        {
            try
            {
                await Task.Run(() =>
                {
                    // "undo" first: if the undo cannot run now, the next start undoes too, never finishes (review 10-26 MED)
                    j.Step = "undo";
                    Write(j);
                    Undo(j);
                });
            }
            catch (Exception uex) when (uex is IOException or UnauthorizedAccessException)
            {
                // the journal stays: the next start finishes or undoes it (Recover); the window must not show either version
                UndoFailed = true;
                throw new PackError(ex.Message + " It could not be undone yet (a file is in use): restart the app to finish.");
            }
            throw;
        }
        // the new version is shown: only the clean-up is left, never an undo (a failure here leaves the journal for Recover)
        try { await Task.Run(() => Finish(j)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return new InstalledPack(man.Info, target);
    }

    /// <summary>True after a failed install whose undo could not run: the files wait for the next start.</summary>
    public bool UndoFailed { get; private set; }

    /// <summary>
    /// At start (Packs.List) and before an install: an interrupted install is finished when its new folder is already in
    /// place (the swap went through), else undone (the old version back, the hidden folders deleted).
    /// </summary>
    public static void Recover()
    {
        if (!File.Exists(JournalPath)) return;
        PackJournal? j;
        try { j = JsonSerializer.Deserialize<PackJournal>(File.ReadAllText(JournalPath)); }
        catch (JsonException) { j = null; }
        if (j == null || j.Id == null || !PackManifest.IdRx.IsMatch(j.Id) || !SafeDir(j.Tmp, ".tmp-") || !SafeDir(j.Old, ".old-" + j.Id + "-"))
        {
            File.Delete(JournalPath);   // unreadable: Packs.List deletes the hidden folders, the installed ones stay
            return;
        }
        if (j.Step != "undo" && NewInPlace(j)) Finish(j); else Undo(j);
    }

    /// <summary>True when the swap moved the new folder in: the unpacked folder is gone and packs\&lt;id&gt; exists.</summary>
    static bool NewInPlace(PackJournal j) =>
        j.Step is "swap" or "swapped" or "undo" && j.Tmp != null && !Directory.Exists(Path.Combine(Packs.Dir, j.Tmp))
        && Directory.Exists(Path.Combine(Packs.Dir, j.Id));

    static void Finish(PackJournal j)
    {
        File.Delete(JournalPath);   // first: a left-over .old- folder without a journal is only garbage (Packs.List)
        if (j.Old != null) TryDelete(Path.Combine(Packs.Dir, j.Old));
    }

    static void Undo(PackJournal j)
    {
        var target = Path.Combine(Packs.Dir, j.Id);
        if (NewInPlace(j))
        {
            // the new version is in place but must go: moved first, so a half-deleted folder never shows as installed
            var gone = Path.Combine(Packs.Dir, ".del-" + Guid.NewGuid().ToString("N"));
            Directory.Move(target, gone);
            TryDelete(gone);
        }
        if (j.Old != null && Directory.Exists(Path.Combine(Packs.Dir, j.Old)) && !Directory.Exists(target))
            Directory.Move(Path.Combine(Packs.Dir, j.Old), target);
        if (j.Tmp != null) TryDelete(Path.Combine(Packs.Dir, j.Tmp));
        if (File.Exists(JournalPath)) File.Delete(JournalPath);
    }

    static bool SafeDir(string? name, string prefix) =>
        name == null || (name.StartsWith(prefix, StringComparison.Ordinal) && name.IndexOfAny(new[] { '\\', '/', ':' }) < 0 && !name.Contains(".."));

    static void Write(PackJournal j)
    {
        Directory.CreateDirectory(Packs.Dir);
        var tmp = JournalPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(j));
        File.Move(tmp, JournalPath, overwrite: true);
    }

    static void Crash(string point)
    {
        if (CrashAt == point) Environment.Exit(3);
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* a file in use: Packs.List deletes hidden folders at the next start */ }
    }
}
