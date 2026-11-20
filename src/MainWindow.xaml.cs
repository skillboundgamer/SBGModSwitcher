using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ModSwitcher2;

public partial class MainWindow : Window
{
    /// <summary>Raise when the disclaimer text changes, so every player sees it once more.</summary>
    const int DisclaimerVersion = 1;

    readonly AppSettings _settings = AppSettings.Load();
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(2) };
    bool? _gameRunning;
    bool _checking;
    Action? _bannerAction;
    string? _saveError; // sticky: stays on the banner until a save works
    string? _startError; // sticky: a start-up failure stays visible
    Edition? _edition;   // data\edition.json: the same app with only some groups (Edition.cs); null = the normal app

    public MainWindow()
    {
        InitializeComponent();
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/logo/app.ico")); } catch { }   // window + taskbar icon
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += OnFirstLoaded;
        _watch.Tick += async (_, _) => { await CheckGameAsync(); await CheckOldSwitcherAsync(); };

        RadioButton[] tabs = { TabOverview, TabBoss, TabCompanions, TabValhalla, TabTweaks };
        for (int i = 0; i < tabs.Length; i++) NavTip(tabs[i], $"Ctrl+{i + 1}");
        if (LoadEdition()) tabs = ApplyEditionChrome(tabs);
#if SBG_DEV
        Title = "SBG Switcher DEV";   // our own build (csproj SBG_DEV): never mixed up with the public installer
#endif
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            InputBindings.Add(new KeyBinding(new Relay(() => { if (!Rail.IsEnabled) return; tab.IsChecked = true; tab.Focus(); }),
                                             Key.D1 + i, ModifierKeys.Control));
        }
        InputBindings.Add(new KeyBinding(new Relay(() => { if (SearchBox.IsVisible) SearchText.Focus(); }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(FocusTopSearch), Key.K, ModifierKeys.Control));
        SearchRowsBuild();
        InputBindings.Add(new KeyBinding(new Relay(() => { if (Rail.IsEnabled && PendingCount > 0) OpenReview(); }), Key.S, ModifierKeys.Control));
        // Coming back from the old Switcher or the game: check the files again (cheap, results are cached).
        Activated += (_, _) => { if (DateTime.UtcNow - _lastModCheck > TimeSpan.FromSeconds(3)) _ = RefreshModsAsync(); };
    }

    /// <summary>Read data\edition.json. A broken file gives an empty edition (no tabs, no groups) and a start-up banner.</summary>
    bool LoadEdition()
    {
        LoadPacks();
        if (_packMode) { _edition = MergedPacksEdition(); return true; }
        try { _edition = Edition.Load(DataRoot.Find()); }
        catch (Exception ex)
        {
            _edition = new Edition { Title = "Edition error" };
            _startError = ex.Message;
        }
        return _edition != null;
    }

    /// <summary>An edition: its name in the title and the rail, only its tabs. Returns the tabs left (for Ctrl+1..).</summary>
    RadioButton[] ApplyEditionChrome(RadioButton[] tabs)
    {
        Title = _edition!.Title;
        RailTitle.Text = _edition.Title;
        var left = new List<RadioButton>();
        foreach (var t in tabs)
        {
            if (TabKeys.TryGetValue(t, out var key) && !_edition.ShowsTab(key)) { t.Visibility = Visibility.Collapsed; continue; }
            left.Add(t);
            NavTip(t, $"Ctrl+{left.Count}");
        }
        ShowCommunity();
        return left.ToArray();
    }

    Dictionary<RadioButton, string> TabKeys => new() { [TabBoss] = "boss", [TabCompanions] = "companions", [TabValhalla] = "valhalla", [TabTweaks] = "tweaks" };

    void ShowCommunity()
    {
        // Community row: URLs were checked when the edition was read (Edition.SafeUrl); empty = button hidden.
        DiscordButton.Tag = _edition.Discord;
        NexusButton.Tag = _edition.Nexus;
        DiscordButton.Visibility = _edition.Discord.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NexusButton.Visibility = _edition.Nexus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CommunityRow.Visibility = _edition.Discord.Length + _edition.Nexus.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Open the button's link in the default browser: one Process.Start, no other work.</summary>
    void Community_Click(object sender, RoutedEventArgs e)
    {
        var url = Edition.SafeUrl((sender as Button)?.Tag as string);   // checked again: only https and our sites
        if (url.Length == 0) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch { /* no browser: nothing to do */ }
    }

    async void OnFirstLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnFirstLoaded; // Loaded can fire again; start-up runs once
        try { await StartAsync(); }
        catch (Exception ex) { _startError = ex.Message; UpdateBanner(); }
    }

    /// <summary>The app version as the release names it: 2.0.4, or 2.0.4.1 for a hotfix (the 4th number only when set).</summary>
    internal static string AppVersionText()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v == null) return "2";
        return v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    async Task StartAsync()
    {
        VersionValue.Text = "v" + (_edition != null && !_packMode ? _edition.Version : AppVersionText());

        BuildModCards();
        BuildSetupCard();
        BuildUpdateCard();   // UpdateView.cs: the Settings card (GitHub build: the switch; Nexus build: text only)
        RebuildSearch();   // off the UI thread (SearchIndex.cs)
        GuideButton.Visibility = _packMode ? Visibility.Visible : Visibility.Collapsed;
        if (GuideDemoOut != null) { await RunGuideDemoAsync(); return; }   // hidden --guide-demo mode (GuideDemo.cs)
        bool demo = PerfOut != null || PackDemoOut != null;   // the timing / demo modes take their own screenshots
        if (_packMode && !_settings.GuideDone && !demo) ShowGuide();   // the guide holds the disclaimer as its step 3
        else if (_settings.DisclaimerAccepted < DisclaimerVersion) ShowDisclaimer();
        if (PerfOut != null) { await RunPerfAsync(); return; }   // hidden --perf timing mode (Perf.cs)
        if (PackDemoOut != null) { await RunPackDemoAsync(); return; }   // hidden --pack-demo mode (PacksDemo.cs)

        // Path detection reads the registry and Steam's library list: off the UI thread.
        if (!GameLocator.IsGameDir(_settings.GamePath))
        {
            var found = await Task.Run(GameLocator.Detect);
            if (found != null) { _settings.GamePath = found; TrySave(); }
        }
        RefreshStatus();
        StartUpdateCheck();   // UpdateView.cs: once, off the UI thread (GitHub build; the Nexus build does nothing)
        GuideRefresh();   // the search may have found the game after the guide was drawn
        await CheckGameAsync();
        _watch.Start();
        await FirstOpenSpeedAsync();   // SpeedFirstOpen.cs: a custom walk speed in the MIO ini goes back to default, once
    }

    // ---------- status ----------

    void RefreshStatus()
    {
        var path = _settings.GamePath;
        bool ok = GameLocator.IsGameDir(path);
        PathBox.Text = ok ? path : "Not set";
        FolderValue.Text = ok ? "Found" : "Not set";
        OvTone(FolderIco, FolderIcon, ok ? "ThOk" : "ThBad");
        FolderValue.ToolTip = ok ? path : null;

        bool loader = ok && GameLocator.HasModLoader(path!);
        UpdateLoaderChip(ok, loader);
        if (_searchBuilt) RebuildSearch();   // a new game folder: its Back blades presets
        UpdateBanner();
        _ = RefreshModsAsync();
        _ = RefreshVotgAsync();   // VotgView.cs: scans once per game folder, off the UI thread
        _ = RefreshScriptLoaderAsync();   // OverviewView.cs: loader files + GoWR.exe version, off the UI thread
        FillLanguages();
        _ = EnsureItemsAsync(false);   // the loadout item list from this PC's game files (8b), once per language
    }

    async Task CheckGameAsync()
    {
        if (_checking) return;
        _checking = true;
        try
        {
            bool running = await Task.Run(GameLocator.IsGameRunning);
            if (running == _gameRunning) return;
            _gameRunning = running;
            RestartWatch(running);
            SetChip(GameDot, GameDotText, running ? "Game running" : "Game idle", running ? "ThWarn" : "ThOk");
            UpdateBanner();
        }
        catch (Exception) { /* a process query can fail during shutdown; try again on the next tick */ }
        finally { _checking = false; }
    }

    /// <summary>The most important problem first: text, tone, button, action; null = none. The top banner shows it on every
    /// page but the Overview, where the hero card says it (OverviewView.cs UpdateHero).</summary>
    (string Text, string Tone, string? Action, Action? OnAction)? TopProblem()
    {
        if (_startError != null) return ("Start-up failed: " + _startError, "Bad", null, null);
        if (_saveError != null) return ("Could not save settings: " + _saveError, "Bad", null, null);
        if (!GameLocator.IsGameDir(_settings.GamePath))
            return ("Game folder not found", "Bad", "Set folder", () => { TabSettings.IsChecked = true; Browse_Click(this, new RoutedEventArgs()); });
        if (!GameLocator.HasModLoader(_settings.GamePath!)) return ("Mod loader (GoWR MIO) not installed: mods will not load", "Warn", null, null);
        if (_restartPending.Count > 0)
            return ($"Restart the game to apply {_restartPending.Count} change{(_restartPending.Count == 1 ? "" : "s")}: "
                    + string.Join(", ", _restartPending), "Warn", null, null);
        if (_oldSwitcherOpen) return ("The old Mod Switcher is open: both write the same files. Use one at a time.", "Warn", null, null);
        if (_gameRunning == true) return ("Game is running: some changes need it closed", "Warn", null, null);
        return null;
    }

    /// <summary>One banner, most important problem first; hidden on the Overview, where the hero says it.</summary>
    void UpdateBanner()
    {
        var p = TopProblem();
        if (p is { } q && TabOverview.IsChecked != true) ShowBanner(q.Text, q.Tone, q.Action, q.OnAction);
        else Banner.Visibility = Visibility.Collapsed;
        UpdateHero(p);
    }

    void ShowBanner(string text, string tone, string? action, Action? onAction)
    {
        BannerText.Text = text;
        BannerText.Foreground = Brush(tone);
        BannerIcon.Stroke = Brush(tone);
        Banner.Background = Brush(tone == "Bad" ? "BadBg" : "WarnBg");
        _bannerAction = onAction;
        BannerAction.Content = action;
        BannerAction.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        Banner.Visibility = Visibility.Visible;
    }

    void BannerAction_Click(object sender, RoutedEventArgs e) => _bannerAction?.Invoke();

    Brush Brush(string key) => (Brush)FindResource(key);

    // ---------- tabs and rail ----------

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewOverview == null) return; // fires once during InitializeComponent
        var name = (string)((RadioButton)sender).Tag;
        PageTitle.Text = name;
        ViewOverview.Visibility = Show(name == "Overview");
        ViewBoss.Visibility = Show(name == "Boss Fights");
        ViewCompanions.Visibility = Show(name == "Companions");
        ViewValhalla.Visibility = Show(name == "Valhalla");
        ViewTweaks.Visibility = Show(name == "Game Tweaks");
        ViewSettings.Visibility = Show(name == "Settings");
        SearchBox.Visibility = Show(name is "Boss Fights" or "Companions" or "Valhalla" or "Game Tweaks");
        if (SearchPanel.IsVisible) SearchClose();
        UpdateBanner();   // the top banner hides on the Overview (the hero says it there)
    }

    static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The sidebar key hint ("Ctrl+2"): shown beside the page name when the rail slides open (Theme.xaml ThNav)
    /// and read by screen readers. No tooltip: the open rail shows the names.</summary>
    static void NavTip(RadioButton tab, string keys) => System.Windows.Automation.AutomationProperties.SetHelpText(tab, keys);

    /// <summary>The top bar's loader chip: the same check as the Overview "Mod loader" tile.</summary>
    void UpdateLoaderChip(bool folderOk, bool loader) =>
        SetChip(LoaderDot, LoaderChipText, !folderOk ? "No game folder" : loader ? "Loader ready" : "Loader missing",
                !folderOk ? "ThBad" : loader ? "ThOk" : "ThWarn");

    /// <summary>A top-bar chip: dot colour + text (Theme.xaml tokens).</summary>
    void SetChip(System.Windows.Shapes.Ellipse dot, TextBlock text, string value, string tone)
    {
        dot.Fill = Brush(tone);
        text.Text = value;
        text.Foreground = Brush(tone == "ThBad" || tone == "ThWarn" ? tone : "ThMuted");
    }

    RadioButton? TabFor(string tab) => tab switch
    {
        "overview" => TabOverview, "boss" => TabBoss, "companions" => TabCompanions, "valhalla" => TabValhalla,
        "tweaks" => TabTweaks, "settings" => TabSettings, _ => null,
    };

    Panel? ViewFor(string tab) => tab switch
    {
        "overview" => ViewOverview, "boss" => ViewBoss, "companions" => ViewCompanions, "valhalla" => ViewValhalla,
        "tweaks" => ViewTweaks, "settings" => ViewSettings, _ => null,
    };

    // ---------- settings ----------

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Pick the God of War Ragnarök folder (the one with GoWR.exe)" };
        if (GameLocator.IsGameDir(_settings.GamePath)) dlg.InitialDirectory = _settings.GamePath;
        if (dlg.ShowDialog(this) != true) return;

        if (!GameLocator.IsGameDir(dlg.FolderName))
        {
            MessageBox.Show(this, $"{GameLocator.ExeName} is not in that folder.", "Wrong folder",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _settings.GamePath = dlg.FolderName;
        TrySave();
        RefreshStatus();
    }

    async void Detect_Click(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        btn.IsEnabled = false;
        var found = await Task.Run(GameLocator.Detect);
        btn.IsEnabled = true;
        if (found == null)
        {
            MessageBox.Show(this, "Could not find the game in your Steam libraries. Use Browse.", "Not found",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _settings.GamePath = found;
        TrySave();
        RefreshStatus();
    }

    void OpenSettingsDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppSettings.Dir}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) { ShowBanner("Could not open the settings folder: " + ex.Message, "Bad", null, null); }
    }

    void TrySave()
    {
        try { _settings.Save(); _saveError = null; }
        catch (Exception ex) { _saveError = ex.Message; }
        UpdateBanner();
    }

    // ---------- disclaimer ----------

    void ShowDisclaimer_Click(object sender, RoutedEventArgs e) => ShowDisclaimer();

    void ShowDisclaimer()
    {
        AgreeBox.IsChecked = _settings.DisclaimerAccepted >= DisclaimerVersion;
        Disclaimer.Visibility = Visibility.Visible;
        MainArea.IsEnabled = Rail.IsEnabled = false; // keeps Tab focus inside the box
        AgreeBox.Focus();
    }

    void Agree_Click(object sender, RoutedEventArgs e)
    {
        _settings.DisclaimerAccepted = DisclaimerVersion;
        TrySave();
        Disclaimer.Visibility = Visibility.Collapsed;
        MainArea.IsEnabled = Rail.IsEnabled = true;
    }

    // ---------- window chrome ----------

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void UseDarkTitleBar()
    {
        int on = 1;
        var hwnd = new WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? p) => true;
        public void Execute(object? p) => run();
    }
}
