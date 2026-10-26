using System.IO;
using System.Security.Cryptography;

namespace ModSwitcher2;

/// <summary>What one apply did.</summary>
public sealed class ApplyResult
{
    public readonly List<string> Log = new();
    /// <summary>A file the game reads at start changed (wad, load table, size table): full game restart.</summary>
    public bool FullRestart;
    /// <summary>Nothing on the disk changed (the preset was already there).</summary>
    public bool NoChange = true;
}

/// <summary>
/// Writes a preset to the game folder. Same rules as ApplyPreset / PutFile / RemoveOverride / SlotSet / KingVoiceSet in
/// switcher/ModSwitcher.ps1:
///  - a start-time file (wad, size table, load table) is never written while the game runs, checked before step 1 so a
///    preset never half-applies;
///  - the first time a file under exec\ is touched, its original goes to DataRoot.BackupDir (in development
///    {data}\originals\game_files\, shared with the old app; in a release %LOCALAPPDATA%\SBGModSwitcher\originals);
///  - every copy is read back and compared; removed files go to a recycle folder, never deleted.
/// </summary>
public sealed class ModApplier
{
    readonly string _game, _data, _recycle;
    readonly ModMatcher _m;
    readonly Func<bool> _gameRunning;

    public ModApplier(string gameRoot, string dataRoot, string recycleDir, ModMatcher matcher, Func<bool>? gameRunning = null)
    {
        _game = gameRoot; _data = dataRoot; _recycle = recycleDir; _m = matcher;
        _gameRunning = gameRunning ?? (() => GameLocator.IsGameRunningIn(gameRoot));
    }

    public static string DefaultRecycle => Path.Combine(AppSettings.Dir, "removed");

    // the second wall (after PackManifest's checks, batch A 2026-10-26): every path is made full and must stay inside
    // the game's mod\ or exec\, the data root, or the backup folder, else nothing is read, written or deleted
    string Game(string rel)
    {
        var full = Path.GetFullPath(Path.Combine(_game, rel));
        if (!Under(full, Path.Combine(_game, "mod")) && !Under(full, Path.Combine(_game, "exec")))
            throw new InvalidOperationException($"refused: {rel} is outside the game's mod\\ and exec\\ folders (nothing was changed)");
        return full;
    }
    string Src(string rel) => Inside(_data, rel, "the mod's data folder");
    string Orig(string rel) => Inside(DataRoot.BackupDir(_data), Path.GetRelativePath(_game, Game(rel)), "the backup folder");

    static string Inside(string root, string rel, string what)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!Under(full, root)) throw new InvalidOperationException($"refused: {rel} is outside {what} (nothing was changed)");
        return full;
    }

    static bool Under(string full, string dir) =>
        full.StartsWith(Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every path of a preset through the wall, before step 1: a refusal never leaves it half-applied.</summary>
    void CheckPaths(ModPreset p)
    {
        foreach (var s in p.Steps)
        {
            if (s.Length == 0 || !PackManifest.StepKinds.TryGetValue(s[0], out var k) || s.Length != k.Len) continue;   // the switch below says why
            foreach (var i in k.Game) Game(s[i]);
            foreach (var i in k.Src) Src(s[i]);
        }
    }

    public ApplyResult Apply(ModPreset p)
    {
        if (p.Disabled) throw new InvalidOperationException($"'{p.Name}' cannot be applied: {p.DisabledWhy ?? "not built yet"}");
        CheckPaths(p);
        if (NeedsClosedGame(p) && _gameRunning())
            throw new InvalidOperationException("Close the game first: this preset changes a file the game reads at start (nothing was changed)");
        CheckOriginals(p);

        var r = new ApplyResult();
        // a missing MIO dump is taken from the player's own wad first (sha-checked; off the UI thread: Actions.ApplyAsync)
        foreach (var f in DumpFill.FillMissing(_game)) if (f.LogLine is { } fl) r.Log.Add(fl);
        foreach (var s in p.Steps)
        {
            switch (s[0])
            {
                case "put":
                    if (!_m.SameFile(Src(s[1]), Game(s[2]))) { PutFile(Src(s[1]), s[2], r); Changed(r, "copied " + s[2]); }
                    break;
                case "putrestart":
                    if (!_m.SameFile(Src(s[1]), Game(s[2])))
                    {
                        if (_gameRunning()) throw new InvalidOperationException($"Close the game first: {Path.GetFileName(s[2])} is read at game start");
                        PutFile(Src(s[1]), s[2], r); Changed(r, "copied " + s[2]); r.FullRestart = true;
                    }
                    break;
                case "rm":
                    if (File.Exists(Game(s[1]))) { Remove(s[1]); Changed(r, "removed " + s[1]); }
                    break;
                case "recipe":
                case "reciperestart":
                    if (!_m.RecipeMatches(s[1], s[2]))
                    {
                        if (s[0] == "reciperestart" && _gameRunning())
                            throw new InvalidOperationException($"Close the game first: {Path.GetFileName(s[2])} is read at game start");
                        PutRecipe(s[1], s[2], r); Changed(r, "built from recipe " + s[2]);
                        if (s[0] == "reciperestart") r.FullRestart = true;
                    }
                    break;
                case "restore":
                case "restorerestart":
                    if (!_m.RestoreMatches(s[1]))
                    {
                        if (s[0] == "restorerestart" && _gameRunning())
                            throw new InvalidOperationException($"Close the game first: {Path.GetFileName(s[1])} is read at game start");
                        PutFile(Orig(s[1]), s[1], r); Changed(r, "original back: " + s[1]);
                        if (s[0] == "restorerestart") r.FullRestart = true;
                    }
                    break;
                case "slot":
                    if (SlotSet(s[1], s[2], _m.SlotWant(s[1], s[3]))) { Changed(r, $"memory slot {s[1]} set"); r.FullRestart = true; }
                    break;
                case "kingvo":
                    int n = KingVoiceSet(s[1], s[2] == "on");
                    if (n > 0) { Changed(r, $"{n} King voice lines changed in {Path.GetFileName(s[1])}"); r.FullRestart = true; }
                    break;
                default:
                    throw new InvalidDataException("unknown step kind " + s[0]);
            }
        }
        if (!_m.Matches(p)) throw new IOException("read-back failed: the files do not match the preset after applying it");
        return r;
    }

    static void Changed(ApplyResult r, string line) { r.Log.Add(line); r.NoChange = false; }

    // ---------- Lab (experimental) ----------

    /// <summary>Memory slot -> newSize bytes (size table + wad header). Old app: the -2 MB / +2 MB buttons.</summary>
    public ApplyResult SetSlot(string wadName, string wadRel, uint newSize)
    {
        var r = new ApplyResult();
        if (newSize < Lab.Step) throw new ArgumentException("memory slot: already at the minimum");
        if (SlotSet(wadName, wadRel, newSize)) { Changed(r, $"memory slot {wadName} = {newSize / 1048576} MB"); r.FullRestart = true; }
        return r;
    }

    /// <summary>The original wad back and the original slot size. Old app: the "Original" button.</summary>
    public ApplyResult RestoreSlot(string wadName, string wadRel)
    {
        var r = new ApplyResult();
        uint want = _m.SlotWant(wadName, "orig");
        // no saved original wad (never touched by a Switcher): it is still the game's own, so only the size is reset
        bool haveOrig = File.Exists(Orig(wadRel));
        if (!haveOrig) r.Log.Add("no saved original of " + wadRel + ": size only");
        bool wadDiffers = haveOrig && !_m.SameFile(Orig(wadRel), Game(wadRel));
        if ((wadDiffers || _m.SlotNow(wadName) != want) && _gameRunning())
            throw new InvalidOperationException("Close the game first: the memory slot is read at game start (nothing was changed)");
        if (wadDiffers) { PutFile(Orig(wadRel), wadRel, r); Changed(r, "original wad back: " + wadRel); r.FullRestart = true; }
        // without a saved original, the wad on disk may not be the game's own: never save it as "the original"
        if (SlotSet(wadName, wadRel, want, backupWad: haveOrig)) { Changed(r, $"memory slot {wadName} = original"); r.FullRestart = true; }
        return r;
    }

    /// <summary>
    /// King's Grave figures (old app: ApplyFighters): the dcb for 2 or 3 bosses and the patched encounter script.
    /// The script is also kept in DataRoot.LabDir under a new timestamped name, and the game copy is checked against it.
    /// </summary>
    public ApplyResult ApplyFighters(Lab.Fighters f)
    {
        var r = new ApplyResult();
        // the try 10 base script and the 2 / 3 boss dcbs are recipes on the player's own files (phase 8a)
        Lab.Validate(f);
        var (script, dcb) = Lab.BuildFighters(RecipeBytes(Lab.FightersBase, Lab.KingsVS), f);
        if (!_m.RecipeMatches(dcb, Lab.KingsDcb)) { PutRecipe(dcb, Lab.KingsDcb, r); Changed(r, "built from recipe " + Lab.KingsDcb); }
        var dst = Game(Lab.KingsVS);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        var tag = f.V1 + "_" + f.V2 + (f.V3 == null ? "" : "_" + f.V3);
        var labDir = DataRoot.LabDir(_data);
        Directory.CreateDirectory(labDir);
        var repoCopy = Path.Combine(labDir, $"kingsarena_lab_{tag}_{DateTime.Now:yyyyMMdd-HHmmss}_enc.visualscript");
        File.WriteAllBytes(repoCopy, script);
        SaveReplaced(Lab.KingsVS, r);
        File.WriteAllBytes(dst, script);
        _m.Invalidate(dst);
        if (!SameFresh(dst, repoCopy)) throw new IOException("write verify failed");
        Changed(r, $"King's Grave figures: {f.Describe()} (kept as {repoCopy})");
        return r;
    }

    /// <summary>Would this preset write a wad / size table (the files the game maps at start)?</summary>
    public bool NeedsClosedGame(ModPreset p)
    {
        foreach (var s in p.Steps)
        {
            bool pending = s[0] switch
            {
                "slot" => _m.SlotNow(s[1]) != _m.SlotWant(s[1], s[3]),
                "kingvo" => (s[2] == "on") != (_m.VoiceCount(Game(s[1]))[1] > 0),
                "put" or "putrestart" => s[2].EndsWith(".wad", StringComparison.OrdinalIgnoreCase) && !_m.SameFile(Src(s[1]), Game(s[2])),
                "recipe" => s[2].EndsWith(".wad", StringComparison.OrdinalIgnoreCase) && !_m.RecipeMatches(s[1], s[2]),
                "reciperestart" => !_m.RecipeMatches(s[1], s[2]),   // checked before step 1: never half-applied
                "restore" => s[1].EndsWith(".wad", StringComparison.OrdinalIgnoreCase) && !_m.RestoreMatches(s[1]),
                "restorerestart" => !_m.RestoreMatches(s[1]),
                _ => false,
            };
            if (pending) return true;
        }
        return false;
    }

    // ---------- steps ----------

    /// <summary>
    /// Build a file from a recipe and the player's own original (phase 8a): "dump" = the MIO dump copy of the script,
    /// "game" = the backup in originals\game_files when there is one, else the untouched game file. The recipe
    /// refuses an original with another hash (other game version or another mod).
    /// </summary>
    void PutRecipe(string recipeRel, string rel, ApplyResult r)
    {
        var bytes = RecipeBytes(recipeRel, rel);
        if (rel.StartsWith(@"exec\", StringComparison.OrdinalIgnoreCase)) EnsureBackup(rel);
        var dst = Game(rel);
        SaveReplaced(rel, r);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.WriteAllBytes(dst, bytes);
        _m.Invalidate(dst);
        if (!_m.RecipeMatches(recipeRel, rel)) throw new IOException("write verify failed for " + rel);
    }

    /// <summary>The bytes a recipe makes from the player's own original (packed again for LZ4 targets).</summary>
    byte[] RecipeBytes(string recipeRel, string rel)
    {
        var rec = _m.RecipeOf(recipeRel);
        if (!string.Equals(rec.Target, rel, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"recipe {recipeRel} is for {rec.Target}, not {rel}");
        string OwnOriginal(string target) => File.Exists(Orig(target)) ? Orig(target) : Game(target);
        string basePath = rec.Source == "dump" ? Game(Recipe.DumpPath(rel)) : OwnOriginal(rel);
        if (!File.Exists(basePath))
            throw new FileNotFoundException(rec.Source == "dump"
                ? $"the MIO dump of {Path.GetFileName(rel)} is missing ({Recipe.DumpPath(rel)}): turn on the dump in GoWR MIO and start the game once"
                : "missing game file " + rel);
        return rec.Build(File.ReadAllBytes(basePath), d => File.ReadAllBytes(OwnOriginal(d)));
    }

    void PutFile(string src, string rel, ApplyResult r)
    {
        if (!File.Exists(src)) throw new FileNotFoundException("missing source file " + src);
        if (rel.StartsWith(@"exec\", StringComparison.OrdinalIgnoreCase)) EnsureBackup(rel);
        var dst = Game(rel);
        SaveReplaced(rel, r);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        _m.Invalidate(dst);
        if (!SameFresh(src, dst)) throw new IOException("copy verify failed for " + rel);
    }

    /// <summary>
    /// The file about to be overwritten goes to the recycle folder first, unless it is the saved original (a wrong
    /// click can then always be undone by hand). Wads changed in place by slot / kingvo are not copied: those steps
    /// undo themselves with the other preset.
    /// </summary>
    void SaveReplaced(string rel, ApplyResult r)
    {
        var dst = Game(rel);
        if (!File.Exists(dst)) return;
        var o = Orig(rel);
        if (File.Exists(o) && SameFresh(o, dst)) return;
        Directory.CreateDirectory(_recycle);
        // the same bytes saved before (switching back and forth) are not saved again
        foreach (var old in Directory.EnumerateFiles(_recycle, Path.GetFileName(dst) + ".*.replaced"))
            if (SameFresh(old, dst)) return;
        var keep = Path.Combine(_recycle, $"{Path.GetFileName(dst)}.{DateTime.Now:yyyyMMdd_HHmmss_fff}.replaced");
        File.Copy(dst, keep);
        r.Log.Add("old file saved: " + keep);
    }

    void Remove(string rel)
    {
        var dst = Game(rel);
        Directory.CreateDirectory(_recycle);
        File.Move(dst, Path.Combine(_recycle, $"{Path.GetFileName(dst)}.{DateTime.Now:yyyyMMdd_HHmmss_fff}"));
        _m.Invalidate(dst);
    }

    void EnsureBackup(string rel)
    {
        RequireOriginal(rel);
        var o = Orig(rel);
        if (File.Exists(o) || !File.Exists(Game(rel))) return;
        Directory.CreateDirectory(Path.GetDirectoryName(o)!);
        File.Copy(Game(rel), o);
    }

    /// <summary>
    /// Release only: its backups start empty, so a file another mod or tool already changed would be saved as the
    /// "original" and "Restore" would put the mod back (review 8c, MED). Refuse unless the file with no backup yet is
    /// the game's own (vanilla.json). Development backups predate every mod, so dev is unchanged.
    /// </summary>
    void RequireOriginal(string rel)
    {
        if (!DataRoot.IsRelease(_data) || File.Exists(Orig(rel)) || !File.Exists(Game(rel))) return;
        var ok = _m.IsGameOriginal(rel);
        if (ok == true) return;
        throw new InvalidOperationException(ok == null
            ? $"{Path.GetFileName(rel)}: the app does not know this file's original, so it does not change it (nothing was changed)"
            : $"{Path.GetFileName(rel)} is not the game's original (another mod or tool changed it, or a game update), so it cannot be backed up. " +
              "Steam -> Verify integrity of game files, then try again (nothing was changed).");
    }

    /// <summary>All of RequireOriginal for a preset, before its step 1, so a refusal never leaves it half-applied.</summary>
    void CheckOriginals(ModPreset p)
    {
        if (!DataRoot.IsRelease(_data)) return;
        foreach (var s in p.Steps)
        {
            switch (s[0])
            {
                case "put" or "putrestart" when IsExec(s[2]) && !_m.SameFile(Src(s[1]), Game(s[2])):
                    RequireOriginal(s[2]); break;
                case "recipe" or "reciperestart" when IsExec(s[2]) && !_m.RecipeMatches(s[1], s[2]):
                    RequireOriginal(s[2]); break;
                // slot / kingvo can also write when only the wad header or some voice lines differ: always checked
                // (cheap once: the hash is cached, and a file with a backup returns at once)
                case "slot":
                    RequireOriginal(SizeTable); RequireOriginal(s[2]); break;
                case "kingvo":
                    RequireOriginal(s[1]); break;
                case "restore" or "restorerestart" when !_m.RestoreMatches(s[1]) && !File.Exists(Orig(s[1])):
                    throw new InvalidOperationException($"{Path.GetFileName(s[1])} is not the game's original and this app has no backup of it. " +
                                                        "Steam -> Verify integrity of game files (nothing was changed).");
            }
        }
    }

    static bool IsExec(string rel) => rel.StartsWith(@"exec\", StringComparison.OrdinalIgnoreCase);

    const string SizeTable = @"exec\dc\pc_le\gbl_wadsizetable.dcb";

    /// <summary>Size table entry + wad header heap size. True when either was written (both are read at game start).</summary>
    bool SlotSet(string wadName, string wadRel, uint newSize, bool backupWad = true)
    {
        var stPath = Game(SizeTable);
        var st = GowrTools.Lz4Read(stPath);
        uint old = GowrTools.GetWadSize(st, wadName);
        var w = GowrTools.Lz4Read(Game(wadRel));
        uint hdr = GowrTools.GetU32(w, 12);
        if (hdr == newSize && old == newSize) return false;
        if (_gameRunning()) throw new InvalidOperationException("Close the game first: the memory slot is read at game start");

        EnsureBackup(SizeTable);
        if (backupWad) EnsureBackup(wadRel);
        if (hdr != newSize)
        {
            GowrTools.SetWadHeap(w, hdr, newSize);
            GowrTools.Lz4Write(Game(wadRel), w);
            _m.Invalidate(Game(wadRel));
        }
        if (old != newSize)
        {
            GowrTools.SetWadSize(st, wadName, newSize);
            GowrTools.Lz4Write(stPath, st);
            _m.Invalidate(stPath);
        }
        if (GowrTools.GetWadSize(GowrTools.Lz4Read(stPath), wadName) != newSize) throw new IOException("size table read-back mismatch");
        if (GowrTools.GetU32(GowrTools.Lz4Read(Game(wadRel)), 12) != newSize) throw new IOException("wad header read-back mismatch");
        return true;
    }

    /// <summary>King banter speaker -> Source (on) / King (off), in place. Returns the lines changed.</summary>
    int KingVoiceSet(string wadRel, bool on)
    {
        var path = Game(wadRel);
        var w = GowrTools.Lz4Read(path);
        int n = GowrTools.KingVoiceSet(w, on);
        if (n > 0)
        {
            if (_gameRunning()) throw new InvalidOperationException("Close the game first: " + Path.GetFileName(wadRel) + " is read at game start");
            EnsureBackup(wadRel);
            GowrTools.Lz4Write(path, w);
            _m.Invalidate(path);
        }
        var c = GowrTools.KingVoiceCount(GowrTools.Lz4Read(path));
        if (on ? c[1] == 0 : c[1] != 0) throw new IOException($"King voice read-back mismatch (King {c[0]}, Source {c[1]})");
        return n;
    }

    static bool SameFresh(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        return fa.Length == fb.Length && SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
    }
}
