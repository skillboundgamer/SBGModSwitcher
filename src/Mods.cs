using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ModSwitcher2;

// ---------- the catalog: mods.json ----------

public sealed class ModPreset
{
    public string Name { get; set; } = "";
    public string? Short { get; set; }
    public string? Warn { get; set; }
    public bool Disabled { get; set; }
    /// <summary>Why it is disabled, shown on hover (default "coming soon").</summary>
    public string? DisabledWhy { get; set; }
    public List<string[]> Steps { get; set; } = new();
    public string Label => string.IsNullOrEmpty(Short) ? Name : Short;
}

public sealed class ModGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tab { get; set; } = "boss";
    public string Icon { get; set; } = "IcSword";
    public string Reload { get; set; } = "menu";
    public string? Note { get; set; }
    public int Vanilla { get; set; }
    public List<ModPreset> Presets { get; set; } = new();
}

public sealed class ModCatalog
{
    public int Version { get; set; }
    public List<ModGroup> Groups { get; set; } = new();
    public List<ModRule> Rules { get; set; } = new();

    /// <summary>An edition (Edition.cs) keeps only its own groups; null = the whole catalog.</summary>
    public static ModCatalog Load(string path, Edition? edition = null)
    {
        var cat = JsonSerializer.Deserialize<ModCatalog>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidDataException("mods.json is empty");
        edition?.Filter(cat);
        return cat;
    }
}

// ---------- the matcher: which preset is on the disk now (read-only) ----------

/// <summary>
/// Same rules as PresetMatches in switcher/ModSwitcher.ps1: a preset is active when every step already holds.
/// Reads only. Expensive results (sha256, LZ4 reads) are cached by path + size + last write time.
/// </summary>
public sealed class ModMatcher
{
    readonly string _game, _data;
    readonly ConcurrentDictionary<string, (long Len, long Ticks, object Value)> _cache = new();

    public ModMatcher(string gameRoot, string dataRoot) { _game = gameRoot; _data = dataRoot; }

    /// <summary>Index of the first preset that holds, or -1 (custom: the files match none of them).</summary>
    public int Check(ModGroup g)
    {
        for (int i = 0; i < g.Presets.Count; i++)
        {
            // A preset whose check throws (missing file, unreadable wad) is simply not active, as in the old app.
            try { if (Matches(g.Presets[i])) return i; }
            catch (Exception) { }
        }
        return -1;
    }

    public bool Matches(ModPreset p)
    {
        if (p.Disabled) return false;
        foreach (var s in p.Steps)
        {
            bool ok = s[0] switch
            {
                "put" or "putrestart" => SameFile(Path.Combine(_data, s[1]), Path.Combine(_game, s[2])),
                "rm" => !File.Exists(Path.Combine(_game, s[1])),
                "recipe" or "reciperestart" => RecipeMatches(s[1], s[2]),
                "restore" or "restorerestart" => RestoreMatches(s[1]),
                "slot" => SlotNow(s[1]) == SlotWant(s[1], s[3]),
                "kingvo" => (s[2] == "on") == (VoiceCount(Path.Combine(_game, s[1]))[1] > 0),
                _ => throw new InvalidDataException("unknown step kind " + s[0]),
            };
            if (!ok) return false;
        }
        return true;
    }

    // ---------- recipes (phase 8a): no game files shipped ----------

    readonly ConcurrentDictionary<string, Recipe> _recipes = new();
    sealed record UnpackedHash(string Value); // its own cache key type: not the raw-file Hash

    internal Recipe RecipeOf(string recipeRel) =>
        _recipes.GetOrAdd(Path.GetFullPath(Path.Combine(_data, recipeRel)), p => Recipe.Parse(p));

    /// <summary>The game file holds the recipe's result (compared unpacked: an LZ4 file may be packed differently).</summary>
    internal bool RecipeMatches(string recipeRel, string dest)
    {
        var r = RecipeOf(recipeRel);
        if (!string.Equals(r.Target, dest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"recipe {recipeRel} is for {r.Target}, not {dest}");
        var f = new FileInfo(Path.Combine(_game, dest));
        return f.Exists && UnpackedSha(f) == r.Result;
    }

    internal string UnpackedSha(FileInfo f) =>
        Cached(f, () => new UnpackedHash(Recipe.Sha(Recipe.Unpack(File.ReadAllBytes(f.FullName))))).Value;

    /// <summary>
    /// "Vanilla" for a game file: equal to the player's own backup; never backed up = never touched = vanilla.
    /// A release has no backups at first (they live in %LOCALAPPDATA%), so there "never backed up" also needs the
    /// file to be the game's own (vanilla.json): a game modded by another tool must not read as vanilla (review 8c).
    /// </summary>
    internal bool RestoreMatches(string dest)
    {
        var backup = Path.Combine(DataRoot.BackupDir(_data), dest);
        if (!File.Exists(backup)) return !DataRoot.IsRelease(_data) || IsGameOriginal(dest) != false;
        return SameFile(backup, Path.Combine(_game, dest));
    }

    // ---------- the game's own files (vanilla.json, tools/mkvanilla.py): hashes only ----------

    Dictionary<string, string>? _vanilla;

    /// <summary>Is the game file still the game's own? null = no hash known for it.</summary>
    internal bool? IsGameOriginal(string rel)
    {
        var map = _vanilla ??= LoadVanilla();     // a race only builds the same map twice
        if (!map.TryGetValue(rel, out var want)) return null;
        var f = new FileInfo(Path.Combine(_game, rel));
        return f.Exists && UnpackedSha(f) == want;
    }

    Dictionary<string, string> LoadVanilla()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var p = Path.Combine(_data, "switcher2", "vanilla.json");
        if (!File.Exists(p)) return d;
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
        foreach (var f in doc.RootElement.GetProperty("files").EnumerateObject()) d[f.Name] = f.Value.GetString()!;
        return d;
    }

    internal bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false; // different size = different hash
        return Hash(fa) == Hash(fb);
    }

    string Hash(FileInfo f) => Cached(f, () =>
    {
        using var s = f.OpenRead();
        return Convert.ToHexString(SHA256.HashData(s));
    });

    internal int[] VoiceCount(string wad) => Cached(new FileInfo(wad), () => GowrTools.KingVoiceCount(GowrTools.Lz4Read(wad)));

    internal uint SlotNow(string wadName) => WadSize(Path.Combine(_game, @"exec\dc\pc_le\gbl_wadsizetable.dcb"), wadName);

    /// <summary>
    /// The wanted slot size; "orig" = the size in the player's backed-up size table. No backup = the table was never
    /// touched by a Switcher, so the game's own table IS the original (a fresh PC: review finding 2026-10-17).
    /// </summary>
    internal uint SlotWant(string wadName, string mb)
    {
        if (mb != "orig") return uint.Parse(mb) * 1024 * 1024;
        const string table = @"exec\dc\pc_le\gbl_wadsizetable.dcb";
        var backup = Path.Combine(DataRoot.BackupDir(_data), table);
        if (File.Exists(backup)) return WadSize(backup, wadName);
        // a release with no backup: the game's table is the original only while it is still the game's own
        if (DataRoot.IsRelease(_data) && IsGameOriginal(table) == false)
            throw new InvalidDataException("the memory slot table was changed by another mod or tool: its original sizes are unknown");
        return WadSize(Path.Combine(_game, table), wadName);
    }

    uint WadSize(string table, string wadName) =>
        GowrTools.GetWadSize(Cached(new FileInfo(table), () => GowrTools.Lz4Read(table)), wadName);

    /// <summary>Forget what is cached for a file we just wrote (a copy keeps the source's write time).</summary>
    internal void Invalidate(string path)
    {
        var prefix = Path.GetFullPath(path).ToLowerInvariant() + "|";
        foreach (var k in _cache.Keys) if (k.StartsWith(prefix)) _cache.TryRemove(k, out _);
    }

    T Cached<T>(FileInfo f, Func<T> make) where T : notnull
    {
        if (!f.Exists) throw new FileNotFoundException(f.FullName);
        long len = f.Length, ticks = f.LastWriteTimeUtc.Ticks;
        var key = f.FullName.ToLowerInvariant() + "|" + typeof(T).Name;
        if (_cache.TryGetValue(key, out var c) && c.Len == len && c.Ticks == ticks) return (T)c.Value;
        var v = make();
        _cache[key] = (len, ticks, v);
        return v;
    }
}

/// <summary>
/// Where mods.json and the preset source files live:
///  - release (phase 8c): the "data" folder beside the exe, marked by data\release.txt (tools/mkrelease.py);
///  - development: the project folder, found by walking up from the exe.
/// The paths inside are the same in both, so mods.json does not change.
/// </summary>
public static class DataRoot
{
    public const string ReleaseMarker = "release.txt";

    public static string? Find()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "data");
        if (File.Exists(CatalogPath(beside))) return beside;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(CatalogPath(d.FullName))) return d.FullName;
        return null;
    }

    public static string CatalogPath(string root) => Path.Combine(root, "switcher2", "mods.json");

    public static bool IsRelease(string root) => File.Exists(Path.Combine(root, ReleaseMarker));

    /// <summary>
    /// The player's backups of the original game files: in a release under %LOCALAPPDATA%\SBGModSwitcher (the app
    /// folder may be read-only or replaced by the next version); in development the shared originals folder.
    /// </summary>
    public static string BackupDir(string root) => IsRelease(root)
        ? Path.Combine(AppSettings.Dir, "originals", "game_files")
        : Path.Combine(root, "originals", "game_files");

    /// <summary>Where the Lab keeps each King's Grave script it wrote.</summary>
    public static string LabDir(string root) => IsRelease(root)
        ? Path.Combine(AppSettings.Dir, "lab")
        : Path.Combine(root, "versions", "lab");
}
