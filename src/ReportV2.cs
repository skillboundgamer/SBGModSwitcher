using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

/// <summary>
/// "Report a problem" v2 (release plan 2.0.5 step 2, research/switcher2/release_plan.md): one plain .txt the player can
/// read first. Versions, loader state, the file owner map, mods, log tails (load noise filtered), settings without
/// paths, the last Apply, and a crash section from the newest game crash dump. Facts only, no verdict text.
/// Scrubbed with <see cref="ReportText.Scrub"/>, capped at ~200 KB. No network code: the button copies the text.
/// Pure reads; build it off the UI thread.
/// </summary>
public static class ReportV2
{
    public const int Cap = 200_000, LineCap = 300;
    const int ActivityTail = 40, MioTail = 60, LoaderTail = 40, CrashLines = 30;
    static readonly string[] FailWords = { "fail", "could not", "error", "not taken", "refused" };
    static readonly Regex MioNoise = new(@"^(VS|LUA): (loading|dumping) file ", RegexOptions.Compiled);
    static readonly Regex MioLuaLoad = new(@"^LUA: loading file ""(?<f>[^""]+)""", RegexOptions.Compiled);
    static readonly Regex Stamp = new(@"^\[(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)(\.\d+)?\] \[(?<src>[^\]]+)\] \[(?<lvl>[^\]]+)\] (?<msg>.*)$", RegexOptions.Compiled);
    static readonly Regex ScriptLoad = new(@"load \(script\): (?<f>@?\S+)", RegexOptions.Compiled);

    public sealed record Input(string AppVersion, string Activity, string? Game, string? DataRoot,
                               string? UserDir, string? UserName, AppSettings? Settings);

    public static string Build(Input i)
    {
        var sb = new StringBuilder();
        var game = string.IsNullOrWhiteSpace(i.Game) ? null : i.Game!.TrimEnd('\\', '/');
        bool found = GameLocator.IsGameDir(game);
        var act = i.Activity.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        FileOwners? owners = found ? new FileOwners(game!, i.DataRoot) : null;

        sb.AppendLine("SBG Mod Switcher problem report (v2)");
        sb.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        Section(sb, "Versions");
        sb.AppendLine("  Switcher: " + i.AppVersion);
        sb.AppendLine("  Windows: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        sb.AppendLine("  Game folder found: " + (found ? "yes" : "no"));
        if (found) sb.AppendLine("  Game exe: " + ExeText(Path.Combine(game!, GameLocator.ExeName)));

        Section(sb, "What failed (from the Activity log)");
        var failed = act.Where(l => FailWords.Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).TakeLast(15).ToList();
        if (failed.Count == 0) sb.AppendLine("  (no failed line in this session)");
        foreach (var l in failed) sb.AppendLine("  " + l.Trim());
        Section(sb, "Last Apply");
        var apply = act.Where(l => l.Contains("Apply", StringComparison.OrdinalIgnoreCase) || l.Contains("applied", StringComparison.OrdinalIgnoreCase)).TakeLast(8).ToList();
        if (apply.Count == 0) sb.AppendLine("  (no Apply in this session)");
        foreach (var l in apply) sb.AppendLine("  " + l.Trim());

        if (found)
        {
            Section(sb, "Crash (newest game crash dump)");
            foreach (var l in CrashLinesFor(game!, owners!)) sb.AppendLine("  " + l);
            Section(sb, "Loader state");
            foreach (var l in LoaderLines(game!)) sb.AppendLine("  " + l);
            Section(sb, "File owner map (game root and mod folder)");
            foreach (var l in owners!.Lines()) sb.AppendLine("  " + l);
            Section(sb, "Game files this app has a backup of");
            foreach (var l in owners.ChangedGameFiles(i.DataRoot)) sb.AppendLine(l);
        }

        Section(sb, "Mods");
        foreach (var l in ModLines()) sb.AppendLine("  " + l);
        Section(sb, "Settings");
        sb.AppendLine("  " + SettingsText(i.Settings));

        Section(sb, $"Activity log, last {ActivityTail} lines");
        foreach (var l in act.TakeLast(ActivityTail)) sb.AppendLine("  " + l.TrimEnd());
        if (found)
        {
            var mio = ReadLines(Path.Combine(game!, "mod", "GOWR_MIO.log"));
            Section(sb, $"GOWR MIO log ({mio.Count} lines; file load lines left out), last {MioTail}");
            if (mio.Count == 0) sb.AppendLine("  (no GOWR_MIO.log found)");
            foreach (var l in FilterMio(mio).TakeLast(MioTail)) sb.AppendLine("  " + l);
            var sl = ReadLines(Path.Combine(game!, "GOWR-Script-Loader.log"));
            if (sl.Count > 0)
            {
                Section(sb, $"Script loader log (GOWR-Script-Loader.log, {sl.Count} lines), warnings, errors and failed lines, last {LoaderTail}");
                var bad = sl.Where(l => !l.Contains("] [info] ") || FailWords.Concat(new[] { "not loaded", "cannot" }).Any(w => l.Contains(w, StringComparison.OrdinalIgnoreCase))).TakeLast(LoaderTail).ToList();
                if (bad.Count == 0) sb.AppendLine("  (none)");
                foreach (var l in bad) sb.AppendLine("  " + l);
                Section(sb, "Script loader log, last 15 lines");
                foreach (var l in sl.TakeLast(15)) sb.AppendLine("  " + l);
            }
        }

        // one very long log line (a loader's list of every loaded wad) must not fill the report: cut each line at 300
        // scrub first: a cut inside a path must never leave part of a user or folder name
        var scrubbed = ReportText.Scrub(sb.ToString(), game, i.UserDir, i.UserName);
        var text = string.Join(Environment.NewLine, scrubbed.Replace("\r", "").Split('\n')
                                 .Select(l => l.Length > LineCap ? l[..LineCap] + " …" : l));
        if (text.Length > Cap) text = text[..Cap] + Environment.NewLine + "(report cut at 200 KB)" + Environment.NewLine;
        return text;
    }

    static void Section(StringBuilder sb, string title) { sb.AppendLine(); sb.AppendLine("== " + title + " =="); }

    /// <summary>MIO log lines without the per-file "loading / dumping" noise; the count of what was left out goes first.</summary>
    public static List<string> FilterMio(List<string> lines)
    {
        int noise = lines.Count(l => MioNoise.IsMatch(l));
        var keep = lines.Where(l => !MioNoise.IsMatch(l)).Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        keep.Insert(0, $"({noise} file load lines left out)");
        return keep;
    }

    static string ExeText(string exe)
    {
        try
        {
            var fi = new FileInfo(exe);
            var ver = FileVersionInfo.GetVersionInfo(exe).FileVersion;
            var vt = string.IsNullOrWhiteSpace(ver) || ver.Replace("0", "").Replace(",", "").Replace(".", "").Trim() == "" ? "" : $"version {ver}, ";
            return $"{vt}{fi.Length:N0} bytes, build stamp {PeStamp(exe)}";
        }
        catch (Exception ex) { return "(could not read: " + ex.Message + ")"; }
    }

    /// <summary>The PE header TimeDateStamp of an exe or dll, as a UTC date (identifies the game build).</summary>
    public static string PeStamp(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var b = new byte[4096];
        int n = fs.Read(b, 0, b.Length);
        int pe = BitConverter.ToInt32(b, 0x3C);
        if (pe <= 0 || pe + 12 > n || b[pe] != 'P' || b[pe + 1] != 'E') return "?";
        uint ts = BitConverter.ToUInt32(b, pe + 8);
        return $"{DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime:yyyy-MM-dd HH:mm} UTC (0x{ts:x8})";
    }

    static List<string> LoaderLines(string game)
    {
        var o = new List<string>();
        try
        {
            var winmm = Path.Combine(game, "winmm.dll");
            o.Add("MIO (winmm.dll + mod\\GOWR_MIO.ini): " + (GameLocator.HasModLoader(game) ? "yes" : "no"));
            o.Add(File.Exists(winmm) ? $"winmm.dll: {new FileInfo(winmm).Length:N0} bytes, sha256 {FileOwners.Sha(winmm)[..16]}" : "winmm.dll: none");
            var ini = Path.Combine(game, Loader.Ini);
            if (File.Exists(ini))
            {
                var t = File.ReadAllText(ini);
                o.Add($"MIO switches: dump lua={On(t, "dump", "lua")} vs={On(t, "dump", "visualscript")}, replace lua={On(t, "replace", "lua")} vs={On(t, "replace", "visualscript")}");
            }
            var v = VotgReceipt.Read(game);
            bool votg = VotgReceipt.Exists(game);
            o.Add("VOTG receipt: " + (!votg ? "no" : v == null ? "yes (could not read it)"
                : $"yes, mod {(v.ModVersion == "" ? "?" : v.ModVersion)}, payload {Short(v.Payload)}, loader {Short(v.LoaderSha)}, tested with this Switcher: {(v.Tested ? "yes" : "no")}"));
            o.Add("VOTG backup folder (mod\\_votg_backup): " + (Directory.Exists(Path.Combine(game, Votg.BackupDir)) ? "yes" : "no"));
            var sl = Path.Combine(game, "GOWR-Script-Loader.ini");
            o.Add("Script loader ini (GOWR-Script-Loader.ini): " + (File.Exists(sl) ? "yes" : "no"));
            var mine = Loader.Mine(game);
            o.Add($"Our loader files (records of this app): {mine.Count}");
            foreach (var r in mine)
            {
                var p = Path.Combine(game, r.Game);
                string state = !File.Exists(p) ? "MISSING" : r.Matches(FileOwners.Sha(p)) ? "matches" : "CHANGED";
                o.Add($"  {r.Game}  sha256 {Short(r.Sha)}  {state}{(r.Backup != null ? ", backup kept" : "")}");
            }
            o.Add("Our Kratos stub wanted: " + (Loader.StubWanted(game) ? "yes" : "no (hook mode)"));
            o.Add("Loader problem: " + (Loader.Problem(game) ?? "none"));
        }
        catch (Exception ex) { o.Add("(could not read the loader state: " + ex.Message + ")"); }
        return o;
    }

    static string On(string ini, string sec, string key) => Loader.IniOn(ini, sec, key) ? "1" : "0";
    static string Short(string s) => s.Length > 16 ? s[..16] : s == "" ? "?" : s;

    static List<string> ModLines()
    {
        var o = new List<string>();
        try
        {
            var errs = new List<string>();
            var packs = Packs.List(errs);
            o.Add($"Installed packs: {packs.Count}");
            foreach (var p in packs) o.Add($"  {p.Info.Id}  version {p.Info.Version}  ({p.Info.Groups.Count} mod{(p.Info.Groups.Count == 1 ? "" : "s")}: {string.Join(", ", p.Info.Groups)})");
            foreach (var e in errs) o.Add("  pack problem: " + e);
        }
        catch (Exception ex) { o.Add("(could not read the packs: " + ex.Message + ")"); }
        o.Add("On / off per mod: see the Activity lines and the owner map (OURS files in the game = applied).");
        return o;
    }

    static string SettingsText(AppSettings? s)
    {
        if (s == null) return "(none)";
        try
        {
            var node = JsonSerializer.SerializeToNode(s)!.AsObject();
            node.Remove(nameof(AppSettings.GamePath));   // no paths
            return node.ToJsonString();
        }
        catch (Exception ex) { return "(could not read: " + ex.Message + ")"; }
    }

    /// <summary>All lines of a log the game may still hold open (shared read). Empty on any problem.</summary>
    public static List<string> ReadLines(string path)
    {
        var lines = new List<string>();
        try
        {
            if (!File.Exists(path)) return lines;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var rd = new StreamReader(fs);
            string? line;
            while ((line = rd.ReadLine()) != null) lines.Add(line);
        }
        catch (Exception) { }
        return lines;
    }

    /// <summary>The crash section: dump facts, stack modules with owner tags, a known-address fact, the loader's own
    /// crash report when it is from the same time, the loader log lines before the crash and the files loaded last.</summary>
    public static List<string> CrashLinesFor(string game, FileOwners owners)
    {
        var o = new List<string>();
        var dmp = CrashInfo.Newest(game);
        if (dmp == null) { o.Add("(no crash dump in .crashdata\\reports)"); return o; }
        DateTime when = dmp.LastWriteTime;
        try
        {
            var r = CrashInfo.Read(dmp.FullName, p => CrashInfo.TagModule(p, game, owners.VotgLoader));
            when = r.Time;
            o.Add($"Dump: {r.File}  time {r.Time:yyyy-MM-dd HH:mm:ss}  ({dmp.Length:N0} bytes, {CrashAge(r.Time)})");
            o.Add($"Exception 0x{r.Code:X8} at {r.Where}");
            if (r.AccessText != null) o.Add(r.AccessText);
            if (r.Known != null) o.Add(r.Known);
            o.Add("Modules on the crashing stack (hits, owner):");
            foreach (var (m, tag, n) in r.StackModules.Take(15)) o.Add($"  {m}  {n}  {tag}");
        }
        catch (Exception ex) { o.Add($"Dump: {dmp.Name}  (could not read: {ex.Message})"); }

        // the script loader's own crash report (VOTG loader), newest block, when it is within 10 min of the dump
        var crashLog = ReadLines(Path.Combine(game, "GOWR-Script-Loader-Crash.log"));
        int start = crashLog.FindLastIndex(l => l.StartsWith("====") && l.Contains("crash report"));
        if (start >= 0)
        {
            var block = crashLog.Skip(start).ToList();
            var tline = block.FirstOrDefault(l => l.StartsWith("Time: ")) ?? "";
            bool near = DateTime.TryParseExact(tline.Length >= 25 ? tline.Substring(6, 19) : "", "yyyy-MM-dd HH:mm:ss",
                                               CultureInfo.InvariantCulture, DateTimeStyles.None, out var bt)
                        && Math.Abs((bt - when).TotalMinutes) <= 10;
            o.Add("");
            o.Add("Script loader crash report (GOWR-Script-Loader-Crash.log), newest block, "
                  + (near ? "same time as the dump:" : "NOT the same time as the dump (another crash or a first-chance report):"));
            foreach (var l in block.Skip(1).TakeWhile(l => !l.StartsWith("Registers:"))) o.Add("  " + l.Trim());
            int st = block.FindIndex(l => l.StartsWith("Stack:"));
            if (st >= 0) foreach (var l in block.Skip(st).TakeWhile(l => !l.StartsWith("Last ")).Take(20)) o.Add("  " + l.Trim());
            int ev = block.FindIndex(l => l.StartsWith("Last ") && l.Contains("events"));
            if (ev >= 0)
            {
                o.Add("  Last events before the crash (owner tag first):");
                foreach (var l in block.Skip(ev + 1).Where(l => l.Trim().Length > 0).TakeLast(CrashLines)) o.Add("    " + TagLine(l.Trim(), owners));
            }
        }

        // loader log lines (with time stamps) up to the crash time, from this run's or the previous run's log
        var before = new List<string>();
        foreach (var name in new[] { "GOWR-Script-Loader.log", "GOWR-Script-Loader.previous.log" })
        {
            var lines = ReadLines(Path.Combine(game, name));
            var hit = lines.Where(l => Stamp.Match(l) is { Success: true } m
                                       && DateTime.TryParseExact(m.Groups["t"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                                       && t <= when.AddSeconds(2) && t >= when.AddMinutes(-10)).ToList();
            if (hit.Count == 0) continue;
            before.Add($"From {name}: last {Math.Min(CrashLines, hit.Count)} lines up to the crash time (owner tag first):");
            foreach (var l in hit.TakeLast(CrashLines)) before.Add("  " + TagLine(l, owners));
            break;
        }
        o.Add("");
        if (before.Count == 0) o.Add("Script loader log: no lines from the 10 minutes before the crash (log of a later run, or no script loader).");
        o.AddRange(before);

        // files loaded last according to the MIO log (only meaningful when the log is from the crashed run)
        var mio = ReadLines(Path.Combine(game, "mod", "GOWR_MIO.log"));
        var loads = mio.Select(l => MioLuaLoad.Match(l)).Where(m => m.Success).Select(m => m.Groups["f"].Value).TakeLast(10).ToList();
        if (loads.Count > 0)
        {
            var mioTime = SafeTime(Path.Combine(game, "mod", "GOWR_MIO.log"));
            o.Add("");
            o.Add($"Lua files loaded last (GOWR_MIO.log, written {mioTime:yyyy-MM-dd HH:mm}{(mioTime < when.AddMinutes(-10) || mioTime > when.AddMinutes(10) ? ", NOT the crash run" : "")}):");
            foreach (var f in loads) o.Add($"  {owners.TagScript(f),-7} {f}");
        }
        return o;
    }

    static DateTime SafeTime(string p) { try { return File.GetLastWriteTime(p); } catch (Exception) { return DateTime.MinValue; } }

    static string CrashAge(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago" : d.TotalDays < 2 ? $"{(int)d.TotalHours} h ago" : $"{(int)d.TotalDays} days ago";
    }

    /// <summary>One log line with an owner tag in front: the script of a "load (script)" line, OURS for an [SBG] line,
    /// "lua" for other script prints, "loader" for the loader's own lines.</summary>
    public static string TagLine(string line, FileOwners owners)
    {
        string tag;
        var m = ScriptLoad.Match(line);
        if (m.Success) tag = owners.TagScript(m.Groups["f"].Value);
        else if (line.Contains("[SBG]")) tag = FileOwners.Ours;
        else if (line.Contains("[lua]") || line.Contains("print:")) tag = "lua";
        else tag = "loader";
        return $"{tag,-7} {line}";
    }
}
