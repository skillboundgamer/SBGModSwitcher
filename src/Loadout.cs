using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

/// <summary>One line of switcher\valhalla_items.tsv: internal name, kind, name in game, description.</summary>
public sealed record LoadoutItem(string Name, string Kind, string Label, string Desc);

/// <summary>What mod\sbg_loadout_cfg.lua holds now.</summary>
public sealed class LoadoutState
{
    public bool Exists, On;
    public int Rev;
    public List<string> Glyphs = new(), Stats = new(), Drop = new();
    /// <summary>Burden glyphs: the plug-in puts them in Kratos' Talisman GlyphSlot1-3 (cfg field "burdens", only written when not empty).</summary>
    public List<string> Burdens = new();
    /// <summary>Penalty of Breaching is in use: the game takes one glyph slot after the door, so at most 14 glyphs (cfg line "breaching = true", only written when true).</summary>
    public bool Breaching;
    public Dictionary<string, string> Slots = new();
}

/// <summary>The loadout plug-in in the game against the app's copy. Game is null when mod\sbg_loadout.lua is missing.</summary>
public sealed record LoadoutCode(string App, string? Game, bool AppExists, bool Same);

/// <summary>
/// The Valhalla loadout (glyphs, runic attacks, relic), the same files and format as the "Valhalla loadout" tab of
/// switcher/ModSwitcher.ps1 (LoadoutItems / LoadoutRead / LoadoutEnsureStub / LoadoutWrite). The game side is
/// mod\sbg_loadout.lua (the code) + mod\sbg_loadout_cfg.lua (the choice), hot reloaded every ~2 s: no restart.
/// </summary>
public static class Loadout
{
    public const string CodeSrc = @"versions\loadout_v05.lua";
    public const string CodeGame = @"mod\sbg_loadout.lua";
    public const string CfgGame = @"mod\sbg_loadout_cfg.lua";
    public const string LiveGame = @"mod\live.lua";
    public const string ItemsSrc = @"switcher\valhalla_items.tsv";
    public const string StubSrc = @"switcher\loadout_stub.lua";
    public const int MaxGlyphs = 15, MaxStats = 32, MaxBurdens = 3, MaxGlyphsBreaching = 14;

    /// <summary>Equipment slot -> label, in the old app's order; an item's kind for a slot is the slot name.</summary>
    public static readonly (string Slot, string Label)[] Slots =
    {
        ("WeaponSpecial_AxeLight", "Axe, light runic"), ("WeaponSpecial_AxeHeavy", "Axe, heavy runic"),
        ("WeaponSpecial_BladesLight", "Blades, light runic"), ("WeaponSpecial_BladesHeavy", "Blades, heavy runic"),
        ("WeaponSpecial_SpearLight", "Spear, light runic"), ("WeaponSpecial_SpearHeavy", "Spear, heavy runic"),
        ("SwordHilt", "Relic"),
    };

    /// <summary>Glyph kinds (the tsv's kind column) in the old app's order. "Stat" glyphs go in their own list.</summary>
    public static readonly string[] GlyphKinds = { "Perk", "Axe", "Blades", "Spear", "Stat" };

    /// <summary>Items from the dev copy in the data root (switcher\valhalla_items.tsv).</summary>
    public static List<LoadoutItem> Items(string dataRoot) => ItemsFrom(Path.Combine(dataRoot, ItemsSrc));

    /// <summary>Items from a tsv: the one built on this PC (phase 8b) or the dev copy.</summary>
    public static List<LoadoutItem> ItemsFrom(string p)
    {
        var list = new List<LoadoutItem>();
        if (!File.Exists(p)) return list;
        foreach (var line in File.ReadAllLines(p))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var f = line.Split('\t');
            if (f.Length < 3) continue;
            list.Add(new LoadoutItem(f[0], f[1], f[2], f.Length > 3 ? f[3] : ""));
        }
        return list;
    }

    /// <summary>
    /// Read only: is mod\sbg_loadout.lua the same file as the app's copy (CodeSrc)? Byte compare; the shown version is
    /// the M.version = "..." line of each file ("?" when there is none). The old Switcher can put an old copy back.
    /// </summary>
    public static LoadoutCode Code(string gameRoot, string dataRoot)
    {
        var src = Path.Combine(dataRoot, CodeSrc);
        var game = Path.Combine(gameRoot, CodeGame);
        var a = File.Exists(src) ? File.ReadAllBytes(src) : null;
        var g = File.Exists(game) ? File.ReadAllBytes(game) : null;
        return new LoadoutCode(VersionOf(a), g == null ? null : VersionOf(g), a != null,
                               a != null && g != null && a.AsSpan().SequenceEqual(g));
    }

    static string VersionOf(byte[]? b)
    {
        if (b == null) return "?";
        var m = Regex.Match(Encoding.UTF8.GetString(b), "(?m)^\\s*M\\.version\\s*=\\s*\"([^\"\\r\\n]*)\"");
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : "?";
    }

    public static LoadoutState Read(string gameRoot)
    {
        var r = new LoadoutState();
        var p = Path.Combine(gameRoot, CfgGame);
        if (!File.Exists(p)) return r;
        var s = File.ReadAllText(p);
        r.Exists = true;
        var m = Regex.Match(s, @"(?m)^\s*on = (true|false)");
        if (m.Success) r.On = m.Groups[1].Value == "true";
        m = Regex.Match(s, @"(?m)^\s*rev = (\d+)");
        if (m.Success) r.Rev = int.Parse(m.Groups[1].Value);
        r.Glyphs = List(s, "glyphs");
        r.Stats = List(s, "stats");
        r.Drop = List(s, "drop");
        r.Burdens = List(s, "burdens");
        r.Breaching = Regex.IsMatch(s, @"(?m)^\s*breaching = true");
        m = Regex.Match(s, @"(?s)\bslots = \{(.*?)\}");
        if (m.Success)
            foreach (Match x in Regex.Matches(m.Groups[1].Value, "(\\w+) = \"([^\"]+)\""))
                r.Slots[x.Groups[1].Value] = x.Groups[2].Value;
        return r;
    }

    static List<string> List(string s, string key)
    {
        var m = Regex.Match(s, @"(?s)\b" + key + @" = \{(.*?)\}");
        return m.Success
            ? Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToList()
            : new List<string>();
    }

    /// <summary>Writes the choice (and the code / loader when missing). Returns log lines. No restart needed.</summary>
    public static List<string> Write(string gameRoot, string dataRoot, bool on,
                                     IList<string> glyphs, IList<string> stats, IDictionary<string, string> slots,
                                     IList<string>? burdens = null, bool breaching = false)
    {
        var log = new List<string>();
        glyphs = glyphs.Where(n => n.Length > 0).ToList();
        stats = stats.Where(n => n.Length > 0).ToList();
        burdens = (burdens ?? new List<string>()).Where(n => n.Length > 0).ToList();
        foreach (var n in glyphs.Concat(stats).Concat(burdens).Concat(slots.Values.Where(v => v.Length > 0)))
            if (!Regex.IsMatch(n, "^[A-Za-z0-9_]+$")) throw new ArgumentException($"bad item name '{n}'");
        if (breaching && glyphs.Count > MaxGlyphsBreaching)
            throw new ArgumentException($"at most {MaxGlyphsBreaching} glyphs while Penalty of Breaching is on (you chose {glyphs.Count})");
        if (glyphs.Count > MaxGlyphs) throw new ArgumentException($"at most {MaxGlyphs} glyphs fit (you chose {glyphs.Count})");
        if (stats.Count > MaxStats) throw new ArgumentException($"at most {MaxStats} stat glyphs fit");
        if (burdens.Count > MaxBurdens) throw new ArgumentException($"at most {MaxBurdens} burden glyphs fit (you chose {burdens.Count})");
        var dup = burdens.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(x => x.Count() > 1);
        if (dup != null) throw new ArgumentException($"burden glyph '{dup.Key}' is chosen twice");
        var both = burdens.FirstOrDefault(n => glyphs.Concat(stats).Contains(n, StringComparer.OrdinalIgnoreCase));
        if (both != null) throw new ArgumentException($"'{both}' is in the glyph list and in the burden glyphs: a glyph can only be in one of them");

        // On: the loader chain first (Loader.cs; refuses with a plain reason when the player's MIO cannot run it).
        // A live.lua the loader block cannot go into is refused BEFORE anything is written.
        // Off with no live.lua at all: nothing runs the plug-in, so no loader block (the plug-in and the choice still go in).
        // Only a live.lua of ours ("SBG") is kept by Install; another mod's live.lua is backed up and replaced instead.
        var liveNow = File.Exists(Path.Combine(gameRoot, LiveGame)) ? File.ReadAllText(Path.Combine(gameRoot, LiveGame)) : null;
        if (on && liveNow != null && liveNow.Contains("SBG", StringComparison.Ordinal) && StubBlocked(liveNow))
            throw new InvalidDataException(NoAnchor);
        foreach (var need in new[] { CodeSrc, StubSrc })                                                      // before any write
            if (!File.Exists(Path.Combine(dataRoot, need))) throw new FileNotFoundException("missing " + need);
        if (on) log.AddRange(Loader.Install(gameRoot, dataRoot));
        if ((on || File.Exists(Path.Combine(gameRoot, LiveGame))) && EnsureStub(gameRoot, dataRoot))
            log.Add(@"mod\live.lua: loadout loader added (the installed live.lua did not have it)");

        var codeSrc = Path.Combine(dataRoot, CodeSrc);
        var codeGame = Path.Combine(gameRoot, CodeGame);
        if (!File.Exists(codeGame) || !File.ReadAllBytes(codeSrc).AsSpan().SequenceEqual(File.ReadAllBytes(codeGame)))
        {
            File.Copy(codeSrc, codeGame, overwrite: true);
            log.Add($"{CodeGame} <- {CodeSrc}");
        }

        var old = Read(gameRoot);
        int rev = old.Rev + 1;
        // drop = every name this tab placed before that is not wanted now; the game side takes exactly those out again
        var want = glyphs.Concat(stats).Concat(burdens).ToHashSet(StringComparer.OrdinalIgnoreCase); // -notcontains in the old app ignores case
        var drop = old.Drop.Concat(old.Glyphs).Concat(old.Stats).Concat(old.Burdens).Where(n => n.Length > 0 && !want.Contains(n)).Distinct().ToList();

        const string nl = "\r\n";
        var t = new StringBuilder();
        t.Append(@"-- mod\sbg_loadout_cfg.lua - written by the Mod Switcher (tab ""Valhalla loadout""). mod\sbg_loadout.lua reads it every ~2 s.").Append(nl);
        t.Append("return {").Append(nl).Append("  on = ").Append(on ? "true" : "false").Append(',').Append(nl);
        t.Append("  rev = ").Append(rev).Append(',').Append(nl);
        AppendList(t, "glyphs", glyphs);
        AppendList(t, "stats", stats);
        AppendList(t, "drop", drop);
        t.Append("  slots = {").Append(nl);
        foreach (var (slot, _) in Slots)
            if (slots.TryGetValue(slot, out var v) && v.Length > 0) t.Append("    ").Append(slot).Append(" = \"").Append(v).Append("\",").Append(nl);
        t.Append("  },").Append(nl);
        if (burdens.Count > 0) AppendList(t, "burdens", burdens);   // only when used: an empty list keeps the old app's bytes
        if (breaching) t.Append("  breaching = true,").Append(nl);   // only when true: off keeps the old app's bytes
        t.Append('}').Append(nl);
        File.WriteAllText(Path.Combine(gameRoot, CfgGame), t.ToString(), new UTF8Encoding(false));

        var back = Read(gameRoot);
        if (back.Rev != rev || back.On != on || back.Glyphs.Count != glyphs.Count || back.Stats.Count != stats.Count
            || back.Burdens.Count != burdens.Count || back.Breaching != breaching)
            throw new IOException("loadout file write verify failed");
        log.Add(on ? $"loadout rev {rev}: {back.Glyphs.Count} glyphs, {back.Stats.Count} stat glyphs, {back.Slots.Count} runic/relic slots"
                       + (burdens.Count > 0 ? $", {burdens.Count} burden glyphs" : "")
                       + (breaching ? ", Penalty of Breaching on" : "")
                   : $"loadout rev {rev}: OFF (the game's own loadout; glyphs this tab gave are taken out again)");
        return log;

        static void AppendList(StringBuilder t, string key, IEnumerable<string> names)
        {
            t.Append("  ").Append(key).Append(" = {").Append(nl);
            foreach (var n in names) if (n.Length > 0) t.Append("    \"").Append(n).Append("\",").Append(nl);
            t.Append("  },").Append(nl);
        }
    }

    /// <summary>
    /// mod\live.lua must call the loadout file. When the installed live.lua does not mention sbg_loadout, the block in
    /// switcher\loadout_stub.lua goes in front of "L.tick = function(C, SBG)" and one call line right after it.
    /// </summary>
    static readonly Regex anchor = new(@"(?m)^L\.tick = function\(C, SBG\)[ \t]*\r?$");
    const string NoAnchor = @"mod\live.lua has no ""L.tick = function(C, SBG)"" line: cannot add the loadout loader";

    /// <summary>True when a live.lua without the loader has no single anchor line, so the loader block cannot go in.</summary>
    static bool StubBlocked(string liveText) =>
        !liveText.Contains("sbg_loadout", StringComparison.OrdinalIgnoreCase) && anchor.Matches(liveText).Count != 1;

    static bool EnsureStub(string gameRoot, string dataRoot)
    {
        var live = Path.Combine(gameRoot, LiveGame);
        if (!File.Exists(live)) throw new FileNotFoundException(@"mod\live.lua is missing (install the probe + live.lua first)");
        var src = File.ReadAllText(live);
        if (src.Contains("sbg_loadout", StringComparison.OrdinalIgnoreCase)) return false;
        if (StubBlocked(src)) throw new InvalidDataException(NoAnchor);
        string nl = src.Contains("\r\n") ? "\r\n" : "\n";
        var stub = File.ReadAllText(Path.Combine(dataRoot, StubSrc)).Replace("\r\n", "\n").TrimEnd('\n').Replace("\n", nl);
        const string call = "  do local okLO, errLO = pcall(sbgLoadoutTick, C, SBG); if not okLO and not SBG.loadoutErr then SBG.loadoutErr = true; print(\"[SBG] loadout stub ERR \" .. tostring(errLO)) end end -- SBG-LOADOUT-STUB call";
        var updated = anchor.Replace(src, m => stub + nl + m.Value.TrimEnd('\r') + nl + call, 1);
        File.WriteAllText(live, updated, new UTF8Encoding(false));
        if (!File.ReadAllText(live).Contains("SBG-LOADOUT-STUB call")) throw new IOException("live.lua write verify failed");
        return true;
    }
}
