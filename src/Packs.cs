using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

/// <summary>A pack zip or folder that cannot be used; the message is shown to the player as it is.</summary>
public sealed class PackError : Exception
{
    public PackError(string message) : base(message) { }
}

/// <summary>One installed pack: its pack.json and its folder (the data root of its groups).</summary>
public sealed record InstalledPack(Edition Info, string Root);

/// <summary>
/// Mod packs (Nexus plan v2, notes/state/nexus-release.md): the app is a one-time download, each mod is a data-only zip.
/// A pack zip holds at its root <c>pack.json</c> (the fields of an edition, Edition.cs, plus "needsSwitcher"),
/// <c>switcher2\mods.json</c>, <c>recipes\*.recipe</c> and the loadout Lua at the same relative paths a release data
/// folder uses (tools/mkrelease.py DATA_FILES). Only .json .recipe .lua .txt .md files are allowed: never an exe or dll.
/// Installed packs live in <c>%LOCALAPPDATA%\SBGModSwitcher\packs\&lt;id&gt;\</c>; each one is a data root of its own,
/// marked with release.txt so backups and the Lab folder stay app-level (DataRoot.BackupDir). vanilla.json stays with
/// the app (DataRoot.VanillaPath). Everything here does file work: call it off the UI thread.
/// </summary>
public static class Packs
{
    public static string Dir => Path.Combine(AppSettings.Dir, "packs");

    /// <summary>Unpack a checked zip into a new hidden folder under <see cref="Dir"/> (same disk, so the swap is a rename).</summary>
    public static string Unpack(string zipPath, Edition info, IProgress<double>? progress = null)
    {
        Directory.CreateDirectory(Dir);
        var tmp = Path.Combine(Dir, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        var rootFull = Path.GetFullPath(tmp) + Path.DirectorySeparatorChar;
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var files = zip.Entries.Select(e => (e, rel: PackManifest.SafeName(e.FullName))).Where(x => x.rel != null).ToList();
            for (int i = 0; i < files.Count; i++)
            {
                var dest = Path.GetFullPath(Path.Combine(tmp, files[i].rel!));
                if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new PackError("Unsafe path in the zip: " + files[i].e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                files[i].e.ExtractToFile(dest, overwrite: false);
                progress?.Report((i + 1) / (double)files.Count);
            }
            // the marker makes the folder a release data root (backups app-level); the unpacked pack is checked again
            File.WriteAllText(Path.Combine(tmp, DataRoot.ReleaseMarker), $"pack {info.Id} {info.Version}\n");
            if (PackManifest.FromFolder(tmp, PackManifest.AppVersion).Info.Id != info.Id) throw new PackError("The unpacked pack is not the one that was checked.");
            return tmp;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Delete an installed pack's folder (its mods must be off first).</summary>
    public static void Delete(string id)
    {
        if (!PackManifest.IdRx.IsMatch(id)) throw new PackError("Bad pack id: " + id);
        var target = Path.Combine(Dir, id);
        if (!Directory.Exists(target)) return;
        // moved first, so a half-deleted pack never shows up as installed
        var gone = Path.Combine(Dir, ".del-" + Guid.NewGuid().ToString("N"));
        Directory.Move(target, gone);
        TryDelete(gone);
    }

    /// <summary>Installed packs, by folder name. A broken one is listed in <paramref name="errors"/> and skipped.
    /// An interrupted install is finished or undone first (PackInstaller.Recover); then hidden folders are left-overs.</summary>
    public static List<InstalledPack> List(List<string>? errors = null)
    {
        var list = new List<InstalledPack>();
        if (!Directory.Exists(Dir)) return list;
        try { PackInstaller.Recover(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a folder in use: keep every hidden folder (the journal needs them) and try again at the next start
            errors?.Add("an interrupted install could not be finished yet: " + ex.Message);
            return Directory.GetDirectories(Dir).Where(d => !Path.GetFileName(d).StartsWith('.'))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Select(d => TryRead(d, errors)).OfType<InstalledPack>().ToList();
        }
        foreach (var d in Directory.GetDirectories(Dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(d).StartsWith('.')) { TryDelete(d); continue; }
            if (TryRead(d, errors) is { } p) list.Add(p);
        }
        return list;
    }

    static InstalledPack? TryRead(string d, List<string>? errors)
    {
        try { return new InstalledPack(PackManifest.ReadInfo(d), d); }
        catch (PackError ex) { errors?.Add(ex.Message); return null; }
    }

    static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception) { /* a file in use: the next start cleans it up (List) */ }
    }
}
