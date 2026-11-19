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

    async Task StartAsync()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionValue.Text = _edition != null && !_packMode ? "v" + _edition.Version : v == null ? "v2" : $"v{v.Major}.{v.Minor}.{v.Build}";

        BuildModCards();
        BuildSetupCard();
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

    // ---------- top search (revamp batch A) ----------
    // Speed: the index is built off the UI thread (RebuildSearch); a key press runs SearchIndex.Query on ~150 small
    // strings and refills at most 8 rows that were made once (SearchRowsBuild): no card, list or file is touched.

    const int SearchMax = 8;
    volatile SearchIndex _search = SearchIndex.Empty;
    int _searchGen;           // the newest RebuildSearch wins
    bool _searchBuilt;
    List<SearchHit> _hits = new();
    int _hitSel;
    sealed class SearchSlot
    {
        public TextBlock Head = null!;
        public Border Row = null!, IconBox = null!;
        public System.Windows.Shapes.Path Icon = null!;
        public Run Before = null!, Match = null!, After = null!;
        public TextBlock Where = null!;
    }
    readonly List<SearchSlot> _slots = new();

    /// <summary>Internal: the guide's step names for the search's Help rows (GuideView.cs GuideNames).</summary>
    internal static IReadOnlyList<string> GuideStepNames => GuideNames;

    /// <summary>Collects the entries on the UI thread (a copy of the group list, cheap), builds the index on a worker.</summary>
    void RebuildSearch()
    {
        var cat = _catalog == null ? null : new ModCatalog { Groups = _catalog.Groups.ToList() };
        bool full = _edition == null, loadout = _edition?.Loadout == true;
        string? game = GameLocator.IsGameDir(_settings.GamePath) ? _settings.GamePath : null;
        int gen = ++_searchGen;
        _searchBuilt = true;
        _ = Task.Run(() =>
        {
            try
            {
                var idx = SearchIndex.Build(SearchIndex.Collect(cat, full, loadout, game, GuideNames));
                Dispatcher.InvokeAsync(() => { if (gen == _searchGen) { _search = idx; if (SearchPanel.IsVisible) TopSearchFill(); } });
            }
            catch (Exception) { /* search stays on the last index; the app works without it */ }
        });
    }

    /// <summary>The 8 result rows + group heads, made once; a query only sets their texts and visibility.</summary>
    void SearchRowsBuild()
    {
        for (int i = 0; i < SearchMax; i++)
        {
            var s = new SearchSlot();
            s.Head = new TextBlock { Style = (Style)FindResource("ThGroupHead"), Visibility = Visibility.Collapsed };
            s.Icon = new System.Windows.Shapes.Path { Style = (Style)FindResource("Icon"), HorizontalAlignment = HorizontalAlignment.Center,
                                                      VerticalAlignment = VerticalAlignment.Center, LayoutTransform = new ScaleTransform(0.72, 0.72) };
            s.IconBox = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(9), Child = s.Icon, Margin = new Thickness(0, 0, 12, 0) };
            DockPanel.SetDock(s.IconBox, Dock.Left);
            s.Where = new TextBlock { Foreground = Brush("ThMuted"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            DockPanel.SetDock(s.Where, Dock.Right);
            s.Before = new Run(); s.Match = new Run { Foreground = Brush("ThAccent") }; s.After = new Run();
            var title = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Brush("ThText"), VerticalAlignment = VerticalAlignment.Center,
                                        TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(s.Before); title.Inlines.Add(s.Match); title.Inlines.Add(s.After);
            var dock = new DockPanel();
            dock.Children.Add(s.IconBox); dock.Children.Add(s.Where); dock.Children.Add(title);
            s.Row = new Border { Style = (Style)FindResource("ThResultRow"), Child = dock, Visibility = Visibility.Collapsed };
            int at = i;
            s.Row.MouseEnter += (_, _) => SelectHit(at);
            s.Row.MouseLeftButtonUp += (_, e) => { e.Handled = true; SelectHit(at); OpenHit(); };
            SearchRows.Children.Add(s.Head);
            SearchRows.Children.Add(s.Row);
            _slots.Add(s);
        }
    }

    void FocusTopSearch()
    {
        if (!MainArea.IsEnabled) return;   // a box (review, guide, disclaimer) is open
        TopSearch.Focus();
        TopSearch.SelectAll();
    }

    void TopSearchBox_MouseDown(object sender, MouseButtonEventArgs e) { if (!TopSearch.IsKeyboardFocusWithin) { TopSearch.Focus(); e.Handled = true; } }

    void TopSearch_Changed(object sender, TextChangedEventArgs e)
    {
        TopSearchHint.Visibility = TopSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TopSearchFill();
    }

    /// <summary>The query: at most 8 hits into the 8 reused rows; the panel shows while there is text.</summary>
    void TopSearchFill()
    {
        var q = TopSearch.Text;
        if (q.Trim().Length == 0) { SearchClose(); return; }
        _hits = _search.Query(q, SearchMax);
        SearchGroup? last = null;
        for (int i = 0; i < _slots.Count; i++)
        {
            var s = _slots[i];
            if (i >= _hits.Count) { s.Row.Visibility = s.Head.Visibility = Visibility.Collapsed; continue; }
            var h = _hits[i];
            var en = h.Entry;
            bool head = en.Group != last;
            last = en.Group;
            s.Head.Text = en.Group.ToString().ToUpperInvariant();
            s.Head.Visibility = head ? Visibility.Visible : Visibility.Collapsed;
            s.Before.Text = en.Title[..h.MatchStart];
            s.Match.Text = en.Title.Substring(h.MatchStart, h.MatchLength);
            s.After.Text = en.Title[(h.MatchStart + h.MatchLength)..];
            s.Where.Text = HitWhere(en);
            string tone = en.Group == SearchGroup.Settings ? "ThWarn" : "ThAccent";
            s.Icon.Data = TryFindResource(en.Icon) as Geometry ?? (Geometry)FindResource("IcInfo");
            s.Icon.Stroke = Brush(tone);
            s.IconBox.Background = Brush(en.Group == SearchGroup.Settings ? "ThWarnBg" : "ThAccentBg");
            s.Row.Visibility = Visibility.Visible;
        }
        SearchNone.Visibility = _hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectHit(0);
        SearchOpen();
    }

    /// <summary>The right-hand text: the place, and for a mod card its preset now ("Game Tweaks · Normal").</summary>
    string HitWhere(SearchEntry en)
    {
        if (!en.Target.StartsWith("group:") || en.Where.EndsWith("preset")) return en.Where;
        var id = en.Target[6..];
        if (!_cards.TryGetValue(id, out var c) || !_active.TryGetValue(id, out int idx)) return en.Where;
        string now = idx >= 0 && idx < c.Group.Presets.Count ? c.Group.Presets[idx].Label : "Custom";
        return en.Where + " · " + now;
    }

    void SelectHit(int i)
    {
        if (_hits.Count == 0) { _hitSel = -1; return; }
        _hitSel = Math.Clamp(i, 0, _hits.Count - 1);
        for (int k = 0; k < _slots.Count; k++)
            _slots[k].Row.Background = k == _hitSel ? Brush("ThAccentBg") : Brushes.Transparent;
    }

    void TopSearch_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: if (SearchPanel.IsVisible) SelectHit(_hitSel + 1); e.Handled = true; break;
            case Key.Up: if (SearchPanel.IsVisible) SelectHit(_hitSel - 1); e.Handled = true; break;
            case Key.Enter: OpenHit(); e.Handled = true; break;
            case Key.Escape:
                if (TopSearch.Text.Length > 0) TopSearch.Text = "";
                else LeaveTopSearch();
                e.Handled = true;
                break;
        }
    }

    void TopSearch_GotFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        TopSearchBox.BorderBrush = Brush("ThAccent");
        TopSearchGlow.BorderBrush = Brush("ThAccentBg");
        TopSearchKbdText.Text = "Esc";
        if (TopSearch.Text.Trim().Length > 0) TopSearchFill();
    }

    void TopSearch_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        TopSearchBox.BorderBrush = Brush("ThLine");
        TopSearchGlow.BorderBrush = Brushes.Transparent;
        TopSearchKbdText.Text = "Ctrl K";
        SearchClose();
    }

    void SearchScrim_MouseDown(object sender, MouseButtonEventArgs e) { e.Handled = true; LeaveTopSearch(); }

    void SearchOpen() { SearchPanel.Visibility = Visibility.Visible; SearchScrim.Visibility = Visibility.Visible; }
    void SearchClose() { SearchPanel.Visibility = Visibility.Collapsed; SearchScrim.Visibility = Visibility.Collapsed; }

    /// <summary>Esc on an empty box / a click beside the panel: close it and give the focus back to the page.</summary>
    void LeaveTopSearch()
    {
        SearchClose();
        var tab = new[] { TabOverview, TabBoss, TabCompanions, TabValhalla, TabTweaks, TabSettings }.FirstOrDefault(t => t.IsChecked == true);
        if (tab != null) tab.Focus(); else Keyboard.ClearFocus();
    }

    /// <summary>Enter / click: open the page and scroll to the card, which flashes once. Actions run their button.</summary>
    void OpenHit()
    {
        if (_hitSel < 0 || _hitSel >= _hits.Count) return;
        var en = _hits[_hitSel].Entry;
        TopSearch.Text = "";
        LeaveTopSearch();
        if (en.Target.StartsWith("action:")) { RunSearchAction(en.Target[7..]); return; }

        FrameworkElement? card = null;
        if (en.Target.StartsWith("group:")) card = _cards.TryGetValue(en.Target[6..], out var c) ? c.Root : null;
        else if (en.Target.StartsWith("card:") && ViewFor(en.Tab) is { } view) card = FindCard(view, en.Target[5..]);
        var tab = TabFor(en.Tab);
        if (tab == null || tab.Visibility != Visibility.Visible || card == null)
        {
            ShowToast($"\"{en.Title}\" is not in this version of the app", "Warn");
            return;
        }
        if (!IsShown(card, ViewFor(en.Tab)!))
        {
            // a Lab card while "Show experimental" is off: go to that switch instead
            if (FindCard(ViewSettings, "Show experimental") is { } exp)
            {
                TabSettings.IsChecked = true;
                ShowToast("Turn on Show experimental to see " + en.Title, "Warn");
                FlashCard(exp);
            }
            return;
        }
        tab.IsChecked = true;
        tab.Focus();   // the keyboard stays on the page that opened (Esc / arrows work from there)
        FlashCard(card);
    }

    /// <summary>Scroll the card into view after the page's first layout, then one short accent flash of its border.</summary>
    void FlashCard(FrameworkElement card)
    {
        Dispatcher.InvokeAsync(() =>
        {
            card.BringIntoView();
            if (card is not Border b) return;
            var line = ((SolidColorBrush)Brush("Line")).Color;
            var flash = new SolidColorBrush(((SolidColorBrush)Brush("ThAccent")).Color);
            b.BorderBrush = flash;
            var anim = new ColorAnimation(line, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(350) };
            anim.Completed += (_, _) => b.ClearValue(Border.BorderBrushProperty);
            flash.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>True when the card and every parent up to the page are Visible (a Lab card is hidden with its box).</summary>
    static bool IsShown(FrameworkElement el, Panel page)
    {
        for (DependencyObject? d = el; d != null && d != page; d = LogicalTreeHelper.GetParent(d))
            if (d is UIElement u && u.Visibility != Visibility.Visible) return false;
        return true;
    }

    /// <summary>The card on a page whose header text is TITLE (or "TITLE · n"): the outermost Border above that text,
    /// below the page. Runs on Enter only (a walk of one page's logical tree).</summary>
    static FrameworkElement? FindCard(Panel page, string title)
    {
        TextBlock? hit = null;
        var stack = new Stack<object>();
        stack.Push(page);
        while (stack.Count > 0 && hit == null)
        {
            var o = stack.Pop();
            if (o is TextBlock tb && (tb.Text == title || tb.Text.StartsWith(title + " ·"))) { hit = tb; break; }
            if (o is DependencyObject d)
                foreach (var ch in LogicalTreeHelper.GetChildren(d).Cast<object>().Reverse()) stack.Push(ch);
        }
        if (hit == null) return null;
        FrameworkElement? card = null;
        for (DependencyObject? d = hit; d != null && d != page; d = LogicalTreeHelper.GetParent(d))
            if (d is Border b) card = b;
        return card ?? hit;
    }

    void RunSearchAction(string a)
    {
        var ev = new RoutedEventArgs();
        if (a == "settingsdir") OpenSettingsDir_Click(this, ev);
        else if (a == "bugreport") CopyBugReport_Click(this, ev);
        else if (a == "disclaimer") ShowDisclaimer();
        else if (a == "activity") { if (ActivityList.Visibility != Visibility.Visible) ActivityToggle_Click(this, ev); }
        else if (a.StartsWith("guide")) ShowGuide(a.Length > 6 && int.TryParse(a[6..], out int n) ? n : 0);
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
