using System.IO;
using System.Text.Json;

namespace ModSwitcher2;

/// <summary>One grey, disabled card of an edition: <c>{ "title": "...", "text": "...", "tab": "valhalla" }</c>.</summary>
public sealed class ComingSoon
{
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Tab { get; set; } = "valhalla";
}

/// <summary>
/// An "edition" is the same app showing only some mod groups (plan: notes/25 section 1 step 2, the Tyr edition).
/// It exists when <c>data\edition.json</c> is in the data folder, the way <c>data\release.txt</c> marks a release
/// (tools/mkrelease.py --edition puts it there; the source is switcher2/editions/&lt;id&gt;.json). Without that file
/// nothing changes. The name and version live in this one file only.
///   { "id": "tyr", "title": "...", "version": "1.2.0", "groups": ["valdoor", ...], "tabs": ["valhalla"],
///     "loadout": true, "comingSoon": [ { "title": "...", "text": "...", "tab": "valhalla" } ] }
/// (loadout and comingSoon are optional). Groups are filtered out of mods.json when it is loaded (the json is not
/// copied). Tabs are the group tab keys of mods.json (boss, companions, valhalla, tweaks); Overview and Settings are
/// always there. Optional "discord" and "nexus" (strings): the links of the Community row on the Overview page.
/// "" or a missing field hides that button; no link at all hides the row. Only https:// links to discord.gg,
/// discord.com, nexusmods.com or www.nexusmods.com are kept (<see cref="SafeUrl"/>); anything else counts as empty.
/// An edition has no Game Tweaks and no Lab: those parts are never built. The Kratos loadout card and
/// its item list (built from the player's game files, with its card in Settings) are built only with "loadout": true.
/// </summary>
public sealed class Edition
{
    public const string FileName = "edition.json";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    public List<string> Groups { get; set; } = new();
    public List<string> Tabs { get; set; } = new();
    /// <summary>Optional grey "coming soon" cards under the groups of a tab (not mods: no buttons, never applied).</summary>
    public List<ComingSoon> ComingSoon { get; set; } = new();
    /// <summary>The Kratos loadout card on the Valhalla tab, its item list and the item list card in Settings.</summary>
    public bool Loadout { get; set; }

    /// <summary>Optional Discord invite link (Community row). Checked by <see cref="SafeUrl"/> when loaded.</summary>
    public string Discord { get; set; } = "";
    /// <summary>Optional Nexus Mods page link (Community row). Checked by <see cref="SafeUrl"/> when loaded.</summary>
    public string Nexus { get; set; } = "";

    static readonly string[] AllowedHosts = { "discord.gg", "discord.com", "nexusmods.com", "www.nexusmods.com" };

    /// <summary>The link if it is an https:// link to an allowed site, else "" (a changed data file cannot open anything else).</summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return "";
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || !u.IsDefaultPort) return "";
        return AllowedHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase) ? u.AbsoluteUri : "";
    }

    public bool ShowsTab(string tab) => Tabs.Contains(tab, StringComparer.OrdinalIgnoreCase);

    /// <summary>The edition of this data folder, or null (the normal app). A broken file throws.</summary>
    public static Edition? Load(string? dataRoot)
    {
        if (dataRoot == null) return null;
        var path = Path.Combine(dataRoot, FileName);
        if (!File.Exists(path)) return null;
        var ed = JsonSerializer.Deserialize<Edition>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip })
            ?? throw new InvalidDataException(FileName + " is empty");
        if (ed.Title.Length == 0 || ed.Groups.Count == 0)
            throw new InvalidDataException(FileName + " needs a title and at least one group");
        if (ed.Loadout && !ed.ShowsTab("valhalla"))   // the card is built on the Valhalla tab (LoadoutView.cs)
            throw new InvalidDataException(FileName + ": \"loadout\" needs the valhalla tab");
        ed.Discord = SafeUrl(ed.Discord);
        ed.Nexus = SafeUrl(ed.Nexus);
        // A hand-edited null list / null entry must not break the start; a card for a tab the edition does not show
        // would be built on a hidden tab, so it is dropped. Tab keys are lower case (ModsView.TabPanel).
        ed.ComingSoon = (ed.ComingSoon ?? new()).Where(c => c != null && ed.ShowsTab(c.Tab ?? "")).ToList();
        foreach (var c in ed.ComingSoon) c.Tab = c.Tab.ToLowerInvariant();
        return ed;
    }

    /// <summary>Keep only this edition's groups, and only the rules between them.</summary>
    internal void Filter(ModCatalog cat)
    {
        foreach (var id in Groups)
            if (!cat.Groups.Any(g => g.Id == id)) throw new InvalidDataException($"{FileName}: no group \"{id}\" in mods.json");
        cat.Groups = cat.Groups.Where(g => Groups.Contains(g.Id)).ToList();
        cat.Rules = cat.Rules.Where(r => Groups.Contains(r.Edit) && Groups.Contains(r.While.Split('=')[0])).ToList();
    }
}
