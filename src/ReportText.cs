using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ModSwitcher2;

/// <summary>
/// "Report a problem" text (2026-11-13): Switcher version, the failed lines of the activity log (they name the mod
/// and its version and hold the error), the last lines of the GOWR MIO log. Personal folders are replaced by placeholders.
/// Pure functions (no UI, no clipboard) so the text can be tested headless (--report-text).
/// </summary>
public static class ReportText
{
    public const string DiscordInvite = "https://discord.gg/3DZ8hHUVvm";   // same invite as the Community row (Edition.SafeUrl allows it)
    public const string Hint = "Copied. Paste it in #bug-reports on Discord.";
    const int ActivityTail = 40, MioTail = 60;
    static readonly string[] FailWords = { "fail", "could not", "error", "not taken", "refused" };

    /// <summary>Replace the game folder, the user's profile folder and the user name with placeholders (both slash styles).</summary>
    public static string Scrub(string text, string? gameDir, string? userDir, string? userName)
    {
        // longest first: the game folder is often inside the user folder or on another drive
        var pairs = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(gameDir)) pairs.Add((gameDir!.TrimEnd('\\', '/'), "<GAME>"));
        if (!string.IsNullOrWhiteSpace(userDir)) pairs.Add((userDir!.TrimEnd('\\', '/'), "<USER>"));
        foreach (var (p, tag) in pairs.Where(x => x.Item1.Length > 0).OrderByDescending(x => x.Item1.Length))
        {
            text = text.Replace(p.Replace("\\", "\\\\"), tag, StringComparison.OrdinalIgnoreCase);   // doubled backslashes (Lua / JSON errors)
            text = text.Replace(p, tag, StringComparison.OrdinalIgnoreCase);
            text = text.Replace(p.Replace('\\', '/'), tag, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrWhiteSpace(userName) && userName!.Length > 2)
            text = text.Replace(userName, "<USER>", StringComparison.OrdinalIgnoreCase);
        return text;
    }

    public static string Build(string appVersion, string activity, IEnumerable<string> mioLines,
                               string? gameDir, string? userDir, string? userName)
    {
        var act = activity.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        var failed = act.Where(l => FailWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).TakeLast(15).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("SBG Mod Switcher problem report");
        sb.AppendLine("Switcher version: " + appVersion);
        sb.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine();
        sb.AppendLine("What failed (mod, version and error, from the Activity log):");
        if (failed.Count == 0) sb.AppendLine("(no failed line in this session)");
        foreach (var l in failed) sb.AppendLine("  " + l.Trim());
        sb.AppendLine();
        sb.AppendLine($"Activity log, last {ActivityTail} lines:");
        foreach (var l in act.TakeLast(ActivityTail)) sb.AppendLine("  " + l.TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"GOWR MIO log, last {MioTail} lines:");
        var mio = mioLines.TakeLast(MioTail).ToList();
        if (mio.Count == 0) sb.AppendLine("(no GOWR_MIO.log found)");
        foreach (var l in mio) sb.AppendLine("  " + l.TrimEnd());
        return Scrub(sb.ToString(), gameDir, userDir, userName);
    }

    /// <summary>Last lines of mod\GOWR_MIO.log. The game may hold the file open, so it is opened shared. Empty on any problem.</summary>
    public static List<string> ReadMioTail(string? gameDir)
    {
        var lines = new List<string>();
        try
        {
            if (string.IsNullOrEmpty(gameDir)) return lines;
            var path = Path.Combine(gameDir, "mod", "GOWR_MIO.log");
            if (!File.Exists(path)) return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs);
            var q = new Queue<string>();
            string? line;
            while ((line = rd.ReadLine()) != null) { q.Enqueue(line); if (q.Count > MioTail) q.Dequeue(); }
            lines.AddRange(q);
        }
        catch (Exception) { }
        return lines;
    }
}
