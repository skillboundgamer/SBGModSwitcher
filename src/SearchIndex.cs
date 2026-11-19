using System.Globalization;
using System.IO;
using System.Text;

namespace ModSwitcher2;

/// <summary>Which block of the results panel an entry shows in (this order).</summary>
public enum SearchGroup { Mods, Settings, Help }

/// <summary>One thing the top search box can find.
/// Tab: the page it lives on (overview, boss, companions, valhalla, tweaks, settings; "" = no page, an action).
/// Target: what Enter opens: "group:ID" (a mod card), "card:TITLE" (the card on Tab whose header is TITLE),
/// "action:NAME" (guide, guide:N, disclaimer, bugreport, settingsdir, activity).</summary>
public sealed record SearchEntry(SearchGroup Group, string Title, string Where, string Tab, string Target, string Icon, string Keywords = "");

/// <summary>One result: the entry, its score (higher = better) and the matched part of the title (Length 0 = matched
/// in the other words only, nothing to highlight).</summary>
public readonly record struct SearchHit(SearchEntry Entry, int Score, int MatchStart, int MatchLength);

/// <summary>
/// The index behind the top search box (revamp batch A). Speed: <see cref="Build"/> runs once, off the UI thread (folds
/// every text: lower case, no accents, so "hrolf" finds "Hrólf"); <see cref="Query"/> only scans that small in-memory
/// list (about 150 entries, well under 1 ms), so a key press never touches the cards or the disk.
/// Ranking: title starts with the query > a word of the title starts with it > the title holds it > every word of the
/// query is in the title > every word is somewhere (title, place, other words). Ties: shorter title, then list order.
/// The results come grouped Mods, Settings, Help.
/// </summary>
public sealed class SearchIndex
{
    readonly SearchEntry[] _entries;
    readonly string[] _title;   // folded title, same length as the title (char by char), so a match maps 1:1 for the highlight
    readonly string[] _all;     // folded title + place + other words

    SearchIndex(SearchEntry[] entries)
    {
        _entries = entries;
        _title = entries.Select(e => Fold(e.Title)).ToArray();
        _all = entries.Select(e => Fold(e.Title + " " + e.Where + " " + e.Keywords)).ToArray();
    }

    public int Count => _entries.Length;
    public IReadOnlyList<SearchEntry> Entries => _entries;

    public static readonly SearchIndex Empty = new(Array.Empty<SearchEntry>());

    /// <summary>Same title + target twice (a preset listed by two packs) = once.</summary>
    public static SearchIndex Build(IEnumerable<SearchEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new SearchIndex(entries.Where(e => e.Title.Length > 0 && seen.Add(e.Title + "\n" + e.Target)).ToArray());
    }

    /// <summary>Lower case, accents dropped, one char out per char in (a char that does not fold to one char stays as it is).</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            char o = char.ToLowerInvariant(c);
            if (c > 127)
            {
                var d = c.ToString().Normalize(NormalizationForm.FormD);
                var keep = d.Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark).ToArray();
                if (keep.Length == 1) o = char.ToLowerInvariant(keep[0]);
            }
            sb.Append(o);
        }
        return sb.ToString();
    }

    /// <summary>The best <paramref name="max"/> hits, grouped Mods, Settings, Help. Empty or blank query = no hits.</summary>
    public List<SearchHit> Query(string? query, int max = 8)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrWhiteSpace(query) || max <= 0) return hits;
        string q = Fold(query.Trim());
        while (q.Contains("  ")) q = q.Replace("  ", " ");
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < _entries.Length; i++)
        {
            string t = _title[i];
            int score = 0, at = -1, len = 0;
            int pos = t.IndexOf(q, StringComparison.Ordinal);
            if (pos == 0) { score = 100; at = 0; len = q.Length; }
            else if (pos > 0)
            {
                int ws = WordStart(t, q);
                if (ws >= 0) { score = 80; at = ws; }
                else { score = 60; at = pos; }
                len = q.Length;
            }
            else if (words.Length > 1 && words.All(w => t.Contains(w, StringComparison.Ordinal)))
            {
                score = 50; at = t.IndexOf(words[0], StringComparison.Ordinal); len = words[0].Length;
            }
            else if (words.All(w => _all[i].Contains(w, StringComparison.Ordinal)))
            {
                score = 30;
                int p0 = t.IndexOf(words[0], StringComparison.Ordinal);
                if (p0 >= 0) { at = p0; len = words[0].Length; }
            }
            if (score == 0) continue;
            hits.Add(new SearchHit(_entries[i], score, Math.Max(at, 0), at < 0 ? 0 : len));
        }

        var order = hits.Select((h, i) => (h, i))
                        .OrderByDescending(x => x.h.Score).ThenBy(x => x.h.Entry.Title.Length).ThenBy(x => x.i)
                        .Take(max).Select(x => x.h).ToList();
        // grouped for the panel; inside a group the rank order stays (OrderBy is stable)
        return order.OrderBy(h => (int)h.Entry.Group).ToList();
    }

    /// <summary>Where q starts a word of t (after a space or a sign), or -1.</summary>
    static int WordStart(string t, string q)
    {
        for (int p = t.IndexOf(q, StringComparison.Ordinal); p >= 0; p = t.IndexOf(q, p + 1, StringComparison.Ordinal))
            if (p == 0 || !char.IsLetterOrDigit(t[p - 1])) return p;
        return -1;
    }

    // ---------- what the app indexes ----------

    public static string TabName(string tab) => tab switch
    {
        "overview" => "Overview",
        "boss" => "Boss Fights",
        "companions" => "Companions",
        "valhalla" => "Valhalla",
        "tweaks" => "Game Tweaks",
        "settings" => "Settings",
        _ => "Help",
    };

    /// <summary>Preset names that only mean "this mod off": not indexed as their own row.</summary>
    static readonly HashSet<string> OffNames = new(StringComparer.OrdinalIgnoreCase) { "vanilla", "off", "on", "normal", "game", "none" };

    /// <summary>
    /// Every entry the app can show. <paramref name="cat"/> = the mod groups (and their presets) of this app or its packs;
    /// <paramref name="full"/> = the full app (tweaks, the Kratos loadout, the Lab; an edition has none of them);
    /// <paramref name="game"/> = the game folder, for the Back blades presets saved there (file read: call off the UI thread).
    /// </summary>
    public static List<SearchEntry> Collect(ModCatalog? cat, bool full, bool loadout, string? game, IEnumerable<string>? guideSteps = null)
    {
        var list = new List<SearchEntry>();
        if (cat != null)
            foreach (var g in cat.Groups)
            {
                string tab = TabName(g.Tab);
                var presetWords = string.Join(" ", g.Presets.Select(p => p.Name + " " + p.Short));
                list.Add(new SearchEntry(SearchGroup.Mods, g.Name, tab, g.Tab, "group:" + g.Id, g.Icon, (g.Note ?? "") + " " + presetWords));
                for (int i = 0; i < g.Presets.Count; i++)
                {
                    var p = g.Presets[i];
                    if (i == g.Vanilla || p.Disabled || OffNames.Contains(p.Name) || string.Equals(p.Name, g.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new SearchEntry(SearchGroup.Mods, p.Name, g.Name + " · preset", g.Tab, "group:" + g.Id, g.Icon, p.Short ?? ""));
                }
            }

        if (full)
        {
            list.Add(new SearchEntry(SearchGroup.Settings, "Camera FOV", "Game Tweaks", "tweaks", "card:Camera FOV", "IcEye", "fov field of view camera zoom angle"));
            list.Add(new SearchEntry(SearchGroup.Settings, "Kratos walk speed", "Game Tweaks", "tweaks", "card:Kratos walk speed", "IcBolt", "speed hero_speed run reset"));
            list.Add(new SearchEntry(SearchGroup.Settings, "Boss bar names", "Game Tweaks", "tweaks", "card:Boss bar names", "IcCrown", "health bar name boss"));
            list.Add(new SearchEntry(SearchGroup.Settings, "On-screen messages", "Game Tweaks", "tweaks", "card:On-screen messages", "IcVoice", "messages text hint probe"));
            list.Add(new SearchEntry(SearchGroup.Settings, "Loadout item list", "Settings", "settings", "card:Loadout item list", "IcFile", "language items glyphs names"));
            // the Lab is a stub in this build (LabView.cs): no Lab cards to find
        }
        if (full || loadout)
        {
            list.Add(new SearchEntry(SearchGroup.Mods, "Kratos loadout", "Valhalla", "valhalla", "card:Kratos loadout", "IcShield", "glyphs runic relic burdens loadout"));
        }

        // revamp batch B: the script loader is turned on / off on the Overview; GoWR MIO's state is the Game folder card + hero
        list.Add(new SearchEntry(SearchGroup.Settings, "Mod loader (GoWR MIO)", "Overview", "overview", "card:Game folder", "IcPlug", "loader mio winmm"));
        list.Add(new SearchEntry(SearchGroup.Settings, "Script loader", "Overview", "overview", "card:Script loader", "IcPlug", "loader script lua plug-in plugin remove install turn on off kratos"));
        list.Add(new SearchEntry(SearchGroup.Settings, "VOTG", "Overview", "overview", "card:VOTG", "IcShield", "votg vanquisher mod loader hook"));
        list.Add(new SearchEntry(SearchGroup.Help, "Copy diagnostic info", "Overview", "", "action:bugreport", "IcInfo", "diagnostic info copy versions"));
        list.Add(new SearchEntry(SearchGroup.Settings, "Game folder", "Settings", "settings", "card:Game folder", "IcFolder", "path folder browse detect steam"));
        list.Add(new SearchEntry(SearchGroup.Settings, "Active mods", "Overview", "overview", "card:Active mods", "IcCheck", "active on restore refresh"));
        list.Add(new SearchEntry(SearchGroup.Settings, "Settings folder", "Settings", "", "action:settingsdir", "IcFolder", "appdata settings folder"));
        list.Add(new SearchEntry(SearchGroup.Help, "Copy bug report", "Settings", "", "action:bugreport", "IcInfo", "bug report log copy help"));
        list.Add(new SearchEntry(SearchGroup.Help, "Activity log", "Bottom bar", "", "action:activity", "IcFile", "log activity history errors"));
        list.Add(new SearchEntry(SearchGroup.Help, "Disclaimer", "About", "", "action:disclaimer", "IcInfo", "risk unofficial about"));
        if (guideSteps != null)
        {
            int i = 0;
            foreach (var s in guideSteps)
                list.Add(new SearchEntry(SearchGroup.Help, "Guide: " + s, "Guide", "", "action:guide:" + i++, "IcInfo", "guide help setup first start"));
        }
        return list;
    }

    /// <summary>--search QUERY [--data DIR] [--game DIR] [--edition]: one line per hit, "Group TAB Title TAB Where TAB Target" (tests).</summary>
    public static int Cli(string query, string? data, string? game, TextWriter o)
    {
        var root = data ?? DataRoot.Find();
        ModCatalog? cat = null;
        if (root != null && File.Exists(DataRoot.CatalogPath(root))) cat = ModCatalog.Load(DataRoot.CatalogPath(root));
        var idx = Build(Collect(cat, true, true, game, MainWindow.GuideStepNames));
        foreach (var h in idx.Query(query))
            o.WriteLine($"{h.Entry.Group}\t{h.Entry.Title}\t{h.Entry.Where}\t{h.Entry.Target}\t{h.Entry.Title.Substring(h.MatchStart, h.MatchLength)}");
        return 0;
    }
}
