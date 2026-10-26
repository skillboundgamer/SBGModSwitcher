using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    const double RailClosed = 64, RailOpen = 210;

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
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += OnFirstLoaded;
        _watch.Tick += async (_, _) => { await CheckGameAsync(); await CheckOldSwitcherAsync(); };

        RadioButton[] tabs = { TabOverview, TabBoss, TabCompanions, TabValhalla, TabTweaks };
        if (LoadEdition()) tabs = ApplyEditionChrome(tabs);
        for (int i = 0; i < tabs.Length; i++)
        {
            var tab = tabs[i];
            InputBindings.Add(new KeyBinding(new Relay(() => { if (!Rail.IsEnabled) return; tab.IsChecked = true; tab.Focus(); }),
                                             Key.D1 + i, ModifierKeys.Control));
        }
        InputBindings.Add(new KeyBinding(new Relay(() => { if (SearchBox.IsVisible) SearchText.Focus(); }), Key.F, ModifierKeys.Control));
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
            t.ToolTip = $"{t.Tag} (Ctrl+{left.Count})";
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

    async Task StartAsync()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionValue.Text = _edition != null && !_packMode ? "v" + _edition.Version : v == null ? "v2" : $"v{v.Major}.{v.Minor}.{v.Build}";

        BuildModCards();
        BuildSetupCard();
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
        GuideRefresh();   // the search may have found the game after the guide was drawn
        await CheckGameAsync();
        _watch.Start();
    }

    // ---------- status ----------

    void RefreshStatus()
    {
        var path = _settings.GamePath;
        bool ok = GameLocator.IsGameDir(path);
        PathBox.Text = ok ? path : "Not set";
        SetStat(FolderValue, FolderIcon, ok ? "Found" : "Not set", ok ? "Ok" : "Bad");
        FolderValue.ToolTip = ok ? path : null;

        bool loader = ok && GameLocator.HasModLoader(path!);
        SetStat(LoaderValue, LoaderIcon, !ok ? "—" : loader ? "Ready" : "Missing", !ok ? "Dim" : loader ? "Ok" : "Warn");
        UpdateBanner();
        _ = RefreshModsAsync();
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
            SetStat(GameValue, GameIcon, running ? "Running" : "Idle", running ? "Warn" : "Ok");
            GameDot.Fill = Brush(running ? "Warn" : "Ok");
            GameDotText.Text = running ? "Game running" : "Game idle";
            UpdateBanner();
        }
        catch (Exception) { /* a process query can fail during shutdown; try again on the next tick */ }
        finally { _checking = false; }
    }

    /// <summary>One banner, most important problem first.</summary>
    void UpdateBanner()
    {
        if (_startError != null)
            ShowBanner("Start-up failed: " + _startError, "Bad", null, null);
        else if (_saveError != null)
            ShowBanner("Could not save settings: " + _saveError, "Bad", null, null);
        else if (!GameLocator.IsGameDir(_settings.GamePath))
            ShowBanner("Game folder not found", "Bad", "Set folder", () => { TabSettings.IsChecked = true; Browse_Click(this, new RoutedEventArgs()); });
        else if (!GameLocator.HasModLoader(_settings.GamePath!))
            ShowBanner("Mod loader (GoWR MIO) not installed: mods will not load", "Warn", null, null);
        else if (_restartPending.Count > 0)
            ShowBanner($"Restart the game to apply {_restartPending.Count} change{(_restartPending.Count == 1 ? "" : "s")}: "
                       + string.Join(", ", _restartPending), "Warn", null, null);
        else if (_oldSwitcherOpen)
            ShowBanner("The old Mod Switcher is open: both write the same files. Use one at a time.", "Warn", null, null);
        else if (_gameRunning == true)
            ShowBanner("Game is running: some changes need it closed", "Warn", null, null);
        else
            Banner.Visibility = Visibility.Collapsed;
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

    void SetStat(TextBlock value, System.Windows.Shapes.Path icon, string text, string tone)
    {
        value.Text = text;
        icon.Stroke = Brush(tone == "Dim" ? "Acc" : tone);
    }

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
    }

    static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    void Rail_Open(object sender, MouseEventArgs e) => AnimateRail(true);
    void Rail_Close(object sender, MouseEventArgs e) => AnimateRail(false);
    void Rail_FocusChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Keyboard users get the labels too (Tab into the rail opens it).
        if (Rail.IsKeyboardFocusWithin && InputManager.Current.MostRecentInputDevice is KeyboardDevice) AnimateRail(true);
        else if (!Rail.IsKeyboardFocusWithin && !Rail.IsMouseOver) AnimateRail(false);
    }

    void AnimateRail(bool open)
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(160);
        Rail.BeginAnimation(WidthProperty, new DoubleAnimation(open ? RailOpen : RailClosed, dur) { EasingFunction = ease });
        RailShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
                                  new DoubleAnimation(open ? 0.55 : 0, dur));
    }

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
