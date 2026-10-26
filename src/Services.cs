using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ModSwitcher2;

/// <summary>Per-user settings in %LOCALAPPDATA%\SBGModSwitcher\settings.json.</summary>
public sealed class AppSettings
{
    public string? GamePath { get; set; }
    /// <summary>Version of the disclaimer text the player accepted (0 = never).</summary>
    public int DisclaimerAccepted { get; set; }
    /// <summary>The first-start guide of pack mode was finished or skipped (GuideView.cs).</summary>
    public bool GuideDone { get; set; }
    /// <summary>Show the Lab (experimental tools) at the bottom of Boss Fights.</summary>
    public bool ShowExperimental { get; set; }
    /// <summary>Language of the loadout item list (the game's r_lang_*.wad code), English by default.</summary>
    public string ItemsLanguage { get; set; } = "en";
    /// <summary>The King's Grave figures last applied from this app (the files cannot be read back into the picker).</summary>
    public FiguresChoice? LastFigures { get; set; }

    public static string Dir => DirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SBGModSwitcher");
    /// <summary>Headless tests only (--appdata D): a sandbox instead of the real settings folder.</summary>
    public static string? DirOverride { get; set; }
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception) { /* broken file: start fresh, it is rewritten on the next save */ }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, overwrite: true);
    }
}

public sealed class FiguresChoice
{
    public string V1 { get; set; } = "King";
    public string V2 { get; set; } = "King";
    public string? V3 { get; set; }
    public double D2 { get; set; } = 13;
    public int S2 { get; set; }
    public double D3 { get; set; } = 6;
    public int S3 { get; set; }
}

/// <summary>Finds the game folder: Steam libraries first, then the folders above the app.</summary>
public static class GameLocator
{
    const string SteamRel = @"steamapps\common\God of War Ragnarok";
    public const string ExeName = "GoWR.exe";

    public static bool IsGameDir(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, ExeName));

    /// <summary>Mod loader (MIO): winmm.dll in the game root and mod\GOWR_MIO.ini.</summary>
    public static bool HasModLoader(string dir) =>
        File.Exists(Path.Combine(dir, "winmm.dll")) && File.Exists(Path.Combine(dir, "mod", "GOWR_MIO.ini"));

    public static string? Detect()
    {
        try
        {
            foreach (var lib in SteamLibraries())
            {
                var p = Path.Combine(lib, SteamRel);
                if (IsGameDir(p)) return p;
            }
        }
        catch (Exception) { /* registry or vdf unreadable: fall through */ }

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (IsGameDir(d.FullName)) return d.FullName;
        return null;
    }

    static IEnumerable<string> SteamLibraries()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (steam == null) yield break;
        steam = steam.Replace('/', '\\');
        yield return steam;

        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (var p in ParseLibraryPaths(File.ReadAllText(vdf))) yield return p;
    }

    /// <summary>The "path" values of Steam's libraryfolders.vdf (escaped backslashes undone).</summary>
    public static List<string> ParseLibraryPaths(string vdf) =>
        Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\"").Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    /// <summary>Is the game started from this folder running? A process whose path cannot be read counts as running (safe side).</summary>
    public static bool IsGameRunningIn(string gameDir)
    {
        var want = Path.GetFullPath(Path.Combine(gameDir, ExeName));
        var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName));
        try
        {
            foreach (var p in ps)
            {
                string? path;
                try { path = p.MainModule?.FileName; } catch (Exception) { return true; }
                if (path == null || string.Equals(Path.GetFullPath(path), want, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    public static bool IsGameRunning()
    {
        var ps = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExeName));
        foreach (var p in ps) p.Dispose();
        return ps.Length > 0;
    }
}
