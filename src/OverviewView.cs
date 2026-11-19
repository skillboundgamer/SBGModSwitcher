using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ModSwitcher2;

/// <summary>The Overview's loader questions without UI (tests: --loader uses): which active mods run through the
/// script loader (they stop on Remove), which have their files in while it is out (they wait), and may it go now.</summary>
public static class LoaderUse
{
    public static (List<string> Uses, List<string> Waits) Scan(ModCatalog cat, string game, string data)
    {
        var m = new ModMatcher(game, data);
        bool present = Loader.Present(game);
        var uses = new List<string>();
        var waits = new List<string>();
        foreach (var g in cat.Groups)
        {
            int i = m.Check(g);
            if (i >= 0 && g.Presets[i].UsesLoader) uses.Add(g.Name);
            else if (i < 0 && !present && g.Presets.Any(p => p.UsesLoader) && m.Check(g, true) is int w && w >= 0 && g.Presets[w].UsesLoader)
                waits.Add(g.Name);
        }
        return (uses, waits);
    }

    /// <summary>True when a step of the preset writes a file under mod\ (only a mod loader reads those).</summary>
    static bool InModFolder(ModPreset p) =>
        p.Steps.Any(s => Votg.StepDest(s) is { } d && d.StartsWith(@"mod\", StringComparison.Ordinal));

    /// <summary>No mod loader at all: a real game folder with no MIO and no VOTG (three file checks).</summary>
    public static bool NoLoader(string? game) =>
        GameLocator.IsGameDir(game) && !GameLocator.HasModLoader(game!) && !Loader.VotgActive(game!);

    /// <summary>The "not running" rule of the Overview chips: no loader, and the active preset (idx; idx &lt; 0 = any preset of the group)
    /// uses the script loader or writes under mod\. A preset outside mod\ stays plain.</summary>
    public static bool NotRunning(bool noLoader, ModGroup g, int idx) =>
        noLoader && (idx >= 0 ? g.Presets[idx].UsesLoader || InModFolder(g.Presets[idx]) : g.Presets.Any(InModFolder));

    /// <summary>Names of the active mods that do not run because there is no loader (tests: --loader uses, rows "notrunning").</summary>
    public static List<string> NotRunningList(ModCatalog cat, string game, string data)
    {
        var res = new List<string>();
        bool noLoader = NoLoader(game);
        var m = new ModMatcher(game, data);
        foreach (var g in cat.Groups)
        {
            int i = m.Check(g);
            if (i < 0 && m.Check(g, true) is int w && w >= 0) i = w;   // files in: same as the chip's "waits" case
            if (i == g.Vanilla || (i < 0 && !g.Presets.Any(InModFolder))) continue;
            if (i >= 0 && NotRunning(noLoader, g, i)) res.Add(g.Name);
        }
        return res;
    }

    /// <summary>Why Remove must wait (the loadout is On on disk: its plug-in needs the loader to give the player's own items
    /// back); null = it may go.</summary>
    public static string? RemoveBlock(string game) =>
        Loadout.Read(game).On ? "refused: Kratos loadout is On (switch it Off and Apply first)" : null;
}

/// <summary>
/// The Overview page (revamp batch B, 2026-11-19): the hero card (one state line + Open folder / Refresh), four status
/// cards (game folder, script loader, VOTG, game version), one-line banners and the active-mod chips. The script loader
/// (Loader.cs) is turned on and off here now; the Valhalla loadout card only shows a short line about it.
/// Speed: the loader files and the GoWR.exe version are read off the UI thread (RefreshScriptLoaderAsync, at start,
/// Refresh and after an Apply); a click only stages one Apply row and sets a few texts.
/// </summary>
public partial class MainWindow
{
    /// <summary>The script loader state, read off the UI thread. Mine = files this app put in (only then Remove).</summary>
    sealed record LoaderSnap(bool Present, bool Mine, string? Problem, string? ExeVersion);
    LoaderSnap? _ldr;
    int _ldrGen;
    /// <summary>Group id -> the "loader" preset whose files are in while the loader is out (ModsView RefreshModsAsync).</summary>
    readonly Dictionary<string, int> _loaderWait = new();
    Action? _heroAction;

    /// <summary>An icon tile + its stroke in one tone (Theme.xaml ThOk / ThWarn / ThBad / ThAccent).</summary>
    void OvTone(Border box, System.Windows.Shapes.Path icon, string tone)
    {
        icon.Stroke = Brush(tone);
        box.Background = Brush(tone switch { "ThOk" => "ThOkBg", "ThWarn" => "ThWarnBg", "ThBad" => "ThBadBg", _ => "ThAccentBg" });
    }

    async Task RefreshScriptLoaderAsync()
    {
        var game = _settings.GamePath;
        int gen = ++_ldrGen;
        if (!GameLocator.IsGameDir(game)) { _ldr = null; UpdateScriptLoaderCard(); return; }
        LoaderSnap snap;
        try
        {
            snap = await Task.Run(() =>
            {
                string? problem;
                try { problem = Loader.Problem(game!); } catch (Exception ex) { problem = "Could not read the GoWR MIO files: " + ex.Message; }
                bool present = false, mine = false;
                try { present = Loader.Present(game!); } catch (IOException) { }
                try { mine = Loader.Mine(game!).Count > 0; } catch (IOException) { }
                return new LoaderSnap(present, mine, problem, SetupCheck.ExeVersion(game!));
            });
        }
        catch (Exception ex) { Log("Script loader check failed: " + ex.Message, "Warn"); return; }
        if (gen != _ldrGen) return;   // a newer check started meanwhile
        _ldr = snap;
        UpdateScriptLoaderCard();
    }

    /// <summary>The script loader card + the game version card + the "needs the loader" banner (texts only).</summary>
    void UpdateScriptLoaderCard()
    {
        var s = _ldr;
        string value, tone, tip;
        string? link = null;
        if (s == null) { value = "—"; tone = "ThAccent"; tip = "Set the game folder first"; }
        else if (_extra.ContainsKey("loader")) { value = "Remove waits for Apply"; tone = "ThWarn"; tip = "Apply takes the loader files out"; }
        else if (_extra.ContainsKey("loaderon")) { value = "Turn on waits for Apply"; tone = "ThWarn"; tip = "Apply puts the loader files in"; }
        else if (s.Present)
        {
            value = s.Mine ? "Ours" : "In place"; tone = "ThOk";
            link = s.Mine ? "Remove" : null;
            tip = "Kratos' script loads our Lua plug-ins (" + string.Join(", ", Loader.Files.Select(f => System.IO.Path.GetFileName(f.Game))) + ")"
                  + (s.Mine ? "" : "\nNot put in by this app, so it is not removed here.");
        }
        else if (s.Problem != null) { value = "Off · cannot run"; tone = "ThWarn"; tip = s.Problem; }
        else { value = "Off"; tone = "ThAccent"; link = "Turn on"; tip = "Off: mods that need it do not run. Turn on = three small files of ours; anything there is backed up first."; }
        ScriptLoaderValue.Text = value;
        ScriptLoaderCard.ToolTip = tip;
        OvTone(ScriptLoaderIco, ScriptLoaderIcon, tone);
        ScriptLoaderLink.Content = link;
        ScriptLoaderLink.Visibility = ScriptLoaderDot.Visibility = link == null ? Visibility.Collapsed : Visibility.Visible;

        GameVerTitle.Text = s?.ExeVersion is { } v ? "Game v" + v : "Game";
        GameVerValue.Text = s == null ? "—" : s.ExeVersion == null ? "Version not found" : "GoWR.exe";
        UpdateNeedBanner();
        UpdateHero(TopProblem());
    }

    /// <summary>"N mods need the script loader, and it is off" + Turn loader on (hidden while none waits).</summary>
    void UpdateNeedBanner()
    {
        int n = _loaderWait.Count;
        bool show = n > 0 && _ldr is { Present: false } && !_extra.ContainsKey("loaderon");
        NeedBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        NeedText.Text = $"{n} mod{(n == 1 ? " needs" : "s need")} the script loader, and it is off.";
        NeedText.ToolTip = string.Join(", ", WaitingNames());
        NeedAction.IsEnabled = _ldr?.Problem == null;
        NeedAction.ToolTip = _ldr?.Problem;
    }

    IEnumerable<string> WaitingNames() =>
        _catalog?.Groups.Where(g => _loaderWait.ContainsKey(g.Id)).Select(g => g.Name) ?? Enumerable.Empty<string>();

    /// <summary>The active mods whose preset runs through the loader: they stop when it goes out.</summary>
    List<string> LoaderUsers() =>
        _catalog?.Groups.Where(g => _active.TryGetValue(g.Id, out var i) && i >= 0 && g.Presets[i].UsesLoader).Select(g => g.Name).ToList()
        ?? new List<string>();

    /// <summary>The hero: the top problem (same order as the top banner), else "Ready to play".</summary>
    void UpdateHero((string Text, string Tone, string? Action, Action? OnAction)? p)
    {
        int active = ActiveChips.Children.Count;
        string title, sub, tone, icon;
        _heroAction = null;
        string? action = null;
        if (p is { } q)
        {
            int cut = q.Text.IndexOf(':');
            title = cut > 0 ? q.Text[..cut] : q.Text;
            sub = cut > 0 ? q.Text[(cut + 1)..].Trim() : "";
            tone = q.Tone == "Bad" ? "ThBad" : "ThWarn"; icon = "IcWarn";
            action = q.Action; _heroAction = q.OnAction;
        }
        else if (_loaderWait.Count > 0 && _ldr is { Present: false })
        {
            title = "Almost ready"; tone = "ThWarn"; icon = "IcWarn";
            sub = $"{_loaderWait.Count} mod{(_loaderWait.Count == 1 ? "" : "s")} wait{(_loaderWait.Count == 1 ? "s" : "")} for the script loader.";
        }
        else
        {
            title = "Ready to play"; tone = "ThOk"; icon = "IcCheck";
            sub = active == 0 ? "No mods active: the game is vanilla." : $"{active} mod{(active == 1 ? "" : "s")} active. Start the game from Steam.";
        }
        HeroTitle.Text = title;
        HeroSub.Text = sub;
        HeroSub.Visibility = sub.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        HeroIcon.Data = (Geometry)FindResource(icon);
        OvTone(HeroIconBox, HeroIcon, tone);
        HeroAction.Content = action;
        HeroAction.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
    }

    void HeroAction_Click(object sender, RoutedEventArgs e) => _heroAction?.Invoke();

    void OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        var game = _settings.GamePath;
        if (!GameLocator.IsGameDir(game)) { ShowToast("Set the game folder first", "Warn"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + game + "\"") { UseShellExecute = false })?.Dispose(); }
        catch (Exception ex) { ShowToast("Could not open the folder: " + ex.Message, "Bad"); }
    }

    void ChangeFolder_Click(object sender, RoutedEventArgs e) => Browse_Click(sender, e);

    void ScriptLoaderLink_Click(object sender, RoutedEventArgs e)
    {
        if ((ScriptLoaderLink.Content as string) == "Remove") StageLoaderRemove();
        else StageLoaderOn();
    }

    void TurnLoaderOn_Click(object sender, RoutedEventArgs e) => StageLoaderOn();

    /// <summary>"Turn loader on": one row of the Apply list (nothing is written before Apply).</summary>
    bool StageLoaderOn()
    {
        if (!GameLocator.IsGameDir(_settings.GamePath) || _dataRoot == null) return false;
        if (_ldr?.Problem is { } why) { ShowToast(why, "Warn"); return false; }
        string game = _settings.GamePath!, data = _dataRoot;
        _extra.Remove("loader");
        var names = WaitingNames().ToList();
        _extra["loaderon"] = new ExtraEdit("overview", "IcPlug", "Script loader",
            "Off → in the game" + (names.Count == 0 ? "" : " (runs again: " + string.Join(", ", names) + ")"),
            "checkpoint", () => InstallLoaderAsync(game, data), () => { _extra.Remove("loaderon"); UpdateScriptLoaderCard(); });
        UpdatePendingUi();
        UpdateScriptLoaderCard();
        return true;
    }

    async Task<bool> InstallLoaderAsync(string game, string data)
    {
        await _io.WaitAsync();
        try
        {
            var log = await Task.Run(() => Loader.Install(game, data));
            foreach (var l in log) Log("  " + l);
            Log("Script loader is in: load a checkpoint (or restart the game)", "Ok");
            _extra.Remove("loaderon");
            return true;
        }
        catch (Exception ex)
        {
            Log("Script loader: turn on FAILED: " + ex.Message, "Bad");
            return false;
        }
        finally
        {
            _io.Release();
            if (_loList != null) LoCheckLoader();
            _ = RefreshScriptLoaderAsync();
            _ = RefreshModsAsync();   // the waiting mods count as on again
        }
    }
}
