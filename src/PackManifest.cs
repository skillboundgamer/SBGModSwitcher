using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

/// <summary>One game file a pack's mod may change: shown in the preview before install.</summary>
public sealed record PackGameFile(string Mod, string Path);

/// <summary>
/// The one gate of a mod pack (OpenIV way, plan 2026-10-26): pack.json, mods.json and every file name are read and
/// checked here, from the zip before anything is unpacked and from an installed folder before its mods are shown.
/// Nothing else reads a pack's pack.json or mods.json. Checks: names (no "..", no absolute path or drive, only data file
/// types, no reserved Windows names), sizes, pack.json and mods.json readable and matching, needsSwitcher, step kinds
/// and their item count, game paths only under mod\ and exec\, sources relative inside the pack, a put keeps its file
/// type. All of it is file work: call it off the UI thread.
/// </summary>
public sealed class PackManifest
{
    public const string FileName = "pack.json";
    public const long MaxZipBytes = 50L * 1024 * 1024;      // the Tyr pack is well under 1 MB
    public const long MaxUnpackedBytes = 200L * 1024 * 1024;
    public const int MaxEntries = 4000;
    static readonly string[] AllowedExt = { ".json", ".recipe", ".lua", ".txt", ".md" };
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "conin$", "conout$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };
    internal static readonly Regex IdRx = new("^[a-z0-9][a-z0-9-]{0,39}$");

    /// <summary>The app version a pack's "needsSwitcher" is compared with (csproj Version).</summary>
    public static Version AppVersion => typeof(PackManifest).Assembly.GetName().Version ?? new Version(2, 0);

    public Edition Info { get; }
    public ModCatalog Catalog { get; }

    /// <summary>Every game file a preset of the pack may write or delete, by mod, sorted (the preview list).</summary>
    public IReadOnlyList<PackGameFile> GameFiles { get; }

    PackManifest(Edition info, ModCatalog cat)
    {
        Info = info;
        Catalog = cat;
        var files = new List<PackGameFile>();
        foreach (var g in cat.Groups)
        {
            var seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in g.Presets)
                foreach (var s in p.Steps)
                {
                    foreach (var i in StepKinds[s[0]].Game) seen.Add(s[i].Replace('/', '\\'));
                    if (s[0] == "slot") seen.Add(SizeTable);
                }
            files.AddRange(seen.Select(f => new PackGameFile(g.Name, f)));
        }
        if (info.Loadout)
            files.AddRange(Loader.Files.Select(f => new PackGameFile("Kratos loadout", f.Game)));
        GameFiles = files;
    }

    const string SizeTable = @"exec\dc\pc_le\gbl_wadsizetable.dcb";   // a "slot" step also writes it (Apply.cs SlotSet)

    /// <summary>Read and check a pack zip without unpacking it. Throws PackError with the reason for the player.</summary>
    public static PackManifest FromZip(string zipPath, Version app)
    {
        var fi = new FileInfo(zipPath);
        if (!fi.Exists) throw new PackError("The file is gone: " + zipPath);
        if (fi.Length > MaxZipBytes) throw new PackError($"Too big for a mod pack ({fi.Length / 1048576} MB, the limit is {MaxZipBytes / 1048576} MB).");
        ZipArchive zip;
        try { zip = ZipFile.OpenRead(zipPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { throw new PackError("This is not a zip file (or it is damaged)."); }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries) throw new PackError($"Too many files in the zip ({zip.Entries.Count}).");
            long total = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in zip.Entries)
            {
                var rel = SafeName(e.FullName);
                if (rel == null) continue;   // a folder entry
                if (!seen.Add(rel)) throw new PackError("The zip holds the same file twice: " + rel);
                total += e.Length;
                if (total > MaxUnpackedBytes) throw new PackError("The zip unpacks to too much data.");
            }
            var packEntry = zip.GetEntry(FileName) ?? throw new PackError("Not a mod pack: pack.json is missing at the top of the zip.");
            var modsEntry = zip.Entries.FirstOrDefault(x => SafeName(x.FullName) is { } r && r.Equals(ModsJson, StringComparison.OrdinalIgnoreCase))
                            ?? throw new PackError("Not a mod pack: switcher2/mods.json is missing.");
            return Parse(ReadText(packEntry), ReadText(modsEntry), app);
        }
    }

    /// <summary>Read and check an installed pack folder (the same checks: a folder may have been changed by hand).</summary>
    public static PackManifest FromFolder(string root, Version app)
    {
        string packText, modsText;
        try
        {
            packText = File.ReadAllText(Path.Combine(root, FileName));
            modsText = File.ReadAllText(Path.Combine(root, ModsJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new PackError($"{Path.GetFileName(root)}: {ex.Message}"); }
        return Parse(packText, modsText, app);
    }

    /// <summary>Only pack.json of an installed folder (the pack list): id, title, version. Throws PackError.</summary>
    public static Edition ReadInfo(string root)
    {
        var name = Path.GetFileName(root);
        Edition info;
        try { info = Edition.Parse(File.ReadAllText(Path.Combine(root, FileName)), name + @"\" + FileName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { throw new PackError(ex.Message); }
        if (info.Id != name) throw new PackError($"{name}: pack.json says id \"{info.Id}\"");
        return info;
    }

    const string ModsJson = @"switcher2\mods.json";

    static PackManifest Parse(string packText, string modsText, Version app)
    {
        Edition info;
        ModCatalog cat;
        try
        {
            info = Edition.Parse(packText, FileName);
            cat = ModCatalog.Parse(modsText, info, FileName);
        }
        catch (InvalidDataException ex) { throw new PackError(ex.Message); }
        catch (Exception ex) when (ex is not PackError) { throw new PackError("pack.json or mods.json is broken: " + ex.Message); }
        CheckInfo(info, app);
        CheckSteps(cat);
        return new PackManifest(info, cat);
    }

    /// <summary>No group id that another installed pack uses (<paramref name="taken"/>: group id -> (pack id, title));
    /// its own older version does not count.</summary>
    public void CheckGroupsFree(IReadOnlyDictionary<string, (string PackId, string Title)> taken)
    {
        foreach (var g in Catalog.Groups)
            if (taken.TryGetValue(g.Id, out var other) && other.PackId != Info.Id)
                throw new PackError($"\"{g.Name}\" is already in {other.Title}. Remove that one first.");
    }

    static void CheckInfo(Edition info, Version app)
    {
        if (!IdRx.IsMatch(info.Id)) throw new PackError("pack.json: \"id\" must be lower-case letters, digits and '-' (up to 40).");
        if (info.NeedsSwitcher.Length > 0)
        {
            if (!Version.TryParse(info.NeedsSwitcher, out var need)) throw new PackError("pack.json: \"needsSwitcher\" is not a version.");
            if (Norm(need) > Norm(app))
                throw new PackError($"{info.Title} needs Mod Switcher {info.NeedsSwitcher} or newer. You have {app.ToString(3)}: download the new Mod Switcher first.");
        }
    }

    /// <summary>The step kinds of mods.json with their item count, and which items are game paths / pack files (Apply.cs).</summary>
    internal static readonly Dictionary<string, (int Len, int[] Game, int[] Src)> StepKinds = new()
    {
        ["put"] = (3, new[] { 2 }, new[] { 1 }), ["putrestart"] = (3, new[] { 2 }, new[] { 1 }),
        ["recipe"] = (3, new[] { 2 }, new[] { 1 }), ["reciperestart"] = (3, new[] { 2 }, new[] { 1 }),
        ["rm"] = (2, new[] { 1 }, Array.Empty<int>()),
        ["restore"] = (2, new[] { 1 }, Array.Empty<int>()), ["restorerestart"] = (2, new[] { 1 }, Array.Empty<int>()),
        ["kingvo"] = (3, new[] { 1 }, Array.Empty<int>()),
        ["slot"] = (4, new[] { 2 }, Array.Empty<int>()),
    };

    /// <summary>
    /// A pack's steps may only touch the game's mod\ and exec\ folders and read its own files: every path relative,
    /// no "..", no drive; a put keeps its file type. Else turning a mod on could write or delete anywhere (review 10-26).
    /// </summary>
    internal static void CheckSteps(ModCatalog cat)
    {
        foreach (var g in cat.Groups)
            foreach (var p in g.Presets)
                foreach (var s in p.Steps)
                {
                    var where = $"mods.json, \"{g.Name}\" / \"{p.Name}\"";
                    if (s.Length == 0 || !StepKinds.TryGetValue(s[0], out var k) || s.Length != k.Len)
                        throw new PackError($"{where}: unknown or broken step [{string.Join(", ", s)}]");
                    foreach (var i in k.Game)
                    {
                        var rel = RelPath(s[i], where);
                        if (!rel.StartsWith(@"mod\", StringComparison.OrdinalIgnoreCase) && !rel.StartsWith(@"exec\", StringComparison.OrdinalIgnoreCase))
                            throw new PackError($"{where}: a mod may change only the game's mod\\ and exec\\ folders, not {s[i]}");
                    }
                    foreach (var i in k.Src) RelPath(s[i], where);
                    if (s[0] is "put" or "putrestart" && !Path.GetExtension(s[1]).Equals(Path.GetExtension(s[2]), StringComparison.OrdinalIgnoreCase))
                        throw new PackError($"{where}: a put must keep the file type ({s[1]} -> {s[2]})");
                }
    }

    static string RelPath(string path, string where)
    {
        var n = path.Replace('/', '\\');
        if (n.Length == 0 || n.StartsWith('\\') || n.Contains(':') || Path.IsPathRooted(n) || n.Split('\\').Any(x => x is ".." or "." or ""))
            throw new PackError($"{where}: unsafe path {path}");
        return n;
    }

    static Version Norm(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    static string ReadText(ZipArchiveEntry e)
    {
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    /// <summary>The entry's path with '\' separators, or null for a folder entry. Throws on anything unsafe.</summary>
    internal static string? SafeName(string fullName)
    {
        if (fullName.EndsWith('/') || fullName.EndsWith('\\')) return null;
        var n = fullName.Replace('/', '\\');
        if (n.StartsWith('\\') || n.Contains(':') || Path.IsPathRooted(n))
            throw new PackError("The zip has a file with an absolute path: " + fullName);
        var parts = n.Split('\\');
        if (parts.Any(p => p == ".." || p == "." || p.Length == 0 || p.Trim().Length != p.Length || p.EndsWith('.')))
            throw new PackError("The zip has a file with an unsafe path: " + fullName);
        if (n.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || parts.Any(p => p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new PackError("The zip has a file with an unsafe name: " + fullName);
        // Windows device names (con, nul, com1 ...) are not files, also with an extension
        if (parts.Any(p => Reserved.Contains(p.Split('.')[0])))
            throw new PackError("The zip has a file with a reserved Windows name: " + fullName);
        var ext = Path.GetExtension(n).ToLowerInvariant();
        if (!AllowedExt.Contains(ext))
            throw new PackError($"A mod pack may hold only data files (.json .recipe .lua .txt .md), not {(ext.Length == 0 ? "\"" + parts[^1] + "\"" : ext)}.");
        return n;
    }
}
