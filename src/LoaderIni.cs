using System.IO;
using System.Text;

namespace ModSwitcher2;

/// <summary>The MIO ini part of the loader chain: the four switches, set / fix / restore of GOWR_MIO.ini.</summary>
public static partial class Loader
{
    /// <summary>True when the four MIO switches the app needs are on: visualscript and lua under [dump] and [replace].</summary>
    public static bool IniAllOn(string text) =>
        IniKeys.All(k => IniOn(text, k.Section, k.Key));

    static readonly (string Section, string Key)[] IniKeys =
        { ("dump", "visualscript"), ("dump", "lua"), ("replace", "visualscript"), ("replace", "lua") };

    /// <summary>Sets <paramref name="key"/>=1 under [<paramref name="section"/>]: a 0 value is changed in place, a missing key is
    /// added right under the section line, a missing section is added at the end. Every other byte stays (comments, order,
    /// CRLF or LF). Pure text in, text out.</summary>
    public static string IniSet(string text, string section, string key)
    {
        string eol = text.Contains("\r\n") ? "\r\n" : "\n";
        string cr = eol == "\r\n" ? "\r" : "";
        var lines = text.Split('\n').ToList();   // a line keeps its own '\r'
        int head = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (i == 0 && t.StartsWith("ï»¿")) t = t[3..];   // a UTF-8 BOM read as Latin1; the bytes stay
            if (t.Length == 0 || t[0] == ';') continue;
            if (t[0] == '[')
            {
                if (head >= 0) break;                                  // the next section: the key is not in ours
                if (t.Trim('[', ']').Trim().Equals(section, StringComparison.OrdinalIgnoreCase)) head = i;
                continue;
            }
            if (head < 0) continue;
            int eq = lines[i].IndexOf('=');
            if (eq <= 0 || !lines[i][..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = lines[i][(eq + 1)..];
            int semi = rest.IndexOf(';');                                 // an inline comment stays
            var val = semi < 0 ? rest : rest[..semi];
            if (val.Trim() == "1") return text;                          // already on
            int ws = 0;
            while (ws < rest.Length && (rest[ws] == ' ' || rest[ws] == '\t')) ws++;
            // the space before the comment (or the CR) after the value stays
            lines[i] = lines[i][..(eq + 1)] + rest[..ws] + "1" + val[Math.Max(ws, val.TrimEnd().Length)..] + (semi < 0 ? "" : rest[semi..]);
            return string.Join("\n", lines);
        }
        if (head >= 0)
        {
            if (cr.Length > 0 && !lines[head].EndsWith('\r')) lines[head] += "\r";
            bool wasLast = head == lines.Count - 1;                    // header is the last line, no final newline
            lines.Insert(head + 1, key + "=1" + cr);
            if (wasLast) lines.Add("");                                // end with a proper EOL, not a lone CR
            return string.Join("\n", lines);
        }
        var sb = new StringBuilder(text);
        if (text.Length > 0 && !text.EndsWith('\n')) sb.Append(eol);
        sb.Append('[').Append(section).Append(']').Append(eol).Append(key).Append("=1").Append(eol);
        return sb.ToString();
    }

    static string IniPath(string gameRoot) => Path.Combine(gameRoot, Ini);
    static string IniBeside(string gameRoot) => IniPath(gameRoot) + ".sbgbak";
    static string IniCopy(string gameRoot) =>
        Path.Combine(Dir, "ini-" + Sha(Encoding.UTF8.GetBytes(RootKey(gameRoot)))[..10] + ".bak");
    static string IniWritten(string gameRoot) => IniCopy(gameRoot) + ".sha";

    /// <summary>Turns the four MIO switches on (see <see cref="IniSet"/>), after one backup of the player's ini. Returns plain text.</summary>
    public static string IniFix(string gameRoot)
    {
        var path = IniPath(gameRoot);
        if (!File.Exists(path)) return "Install GoWR MIO first";
        var raw = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(raw);     // Latin1 round-trips every byte
        var fixedText = text;
        foreach (var (sec, key) in IniKeys) fixedText = IniSet(fixedText, sec, key);
        if (fixedText == text) return "GoWR MIO is already set";
        if (!File.Exists(IniBeside(gameRoot))) File.WriteAllBytes(IniBeside(gameRoot), raw);   // never over an older backup
        if (!File.Exists(IniCopy(gameRoot)))
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(IniCopy(gameRoot), raw);
        }
        var tmp = path + ".sbgtmp";
        var written = Encoding.Latin1.GetBytes(fixedText);
        File.WriteAllBytes(tmp, written);
        File.Move(tmp, path, overwrite: true);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(IniWritten(gameRoot), Sha(written));   // Restore checks the ini is still ours
        return "GoWR MIO is set: dump and replace are on. Start the game once.";
    }

    /// <summary>True when the app changed the ini and the player's first version is still saved.</summary>
    public static bool IniBackupExists(string gameRoot) => File.Exists(IniBeside(gameRoot)) || File.Exists(IniCopy(gameRoot));

    /// <summary>Puts the player's first ini back (the backup beside it, else the app's copy) and removes the backups. Null = no backup.</summary>
    public static string? IniRestore(string gameRoot)
    {
        var src = File.Exists(IniBeside(gameRoot)) ? IniBeside(gameRoot) : File.Exists(IniCopy(gameRoot)) ? IniCopy(gameRoot) : null;
        if (src == null) return null;
        var mark = IniWritten(gameRoot);
        if (File.Exists(mark) && File.Exists(IniPath(gameRoot))
            && Sha(File.ReadAllBytes(IniPath(gameRoot))) != File.ReadAllText(mark).Trim())
            return "GoWR_MIO.ini changed since Fix it, left as it is";
        File.Copy(src, IniPath(gameRoot), overwrite: true);
        try { File.Delete(IniBeside(gameRoot)); File.Delete(IniCopy(gameRoot)); File.Delete(mark); } catch (IOException) { }
        return "GoWR_MIO.ini is back as it was";
    }

    /// <summary>True when <paramref name="key"/>=1 in [<paramref name="section"/>] (MIO: only 1 is "on"; ; starts a comment).</summary>
    public static bool IniOn(string ini, string section, string key)
    {
        string cur = "";
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[') { cur = line.Trim('[', ']').Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0 || !cur.Equals(section, StringComparison.OrdinalIgnoreCase)) continue;
            if (line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                var v = line[(eq + 1)..]; int semi = v.IndexOf(';');
                return (semi < 0 ? v : v[..semi]).Trim() == "1";
            }
        }
        return false;
    }
}
