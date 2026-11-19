using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace ModSwitcher2;

/// <summary>
/// The file owner map of the problem report (2.0.5 B2): who put a file in the game folder. Tags: OURS (the loader
/// records of this app, or a file our presets / packs / loader chain write), VOTG (in its receipt or backup folder),
/// MIO (its known files), GAME (the game's own files in the root), UNKNOWN. It does not walk the whole game folder
/// (100+ GB): only the root files, the <c>mod\</c> tree (the MIO dump and the VOTG backup are counted, not listed)
/// and the <c>exec\</c> files this app has a backup of. Read only; call it off the UI thread.
/// </summary>
public sealed class FileOwners
{
    public const string Ours = "OURS", VotgTag = "VOTG", Mio = "MIO", Game = "GAME", Unknown = "UNKNOWN";

    /// <summary>The game's own files in the root (Steam install, checked 2026-11-19). Lower case.</summary>
    static readonly HashSet<string> GameRoot = new(StringComparer.OrdinalIgnoreCase)
    {
        "gowr.exe", "amd_fidelityfx_dx12.dll", "bink2w64.dll", "crs-client.dll", "crs-handler.exe", "crs-uploader.exe",
        "dxcompiler.dll", "install_pspc_sdk_runtime.bat", "installscript.vdf", "libsceagcgpuaddress.dll",
        "libsceagctexturetool.dll", "libscejobmanager.dll", "libxess.dll", "notice", "nvngx_dlss.dll", "nvngx_dlssg.dll",
        "pc_icon_256.png", "pspcsdkruntimeinstaller.msi", "pspcsdkruntimemanager.msi", "sl.common.dll", "sl.dlss_g.dll",
        "sl.dlss.dll", "sl.interposer.dll", "sl.pcl.dll", "sl.reflex.dll", "steam_api64.dll", "steam_appid.txt", "settings.ini",
    };
    static readonly HashSet<string> MioFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        @"mod\gowr_mio.ini", @"mod\gowr_mio.log",
    };
    const int MaxListed = 300;   // per tag; the rest is counted

    readonly string _game;
    readonly HashSet<string> _ours, _votg, _receipt;
    readonly Dictionary<string, LoaderRecord> _records;
    public bool VotgLoader { get; }

    public FileOwners(string game, string? dataRoot)
    {
        _game = game;
        // the public pack app has no data folder: its mods are the installed packs, which OurFiles reads either way
        _ours = SafeSet(() => Votg.OurFiles(dataRoot ?? AppSettings.Dir));
        _votg = SafeSet(() => Votg.BackupList(game) ?? new HashSet<string>());
        _receipt = SafeSet(() => (VotgReceipt.Files(game) ?? new List<string>()).Select(Norm).ToHashSet());
        _records = new(StringComparer.OrdinalIgnoreCase);
        try { foreach (var r in Loader.Mine(game)) _records[Norm(r.Game)] = r; } catch (Exception) { }
        VotgLoader = VotgReceipt.Exists(game);
    }

    static string Norm(string p) => p.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    static HashSet<string> SafeSet(Func<HashSet<string>> f)
    {
        try { return new HashSet<string>(f(), StringComparer.OrdinalIgnoreCase); }
        catch (Exception) { return new(StringComparer.OrdinalIgnoreCase); }
    }

    /// <summary>The owner tag of one game-relative path (any slash style).</summary>
    public string TagOf(string rel)
    {
        var n = Norm(rel);
        if (_records.ContainsKey(n)) return Ours;
        // a file VOTG's receipt names (its Kratos script too) is VOTG's, even when our presets may also write that path
        if (_receipt.Contains(n) || n.StartsWith(@"mod\_votg_")) return VotgTag;
        var file = Path.GetFileName(n);
        if (_ours.Contains(n) || (n.StartsWith(@"mod\") && file.StartsWith("sbg_"))) return Ours;
        if (_votg.Contains(n)) return VotgTag;
        if (n == "winmm.dll") return VotgLoader ? VotgTag : Mio;
        if (MioFiles.Contains(n) || n.StartsWith(@"mod\dump\")) return Mio;
        if (!n.Contains('\\') && (GameRoot.Contains(n) || n.StartsWith("gowr-script-loader"))) return n.StartsWith("gowr-script-loader") ? (VotgLoader ? VotgTag : Unknown) : Game;
        return Unknown;
    }

    /// <summary>The owner of a Lua script named in a loader log line ("@mod\sbg_x.lua", "gameart\...\main.lua").</summary>
    public string TagScript(string script)
    {
        var s = script.TrimStart('@').Trim();
        if (s.StartsWith("gameart", StringComparison.OrdinalIgnoreCase)) s = @"mod\int9\" + s;
        var tag = TagOf(s);
        if (tag == Unknown && s.Contains(@"\int9\gameart\", StringComparison.OrdinalIgnoreCase)) return Game;
        return tag;
    }

    /// <summary>Report lines: counts per tag, then each tag's files (OURS with "matches what we wrote" or "changed since").</summary>
    public List<string> Lines()
    {
        var rows = new List<(string Tag, string Line)>();
        int dumpCount = 0, votgBackupCount = 0;
        try
        {
            foreach (var f in new DirectoryInfo(_game).EnumerateFiles())
            {
                var tag = TagOf(f.Name);
                if (tag == Game) continue;   // the game's own root files are not listed
                rows.Add((tag, $"{f.Name}  ({f.Length:N0} bytes){Extra(f.Name, f)}"));
            }
            var mod = Path.Combine(_game, "mod");
            if (Directory.Exists(mod))
                foreach (var path in Directory.EnumerateFiles(mod, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(_game, path);
                    var n = Norm(rel);
                    if (n.StartsWith(@"mod\dump\")) { dumpCount++; continue; }
                    if (n.StartsWith(Norm(Votg.BackupDir) + "\\")) { votgBackupCount++; continue; }
                    var fi = new FileInfo(path);
                    rows.Add((TagOf(rel), $"{rel}  ({fi.Length:N0} bytes){Extra(rel, fi)}"));
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rows.Add((Unknown, "(could not read the folder: " + ex.Message + ")")); }

        var outp = new List<string>();
        var order = new[] { Ours, VotgTag, Mio, Unknown };
        outp.Add("Counts: " + string.Join(", ", order.Select(t => $"{t} {rows.Count(r => r.Tag == t)}"))
                 + $", MIO dump files {dumpCount}, VOTG backup files {votgBackupCount}");
        foreach (var t in order)
        {
            var list = rows.Where(r => r.Tag == t).Select(r => r.Line).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count == 0) continue;
            outp.Add($"[{t}]");
            foreach (var l in list.Take(MaxListed)) outp.Add("  " + l);
            if (list.Count > MaxListed) outp.Add($"  ... {list.Count - MaxListed} more");
        }
        return outp;
    }

    /// <summary>For a file of our loader records: does it still match the sha we wrote.</summary>
    string Extra(string rel, FileInfo fi)
    {
        if (!_records.TryGetValue(Norm(rel), out var r)) return "";
        try { return r.Matches(Sha(fi.FullName)) ? "  matches what we wrote" : "  CHANGED since we wrote it"; }
        catch (Exception) { return "  (could not hash)"; }
    }

    /// <summary>exec\ files this app has a backup of: same as the backup, or changed (size, and sha when small).</summary>
    public List<string> ChangedGameFiles(string? dataRoot)
    {
        var outp = new List<string>();
        try
        {
            // no data folder = the public pack app: its backups are in the release place (DataRoot.BackupDir of a release)
            var bdir = dataRoot != null ? DataRoot.BackupDir(dataRoot) : Path.Combine(AppSettings.Dir, "originals", "game_files");
            if (!Directory.Exists(bdir)) { outp.Add("(this app has no backup of any game file: none changed by it)"); return outp; }
            foreach (var b in Directory.EnumerateFiles(bdir, "*", SearchOption.AllDirectories).Take(200))
            {
                var rel = Path.GetRelativePath(bdir, b);
                var g = new FileInfo(Path.Combine(_game, rel));
                var bi = new FileInfo(b);
                string state;
                if (!g.Exists) state = "MISSING in the game";
                else if (g.Length != bi.Length) state = $"changed (size {g.Length:N0}, backup {bi.Length:N0})";
                else if (g.Length <= 64L << 20) state = Sha(g.FullName) == Sha(b) ? "same as backup" : "changed (same size)";
                else state = "same size (big file, not hashed)";
                outp.Add($"  {rel}  {state}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { outp.Add("(could not read the backups: " + ex.Message + ")"); }
        if (outp.Count == 0) outp.Add("(no backups)");
        return outp;
    }

    public static string Sha(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
