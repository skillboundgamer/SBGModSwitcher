using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ModSwitcher2;

/// <summary>The mod cards of the Boss Fights / Companions / Valhalla / Game Tweaks tabs and the Overview chips.
/// A click only marks an edit (see Pending.cs); nothing is written before Apply.</summary>
public partial class MainWindow
{
    sealed class Card
    {
        public required ModGroup Group;
        public required Border Root;
        public required FrameworkElement Spinner;
        public required Border CustomPill;
        public required Border EditedTag;
        public required TextBlock WarnLine;                       // rule warnings (phase 7), amber, under the presets
        public readonly List<(Border Box, TextBlock Text)> Presets = new();
        public readonly Dictionary<int, string> Missing = new();  // preset index -> source file not found
    }

    ModCatalog? _catalog;
    string? _dataRoot;
    readonly Dictionary<string, ModMatcher> _matchers = new(StringComparer.OrdinalIgnoreCase);
    string? _matcherGame;
    readonly Dictionary<string, Card> _cards = new();
    readonly Dictionary<string, int> _active = new();
    DateTime _lastModCheck = DateTime.MinValue;
    bool _modChecking, _modCheckAgain;
    readonly SemaphoreSlim _io = new(1, 1);

    // ---------- build ----------

    void BuildModCards()
    {
        if (_packMode)
        {
            // packs only (the release app): each pack is its own data root; the loadout uses the first pack that has it
            _catalog = new ModCatalog();
            foreach (var p in _packs) AddPackGroups(p);
            _dataRoot = _packs.FirstOrDefault(p => p.Info.Loadout)?.Root;
            BuildPacksSection();
        }
        else
        {
            _dataRoot = DataRoot.Find();
            if (_dataRoot == null) { ActiveEmpty.Content = "Mod list (mods.json) not found"; return; }
            try { _catalog = ModCatalog.Load(DataRoot.CatalogPath(_dataRoot), _edition); }
            catch (Exception ex) { ActiveEmpty.Content = "Mod list could not be read: " + ex.Message; return; }
            foreach (var g in _catalog.Groups) g.Root = _dataRoot;
        }

        foreach (var g in _catalog.Groups)
        {
            var card = MakeCard(g);
            _cards[g.Id] = card;
            TabPanel(g.Tab).Children.Add(card.Root);
        }
        if (_edition != null)   // an edition has no tweaks or Lab; the loadout and its item list only with "loadout": true
        {
            foreach (var cs in _edition.ComingSoon) TabPanel(cs.Tab).Children.Add(MakeComingSoonCard(cs));
            if (_edition.Loadout && _dataRoot != null) BuildLoadoutOnce();
            return;
        }
        BuildLoadoutCard();
        BuildTweakCards();
        BuildLabCards();
        BuildItemsSettingsCard();
    }

    WrapPanel TabPanel(string tab) => tab switch
    {
        "companions" => ViewCompanions,
        "valhalla" => ViewValhalla,
        "tweaks" => ViewTweaks,
        _ => ViewBoss,
    };

    Card MakeCard(ModGroup g)
    {
        bool tiles = g.Tab == "companions";
        var root = new Border { Style = (Style)FindResource("CardBox"), Width = tiles ? 720 : g.Presets.Count > 5 ? 640 : 380 };
        var body = new StackPanel();
        root.Child = body;

        // header: icon (note on hover), name, spinner, reload badge
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var (badgeIcon, badgeTone, badgeTip) = ReloadBadge(g.Reload);
        var badge = new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(8), ToolTip = badgeTip,
            Background = Brush(badgeTone + "Bg"),
            Child = IconPath(badgeIcon, badgeTone, 0.65),
        };
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        var spinner = Spinner();
        DockPanel.SetDock(spinner, Dock.Right);
        head.Children.Add(spinner);
        var edited = EditedPill();
        DockPanel.SetDock(edited, Dock.Right);
        head.Children.Add(edited);
        var ico = new Border { Style = (Style)FindResource("IcoBox"), Child = IconPath(g.Icon, "Acc", 1), ToolTip = g.Note };
        head.Children.Add(ico);
        head.Children.Add(new TextBlock
        {
            Text = g.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = g.Note,
        });
        body.Children.Add(head);

        var custom = new Border
        {
            Style = (Style)FindResource("Pill"), Background = Brush("WarnBg"), Visibility = Visibility.Collapsed,
            ToolTip = "The files match none of these presets (changed by hand or by another tool)",
            Child = new TextBlock { Text = "Custom", Foreground = Brush("Warn") },
        };
        var warn = new TextBlock { Foreground = Brush("Warn"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        var card = new Card { Group = g, Root = root, Spinner = spinner, CustomPill = custom, EditedTag = edited, WarnLine = warn };
        // a preset whose source file is missing cannot be applied: greyed, the reason on hover (phase 7)
        if (g.Root.Length > 0)
            for (int i = 0; i < g.Presets.Count; i++)
                foreach (var st in g.Presets[i].Steps)
                    if (st.Length > 1 && st[0] is "put" or "putrestart" or "recipe" or "reciperestart" && !System.IO.File.Exists(System.IO.Path.Combine(g.Root, st[1])))
                    { card.Missing[i] = st[1]; break; }

        var row = new WrapPanel();
        for (int pi = 0; pi < g.Presets.Count; pi++)
        {
            var p = g.Presets[pi];
            int index = pi;
            var label = new TextBlock { Text = p.Label, Foreground = Brush("Dim"), HorizontalAlignment = HorizontalAlignment.Center };
            var tip = p.Disabled ? $"{p.Name}\n{p.DisabledWhy ?? "Coming soon"}" : p.Warn == null ? p.Name : $"{p.Name}\n⚠ {p.Warn}";
            var box = new Border
            {
                Style = (Style)FindResource("Pill"), Opacity = p.Disabled ? 0.35 : 1,
                BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
            };
            if (tiles)
            {
                var st = new StackPanel { Width = 72 };
                st.Children.Add(Avatar(p.Label));
                st.Children.Add(label);
                label.Margin = new Thickness(0, 6, 0, 0);
                box.Child = st;
                box.Padding = new Thickness(8, 12, 8, 10);
                box.Margin = new Thickness(0, 0, 8, 8);
                box.CornerRadius = new CornerRadius(14);
            }
            else if (p.Warn != null)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(IconPath("IcWarn", "Warn", 0.55));
                sp.Children.Add(label);
                box.Child = sp;
            }
            else box.Child = label;
            if (card.Missing.TryGetValue(pi, out var miss))
            {
                // the release leaves out what cannot ship (the probe's main.lua is a game script: notes/16 3.3)
                tip = DataRoot.IsRelease(g.Root) ? $"{p.Name}\nNot in this version" : $"{p.Name}\n⚠ file missing: {miss}";
                box.Opacity = 0.35;
            }
            var btn = new Button { Style = (Style)FindResource("Bare"), Content = box, ToolTip = tip, IsEnabled = !p.Disabled && !card.Missing.ContainsKey(pi) };
            ToolTipService.SetShowOnDisabled(btn, true);
            btn.MouseEnter += (_, _) => box.BorderBrush = Brush("LineHi");
            btn.MouseLeave += (_, _) => box.BorderBrush = box.Tag as Brush ?? Brushes.Transparent;
            btn.Click += (_, _) => StageEdit(card, index);
            row.Children.Add(btn);
            card.Presets.Add((box, label));
        }
        row.Children.Add(custom);
        body.Children.Add(row);
        body.Children.Add(warn);
        return card;
    }

    /// <summary>A grey card for something that is not ready (edition file "comingSoon"). Not a mod group: it is not in
    /// _cards, so it has no status, no search hit, no edit and never reaches Apply.</summary>
    Border MakeComingSoonCard(ComingSoon cs)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = cs.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock { Text = cs.Text, Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap });
        return new Border
        {
            Style = (Style)FindResource("CardBox"), Width = 380, Opacity = 0.45, Child = body,
            IsHitTestVisible = false, Focusable = false, IsEnabled = false,   // no hover, no click, no keyboard focus
        };
    }

    /// <summary>The amber "Edited" tag every card shows while it holds an edit that is not applied yet.</summary>
    Border EditedPill() => new()
    {
        Style = (Style)FindResource("Pill"), Background = Brush("WarnBg"), Visibility = Visibility.Collapsed,
        Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4), VerticalAlignment = VerticalAlignment.Center,
        ToolTip = "Edited, not applied yet: press Apply changes (or undo it on the card)",
        Child = new TextBlock { Text = "Edited", Foreground = Brush("Warn"), FontSize = 12 },
    };

    static (string Icon, string Tone, string Tip) ReloadBadge(string reload) => reload switch
    {
        "hot" => ("IcBolt", "Ok", "Applies at once"),
        "checkpoint" => ("IcRestart", "Acc", "Restart from checkpoint"),
        "full" => ("IcRestart", "Bad", "Full game restart"),
        _ => ("IcMenu", "Warn", "Main menu → Continue"),
    };

    Path IconPath(string key, string tone, double scale) => new()
    {
        Style = (Style)FindResource("Icon"), Data = (Geometry)FindResource(key), Stroke = Brush(tone),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        LayoutTransform = new ScaleTransform(scale, scale),
    };

    FrameworkElement Avatar(string name)
    {
        var g = new Grid { Width = 48, Height = 48, HorizontalAlignment = HorizontalAlignment.Center };
        g.Children.Add(new Ellipse { Fill = Brush("IcoBg") });
        g.Children.Add(new TextBlock
        {
            Text = name[..1], FontSize = 20, FontWeight = FontWeights.Bold, Foreground = Brush("Acc"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    FrameworkElement Spinner()
    {
        var arc = new Path
        {
            Data = Geometry.Parse("M12 3a9 9 0 0 1 9 9"), Stroke = Brush("Acc"), StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 24, Height = 24, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(), Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed, ToolTip = "Checking the files…",
        };
        arc.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return arc;
    }

    // ---------- status ----------

    async Task RefreshModsAsync()
    {
        if (_catalog == null) return;
        if (!GameLocator.IsGameDir(_settings.GamePath)) { ActiveEmpty.Content = "Set the game folder first"; _ = RefreshSetupAsync(); return; }
        if (_modChecking) { _modCheckAgain = true; return; }
        _modChecking = true;
        try
        {
            do
            {
                _modCheckAgain = false;
                foreach (var c in _cards.Values) c.Spinner.Visibility = Visibility.Visible;
                await _io.WaitAsync(); // never check while a preset is being written
                try
                {
                    // Each group is checked on the thread pool; each card updates as soon as its own answer is in.
                    await Task.WhenAll(_catalog.Groups.ToList().Select(async g =>
                    {
                        var m = Matcher(g.Root);
                        int idx = await Task.Run(() => m.Check(g));
                        if (!_cards.TryGetValue(g.Id, out var card)) return;   // its pack was removed meanwhile
                        _active[g.Id] = idx;
                        ShowActive(card, idx);
                    }));
                }
                finally { _io.Release(); }
                _lastModCheck = DateTime.UtcNow;
                UpdateActiveChips();
                if (_loList != null && !LoDirty) LoadLoadoutFromDisk(); // never throw away unsaved picks
                if (_extra.Count == 0) LoadTweaksFromDisk(); else TweakChanged(); // the probe note follows the probe card
                if (_slotTarget.Count == 0) LoadSlotsFromDisk();
                _ = RefreshSetupAsync();
            } while (_modCheckAgain);
        }
        catch (Exception ex)
        {
            // Fire-and-forget callers never see an exception: show it instead of a card stuck on "checking".
            ActiveEmpty.Content = "Could not check the mods: " + ex.Message;
            ActiveEmpty.Visibility = Visibility.Visible;
            foreach (var c in _cards.Values) c.Spinner.Visibility = Visibility.Collapsed;
        }
        finally { _modChecking = false; ReleaseMemory(); }
    }

    /// <summary>
    /// A check unpacks several big wads at once (hundreds of MB of short-lived arrays). One compacting collection
    /// right after it lets the segments GC (csproj System.GC.Name) give that memory back to Windows (8c release test).
    /// </summary>
    internal static void ReleaseMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);    }

    void ShowActive(Card c, int idx)
    {
        c.Spinner.Visibility = Visibility.Collapsed;
        // an edit that is already on the disk (applied elsewhere, or by the old app) is no longer an edit
        if (_pending.TryGetValue(c.Group.Id, out var p) && p == idx) _pending.Remove(c.Group.Id);
        RenderCard(c);
        UpdatePendingUi();
        if (_reviewOpen && Confirm.IsVisible) // keep an open review list true to what Apply will write
        {
            if (PendingCount == 0) CloseReview(); else FillReview();
        }
    }

    void UpdateActiveChips()
    {
        if (_catalog == null) return;
        ActiveChips.Children.Clear();
        foreach (var g in _catalog.Groups)
        {
            if (!_active.TryGetValue(g.Id, out var idx) || idx == g.Vanilla) continue;
            var what = idx < 0 ? "Custom" : g.Presets[idx].Label;
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(IconPath(g.Icon, idx < 0 ? "Warn" : "Acc", 0.7));
            sp.Children.Add(new TextBlock { Text = $"{g.Name} · {what}", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var chip = new Button { Style = (Style)FindResource("Chip"), Content = sp, ToolTip = "Show this mod" };
            chip.Click += (_, _) => GoToCard(g);
            ActiveChips.Children.Add(chip);
        }
        int n = ActiveChips.Children.Count;
        ActiveHeader.Text = n == 0 ? "Active mods" : $"Active mods · {n}";
        ActiveEmpty.Content = "No mods active: the game is vanilla";
        ActiveEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void GoToCard(ModGroup g)
    {
        var tab = g.Tab switch { "companions" => TabCompanions, "valhalla" => TabValhalla, "tweaks" => TabTweaks, _ => TabBoss };
        SearchText.Text = "";
        tab.IsChecked = true;
        var root = _cards[g.Id].Root;
        root.BringIntoView();
        root.BorderBrush = Brush("Acc"); // a short highlight, then back to the style's border
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        t.Tick += (_, _) => { t.Stop(); root.ClearValue(Border.BorderBrushProperty); };
        t.Start();
    }

    // ---------- search ----------

    void Search_Changed(object sender, TextChangedEventArgs e)
    {
        var q = SearchText.Text.Trim();
        foreach (var c in _cards.Values)
        {
            bool hit = q.Length == 0
                       || c.Group.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                       || (c.Group.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                       || c.Group.Presets.Any(p => p.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
            c.Root.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshModsAsync();
}
