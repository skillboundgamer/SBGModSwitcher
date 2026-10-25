using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace ModSwitcher2;

/// <summary>
/// Phase 7, warnings polish: rule warnings on the cards and in the Apply list, "close the game first" marks, the
/// old-Switcher banner, and the bug report for Settings -> About.
/// </summary>
public partial class MainWindow
{
    bool _oldSwitcherOpen;

    // ---------- rule warnings ----------

    Dictionary<string, List<string>> CurrentWarnings()
    {
        if (_catalog == null) return new();
        bool namesUsed = _extra.ContainsKey("names") ? _namesNow.Length > 0 : (_namesSaved ?? "").Length > 0;
        bool msgsOn = _extra.ContainsKey("msgs") ? _msgsNow : _msgsSaved == true;
        var st = new RuleState
        {
            Active = new Dictionary<string, int>(_active), Pending = new Dictionary<string, int>(_pending),
            Extras = _extra.Keys.ToList(), NamesUsed = namesUsed, MsgsOn = msgsOn, ProbeUnavailable = ProbeOnMissing(),
        };
        return Rules.Check(_catalog.Rules, st);
    }

    /// <summary>The amber line under a card's presets: what the edit will cause (never a block).</summary>
    void ShowRuleWarnings()
    {
        var w = CurrentWarnings();
        foreach (var c in _cards.Values)
        {
            bool has = w.TryGetValue(c.Group.Id, out var list);
            c.WarnLine.Text = has ? "⚠ " + string.Join("\n⚠ ", list!) : "";
            c.WarnLine.Visibility = Show(has);
        }
    }

    /// <summary>Marks for one Apply-list row: rule warnings, and "close the game first" when it writes a start-time file.</summary>
    List<string> RowMarks(string key, ModPreset? preset, string reload)
    {
        var marks = CurrentWarnings().TryGetValue(key, out var l) ? new List<string>(l) : new List<string>();
        if (_gameRunning == true)
        {
            bool startFile = false;
            if (preset != null && _dataRoot != null && GameLocator.IsGameDir(_settings.GamePath))
            {
                try { startFile = new ModApplier(_settings.GamePath!, _dataRoot, ModApplier.DefaultRecycle, Matcher()).NeedsClosedGame(preset); }
                catch (Exception) { startFile = reload == "full"; }
            }
            else startFile = key == "fov" || key.StartsWith("slot:");
            if (startFile) marks.Add("Close the game first: this writes a file the game reads at start (Apply would refuse it)");
        }
        return marks;
    }

    // ---------- the old Switcher ----------

    /// <summary>Checked with the game watcher: both apps write the same files, so one at a time.</summary>
    async Task CheckOldSwitcherAsync()
    {
        bool open = await Task.Run(OldSwitcherOpen);
        if (open == _oldSwitcherOpen) return;
        _oldSwitcherOpen = open;
        UpdateBanner();
    }

    // ---------- bug report ----------

    void CopyBugReport_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        sb.AppendLine((_edition?.Title ?? "SBG Mod Switcher") + " bug report");
        sb.AppendLine($"Version: {v}  |  Windows: {RuntimeInformation.OSDescription}  |  .NET: {RuntimeInformation.FrameworkDescription}");
        var game = _settings.GamePath;
        sb.AppendLine($"Game folder: {(GameLocator.IsGameDir(game) ? game : "NOT FOUND")}");
        sb.AppendLine($"Mod loader: {(GameLocator.IsGameDir(game) && GameLocator.HasModLoader(game!) ? "found" : "missing")}  |  Game running: {(_gameRunning == true ? "yes" : "no")}");
        sb.AppendLine("Mods:");
        if (_catalog != null)
            foreach (var g in _catalog.Groups)
            {
                var idx = _active.TryGetValue(g.Id, out var a) ? a : int.MinValue;
                var now = idx == int.MinValue ? "not checked" : idx < 0 ? "custom" : g.Presets[idx].Label;
                sb.AppendLine($"  {g.Name}: {now}" + (_pending.TryGetValue(g.Id, out var p) ? $"  (edit not applied: {g.Presets[p].Label})" : ""));
            }
        if (_edition == null)
            sb.AppendLine($"Tweaks: FOV {(_fovSaved.On ? _fovSaved.Deg + "°" : "game default")}, bar names \"{_namesSaved ?? "-"}\", messages {(_msgsSaved == true ? "on" : "off")}");
        if (_loList != null)   // the loadout card was built (the full app, or an edition with "loadout": true)
            sb.AppendLine($"Loadout: {(_loSaved.Exists ? (_loSaved.On ? "on" : "off") + $", rev {_loSaved.Rev}, {_loSaved.Glyphs.Count} glyphs" : "none")}"
                          + (_loCode != null ? $", plug-in app v{_loCode.App} / game v{_loCode.Game ?? "-"}" : "")
                          + $", loader {(_loProblem != null ? "blocked: " + _loProblem : "ok")}");
        sb.AppendLine("Last activity:");
        var lines = _activity.ToString().TrimEnd().Split('\n');
        foreach (var line in lines.Skip(Math.Max(0, lines.Length - 50))) sb.AppendLine("  " + line.TrimEnd('\r'));
        try { Clipboard.SetText(sb.ToString()); ShowToast("Bug report copied: paste it into your message", "Ok"); }
        catch (Exception ex) { ShowToast("Could not copy: " + ex.Message, "Bad"); }
    }

    // ---------- one app at a time ----------

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    /// <summary>Called by App when a second copy starts: bring the first window to the front.</summary>
    public static void FocusOtherInstance()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var p in Process.GetProcessesByName(me.ProcessName))
            using (p)
                if (p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
    }
}
