using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

/// <summary>
/// Phase 8b: the loadout's item list (valhalla_items.tsv) built on the player's PC from the player's own files, so the
/// game text never ships. C# port of tools/valitems.py + research/valhalla/lamsof.py, same rules, same output:
///  - item names: the strings of the MIO dump of progressionmanagervalhalla.visualscript;
///  - text ids: exec\dc\pc_le\wad_r_perm.dcb (cells just before the item's 64-bit name hash);
///  - names and descriptions: exec\wad\pc_le\r_lang_&lt;language&gt;.wad.
/// tests/test_switcher2_items.py: for English the result is byte-identical to switcher/valhalla_items.tsv.
/// </summary>
public static class ItemsGen
{
    public const string DumpScript = @"mod\dump\int9\gameart\visualscripts\valhalla\resources\r_perm\progression\progressionmanagervalhalla.visualscript";
    public const string PermDcb = @"exec\dc\pc_le\wad_r_perm.dcb";
    public static string LangWad(string code) => $@"exec\wad\pc_le\r_lang_{code}.wad";
    /// <summary>Bump when Classify / the row rules change: it is part of the built list's file name (ItemsPath), so an
    /// app with new rules never reuses a list built by an older app (2 = the "+" technique glyphs, 2026-10-25).</summary>
    public const int RulesVersion = 2;
    public const string Header = "# internal name\tkind\tlabel\tdescription   (made by tools/valitems.py; do not edit by hand)";

    /// <summary>Language codes the game has on this PC (r_lang_*.wad), English first.</summary>
    public static List<string> Languages(string game)
    {
        var dir = Path.Combine(game, @"exec\wad\pc_le");
        var list = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "r_lang_*.wad").Select(f => Path.GetFileNameWithoutExtension(f)["r_lang_".Length..]).OrderBy(c => c, StringComparer.Ordinal).ToList()
            : new List<string>();
        if (list.Remove("en")) list.Insert(0, "en");
        return list;
    }

    /// <summary>The tsv text. Throws FileNotFoundException naming the missing input.</summary>
    public static string Build(string game, string lang = "en")
    {
        string Need(string rel, string what) => File.Exists(Path.Combine(game, rel)) ? Path.Combine(game, rel)
            : throw new FileNotFoundException($"{what} is missing: {rel}");
        var dump = Need(DumpScript, "the MIO dump of the Valhalla progression script (turn on the dump in GoWR MIO and start the game once)");
        var perm = GowrTools.Lz4Read(Need(PermDcb, "the game's wad_r_perm.dcb"));
        var text = ReadLang(GowrTools.Lz4Read(Need(LangWad(lang), $"the game's {lang} language file")));

        // names: every string of the dump script that is an item we list (sorted, as items_raw.txt was)
        var names = Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(dump)), "[A-Za-z0-9_]{4,}")
            .Select(m => m.Value).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToList();

        var rows = new List<(string Kind, string Label, string Name, string Desc)>();
        int named = 0;
        foreach (var name in names)
        {
            var c = Classify(name);
            if (c == null) continue;
            var (kind, label) = c.Value;
            string desc = "";
            var t = Ids(perm, name);
            var nameText = t.Count > 0 && text.TryGetValue(t[0], out var tx) ? Clean(tx) : "";
            if (nameText.Length > 0)
            {
                named++;
                label = kind == "Stat" ? nameText + " - " + label : nameText + (name.EndsWith("_Val_01") ? " (level 2)" : "");
                if (t.Count > 1) desc = Clean(text.TryGetValue(t[1], out var d) ? d : "");
            }
            rows.Add((kind, label, name, desc));
        }
        // no game name found at all = the record layout moved (a game update): fail instead of caching internal names
        if (rows.Count > 0 && named == 0)
            throw new InvalidDataException("no item names found in wad_r_perm.dcb: this game version is newer than the app");
        string[] order = { "Perk", "Axe", "Blades", "Spear", "Stat", "Technique" };
        var sorted = rows.OrderBy(r => Array.IndexOf(order, r.Kind) is var i && i >= 0 ? i : 9)
                         .ThenBy(r => r.Kind, StringComparer.Ordinal)
                         .ThenBy(r => PyLower(r.Label), StringComparer.Ordinal);
        var sb = new StringBuilder(Header).Append('\n');
        foreach (var r in sorted) sb.Append(r.Name).Append('\t').Append(r.Kind).Append('\t').Append(r.Label).Append('\t').Append(r.Desc).Append('\n');
        return sb.ToString();
    }

    /// <summary>Python's str.lower(): like ToLowerInvariant, except the Turkish dotted capital I becomes "i" + combining dot.</summary>
    static string PyLower(string s) => s.Replace("\u0130", "i\u0307").ToLowerInvariant();

    // ---------- valitems.py classify / words / clean ----------

    static readonly HashSet<string> Skip = new()
    {
        "Glyph_Perk", "Glyph_Impact", "Glyph_Stats", "Glyph_Skill", "Glyph_Weapon_Mod", "Glyph_Refill", "Glyph_Runic_Opt_In",
        "Glyph_RelicBonusRunActive", "Glyph_Perk_RelicCollectorActive", "Glyph_Perk_RelicCollectorGlyphPreDestroy",
    };
    static readonly Dictionary<string, string> Stat = new()
    {
        ["Str"] = "Strength", ["Strength"] = "Strength", ["Def"] = "Defense", ["Run"] = "Runic", ["Runic"] = "Runic",
        ["Vit"] = "Vitality", ["Cld"] = "Cooldown", ["Cooldown"] = "Cooldown", ["Luk"] = "Luck", ["Luck"] = "Luck",
    };
    static readonly Dictionary<string, string> Relic = new() { ["Angurvadal"] = "Angrvadall", ["Hofud"] = "Hofud" };

    static string Words(string s)
    {
        s = Regex.Replace(s.Replace('_', ' '), "([a-z0-9])([A-Z])", "$1 $2");
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    public static (string Kind, string Label)? Classify(string name)
    {
        if (Skip.Contains(name) || name.StartsWith("Glyph_Refill") || name.StartsWith("Glyph_ValKeyWeaponThisRun"))
            return null;
        var st = Regex.Match(name, @"^Glyph_STN_(.+)_Group_Mod$");
        if (st.Success) return ("Technique", Words(st.Groups[1].Value));
        var m = Regex.Match(name, @"^Glyph_AttributeStats_(.+)_(\d)$");
        if (m.Success)
        {
            var parts = Regex.Replace(m.Groups[1].Value, "([a-z])([A-Z])", "$1_$2").Split('_').Select(p => Stat.TryGetValue(p, out var v) ? v : p);
            return ("Stat", string.Join(" + ", parts) + $" (tier {m.Groups[2].Value})");
        }
        m = Regex.Match(name, @"^Glyph_(Axe|Blades|Spear)_(.+?)(_DLC)?_0$");
        if (m.Success)
        {
            var w = m.Groups[1].Value;
            var rest = Regex.Replace(m.Groups[2].Value, "^(Perk_|WeaponComponent_)", "");
            rest = Regex.Replace(rest, $"^({w}|{w.TrimEnd('s')})_?", "");
            return (w, Words(rest));
        }
        m = Regex.Match(name, @"^Glyph_(Perk_|KratosArmorTrinket_|OnTriangle_)(.+?)(_DLC)?_0$");
        if (m.Success) return ("Perk", Words(m.Groups[2].Value));
        m = Regex.Match(name, @"^WeaponSpecial_(Axe|Blades|Spear)_(Light|Heavy)_(.+?)_Val(_01)?$");
        if (m.Success) return ($"WeaponSpecial_{m.Groups[1].Value}{m.Groups[2].Value}", Words(m.Groups[3].Value) + (m.Groups[4].Success ? " (level 2)" : ""));
        m = Regex.Match(name, @"^Sword[Hh]ilt_(.+)_Val_02$");
        if (m.Success) return ("SwordHilt", "Hilt of " + (Relic.TryGetValue(m.Groups[1].Value, out var r) ? r : m.Groups[1].Value));
        return null;
    }

    static string Clean(string text)
    {
        text = Regex.Replace(text, @"\[/?style[^\]]*\]", "");
        text = Regex.Replace(text, @"\[Icons:([A-Z_]+)\]\s*", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    // ---------- lamsof.py ids / lang ----------

    /// <summary>Text ids in the cells before the item's record hash (name first), or none.</summary>
    static List<string> Ids(byte[] perm, string name)
    {
        var key = BitConverter.GetBytes(GowrTools.SmsHash(name));
        int i = perm.AsSpan().LastIndexOf(key);
        var outIds = new List<string>();
        if (i < 0) return outIds;
        foreach (var o in new[] { i - 152, i - 136, i - 120 })
        {
            if (o < 8) break;
            uint kind = BitConverter.ToUInt32(perm, o), tid = BitConverter.ToUInt32(perm, o + 4);
            if (!(kind > 0 && kind < 64 && tid > 0 && tid < 200000 && BitConverter.ToUInt64(perm, o - 8) < 0x1000)) break;
            outIds.Add(tid.ToString());
        }
        return outIds;
    }

    /// <summary>LAMS id -> text: '*id*\n text' records; a record ends where the next '\n*id*\n' starts.</summary>
    static Dictionary<string, string> ReadLang(byte[] wad)
    {
        var s = Encoding.Latin1.GetString(wad);        // 1 char per byte: offsets stay byte offsets
        var outText = new Dictionary<string, string>();
        var first = new Regex(@"\*([0-9]+)\*\n");
        var next = new Regex(@"\n\*([0-9]+)\*\n");
        var m = first.Match(s);
        while (m.Success)
        {
            int start = m.Index + m.Length;
            var n = next.Match(s, start);
            int end = n.Success ? n.Index : s.Length;
            var raw = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(s.Substring(start, end - start)));
            outText[m.Groups[1].Value] = raw.Trim('\n', '\0');
            if (!n.Success) break;
            m = first.Match(s, n.Index + 1);    // the next record starts at that '*'
        }
        return outText;
    }
}
