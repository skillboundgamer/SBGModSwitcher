namespace ModSwitcher2;

/// <summary>A conflict rule from mods.json: an edit of group <c>Edit</c> while <c>While</c> ("group=index") holds.</summary>
public sealed class ModRule
{
    public string Edit { get; set; } = "";
    public string While { get; set; } = "";
    public string Warn { get; set; } = "";
}

/// <summary>What the pending edits would leave behind, for the warning rules (no UI, so it can be tested headless).</summary>
public sealed class RuleState
{
    public Dictionary<string, int> Active { get; set; } = new();   // group id -> preset on disk (-1 = custom)
    public Dictionary<string, int> Pending { get; set; } = new();  // group id -> preset picked, not applied yet
    public List<string> Extras { get; set; } = new();              // edited extra rows: "names", "msgs", "fov", "figures", "slot:..."
    public bool NamesUsed { get; set; }                            // bar names not "game default" after Apply
    public bool MsgsOn { get; set; }                               // on-screen messages on after Apply
    public bool ProbeUnavailable { get; set; }                     // the probe's On preset is not in this build (release)
}

/// <summary>
/// Warnings for the Apply list and the cards (phase 7). A warning never blocks: it says what will happen.
///  - mods.json "rules": e.g. a Valhalla door preset while the Thor companion is on removes the Thor load-table patch;
///  - built in: boss bar names / on-screen messages need the Kratos probe On (live.lua reads them through the probe).
/// </summary>
public static class Rules
{
    public const string NeedProbe = "Needs the Kratos probe On (Game Tweaks): without it this does nothing in game";
    public const string NeedProbeMissing = "Needs the Kratos probe, which is not in this version yet: this does nothing in game";
    public const string ProbeOffBreaks = "Boss bar names / on-screen messages stop working without the probe";

    public static Dictionary<string, List<string>> Check(IEnumerable<ModRule> rules, RuleState s)
    {
        var w = new Dictionary<string, List<string>>();
        void Add(string key, string text) { if (!w.TryGetValue(key, out var l)) w[key] = l = new(); if (!l.Contains(text)) l.Add(text); }
        int Final(string g) => s.Pending.TryGetValue(g, out var p) ? p : s.Active.TryGetValue(g, out var a) ? a : -1;
        bool Edited(string key) => s.Pending.ContainsKey(key) || s.Extras.Contains(key);

        foreach (var r in rules)
        {
            if (!Edited(r.Edit)) continue;
            var parts = r.While.Split('=', 2);
            if (parts.Length == 2 && int.TryParse(parts[1], out var want) && Final(parts[0]) == want) Add(r.Edit, r.Warn);
        }

        // only a probe known to be Off (0) warns: -1 = custom or not checked yet, which says nothing
        bool probeOff = Final("probe") == 0;
        if (probeOff)
        {
            var need = s.ProbeUnavailable ? NeedProbeMissing : NeedProbe;   // a release cannot turn the probe On
            if (s.Extras.Contains("names") && s.NamesUsed) Add("names", need);
            if (s.Extras.Contains("msgs") && s.MsgsOn) Add("msgs", need);
            if (s.Pending.ContainsKey("probe") && (s.NamesUsed || s.MsgsOn)) Add("probe", ProbeOffBreaks);
        }
        return w;
    }
}
