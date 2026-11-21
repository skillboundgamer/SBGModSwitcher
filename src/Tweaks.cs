using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

/// <summary>
/// The Game Tweaks of the old Switcher (FovGet / FovSet, BarNamesGet / BarNamesSet, MsgsGet / MsgsSet in
/// switcher/ModSwitcher.ps1), same files, same rules, same bytes:
///  - camera FOV: [tweeks] fov_angle in mod\GOWR_MIO.ini (50..120, a ';' in front = off), read at game start;
///  - boss bar names: local BAR_NAMES = "..." in mod\live.lua ("" = game default, # = boss number), hot reload;
///  - on-screen messages: local SHOW_MSGS = true|false in mod\live.lua, hot reload.
/// </summary>
public static class Tweaks
{
    public const string MioIni = @"mod\GOWR_MIO.ini";
    public const string Live = @"mod\live.lua";
    public const int FovMin = 50, FovMax = 120, NameMax = 40;
    static readonly UTF8Encoding NoBom = new(false);

    // ---------- camera FOV ----------

    public static (bool On, int Deg) FovGet(string gameRoot)
    {
        // every fov line is read: the last active one wins (as FovActiveGet), else the first commented value is shown as off
        int? off = null;
        (bool, int)? on = null;
        foreach (var l in File.ReadAllLines(Path.Combine(gameRoot, MioIni)))
        {
            var m = Regex.Match(l, @"^\s*(;?)\s*fov_angle\s*=\s*(\d+)", RegexOptions.IgnoreCase); // PS -match ignores case
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out int d)) continue;
            if (m.Groups[1].Value == "" && d > 0) on = (true, d);
            else off ??= d;
        }
        return on ?? (false, off ?? 90);
    }

    /// <summary>Returns true when the file changed (full game restart needed).</summary>
    public static bool FovSet(string gameRoot, bool on, int deg, Func<bool> gameRunning)
    {
        if (deg < FovMin || deg > FovMax) throw new ArgumentException($"FOV must be {FovMin}..{FovMax} (got {deg})");
        var ini = Path.Combine(gameRoot, MioIni);
        var cur = FovGet(gameRoot);
        var text = File.ReadAllText(ini);
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string line = on ? $"fov_angle={deg}" : $";fov_angle={deg}";
        var rx = new Regex(@"(?m)^[ \t]*;?[ \t]*fov_angle[ \t]*=[ \t]*\d+[ \t]*\r?$");
        if (rx.IsMatch(text)) text = rx.Replace(text, _ => line, 1);
        else if (Regex.IsMatch(text, @"(?m)^\[tweeks\][ \t]*\r?$", RegexOptions.IgnoreCase))
            text = new Regex(@"(?m)^(\[tweeks\][ \t]*)\r?$", RegexOptions.IgnoreCase).Replace(text, m => m.Groups[1].Value + nl + line, 1);
        else text = text.TrimEnd() + $"{nl}[tweeks]{nl}{line}{nl}";

        bool changed = cur.On != on || cur.Deg != deg;
        if (changed)
        {
            if (gameRunning()) throw new InvalidOperationException("Close the game first: the FOV is read at game start (nothing was changed)");
            var bak = ini + ".sbg_original";
            if (!File.Exists(bak)) File.Copy(ini, bak);
            File.WriteAllText(ini, text, NoBom);
        }
        var chk = FovGet(gameRoot);
        if (chk.On != on || chk.Deg != deg) throw new IOException("FOV write verify failed");
        return changed;
    }

    // ---------- camera FOV: reset (first open, Game Tweaks) ----------

    static readonly Regex FovLineRx = new(@"^([ \t]*)(fov_angle[ \t]*=[ \t]*)(\d*)", RegexOptions.IgnoreCase);

    /// <summary>The custom FOV MIO would apply (fov_angle active and above 0, any value), or null = the game's own.</summary>
    public static int? FovActiveGet(string gameRoot)
    {
        var ini = Path.Combine(gameRoot, MioIni);
        if (!File.Exists(ini)) return null;
        int? found = null;
        foreach (var l in File.ReadAllLines(ini, Encoding.Latin1))
        {
            var m = FovLineRx.Match(l);
            if (m.Success && int.TryParse(m.Groups[3].Value, out int v) && v > 0) found = v;
        }
        return found;
    }

    /// <summary>
    /// Puts a ';' in front of every active fov_angle line (the value stays). One backup beside the ini, never over an older
    /// one; bytes elsewhere are kept (Latin1 round trip). Returns true when the file changed (full game restart needed).
    /// </summary>
    public static bool FovReset(string gameRoot, Func<bool> gameRunning)
    {
        var ini = Path.Combine(gameRoot, MioIni);
        if (FovActiveGet(gameRoot) == null) return false;
        if (gameRunning()) throw new InvalidOperationException("Close the game first: the FOV is read at game start (nothing was changed)");
        var raw = File.ReadAllBytes(ini);
        var lines = Encoding.Latin1.GetString(raw).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = FovLineRx.Match(lines[i]);
            if (m.Success && int.TryParse(m.Groups[3].Value, out int v) && v > 0) lines[i] = ";" + lines[i];
        }
        var bak = ini + ".sbg_original";
        if (!File.Exists(bak)) File.WriteAllBytes(bak, raw);
        var tmp = ini + ".sbgtmp";
        File.WriteAllBytes(tmp, Encoding.Latin1.GetBytes(string.Join("\n", lines)));
        File.Move(tmp, ini, overwrite: true);
        if (FovActiveGet(gameRoot) != null) throw new IOException("FOV write verify failed");
        return true;
    }

    // ---------- walk speed (reset only) ----------

    /// <summary>MIO's hero_speed range: anything else (0, empty, out of range) is the game's own speed.</summary>
    public const int SpeedMin = 10, SpeedMax = 20;
    static readonly Regex SpeedLineRx = new(@"^([ \t]*)(hero_speed[ \t]*=[ \t]*)(\d*)", RegexOptions.IgnoreCase);

    /// <summary>The custom walk speed MIO would apply (hero_speed active, 10..20), or null = the game's own speed.</summary>
    public static int? SpeedGet(string gameRoot)
    {
        var ini = Path.Combine(gameRoot, MioIni);
        if (!File.Exists(ini)) return null;
        int? found = null;
        foreach (var l in File.ReadAllLines(ini, Encoding.Latin1))
        {
            var m = SpeedLineRx.Match(l);
            if (m.Success && int.TryParse(m.Groups[3].Value, out int v) && v >= SpeedMin && v <= SpeedMax) found = v;
        }
        return found;
    }

    /// <summary>
    /// Puts a ';' in front of every active hero_speed line (the player's own line stays, only commented out). One backup
    /// beside the ini, never over an older one. Bytes elsewhere are kept as they are (Latin1 round trip).
    /// Returns true when the file changed (full game restart needed). The app never sets a speed: this is reset only.
    /// </summary>
    public static bool SpeedReset(string gameRoot, Func<bool> gameRunning)
    {
        var ini = Path.Combine(gameRoot, MioIni);
        if (SpeedGet(gameRoot) == null) return false;
        if (gameRunning()) throw new InvalidOperationException("Close the game first: the speed is read at game start (nothing was changed)");
        var raw = File.ReadAllBytes(ini);
        var lines = Encoding.Latin1.GetString(raw).Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = SpeedLineRx.Match(lines[i]);
            if (m.Success && int.TryParse(m.Groups[3].Value, out int v) && v >= SpeedMin && v <= SpeedMax)
                lines[i] = ";" + lines[i];
        }
        var bak = ini + ".sbg_original";
        if (!File.Exists(bak)) File.WriteAllBytes(bak, raw);
        var tmp = ini + ".sbgtmp";
        File.WriteAllBytes(tmp, Encoding.Latin1.GetBytes(string.Join("\n", lines)));
        File.Move(tmp, ini, overwrite: true);
        if (SpeedGet(gameRoot) != null) throw new IOException("speed write verify failed");
        return true;
    }

    // ---------- boss bar names ----------

    static readonly Regex NamesRx = new("(?m)^local BAR_NAMES = \"((?:[^\"\\\\]|\\\\.)*)\"");

    /// <summary>null = live.lua missing or without the BAR_NAMES line (needs live v22+).</summary>
    public static string? BarNamesGet(string gameRoot)
    {
        var p = Path.Combine(gameRoot, Live);
        if (!File.Exists(p)) return null;
        var m = NamesRx.Match(File.ReadAllText(p));
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
    }

    public static void BarNamesSet(string gameRoot, string text)
    {
        var p = Path.Combine(gameRoot, Live);
        if (!File.Exists(p)) throw new FileNotFoundException("missing " + Live);
        if (text.Length > NameMax) throw new ArgumentException($"bar name: {NameMax} characters max");
        var esc = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var src = File.ReadAllText(p);
        var rx = new Regex("(?m)^local BAR_NAMES = \"(?:[^\"\\\\]|\\\\.)*\"");
        if (!rx.IsMatch(src)) throw new InvalidDataException("live.lua has no BAR_NAMES line (needs live v22+)");
        File.WriteAllText(p, rx.Replace(src, _ => "local BAR_NAMES = \"" + esc + "\"", 1), NoBom);
        if (BarNamesGet(gameRoot) != text) throw new IOException("live.lua write verify failed");
    }

    // ---------- on-screen messages ----------

    static readonly Regex MsgsRx = new(@"(?m)^local SHOW_MSGS = (true|false)");

    /// <summary>null = live.lua missing or without the SHOW_MSGS line (needs live v25+).</summary>
    public static bool? MsgsGet(string gameRoot)
    {
        var p = Path.Combine(gameRoot, Live);
        if (!File.Exists(p)) return null;
        var m = MsgsRx.Match(File.ReadAllText(p));
        return m.Success ? m.Groups[1].Value == "true" : null;
    }

    public static void MsgsSet(string gameRoot, bool on)
    {
        var p = Path.Combine(gameRoot, Live);
        var src = File.ReadAllText(p);
        if (!MsgsRx.IsMatch(src)) throw new InvalidDataException("live.lua has no SHOW_MSGS line (needs live v25+)");
        File.WriteAllText(p, MsgsRx.Replace(src, "local SHOW_MSGS = " + (on ? "true" : "false"), 1), NoBom);
        if (MsgsGet(gameRoot) != on) throw new IOException("live.lua write verify failed");
    }
}
