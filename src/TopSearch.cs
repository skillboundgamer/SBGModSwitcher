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

public partial class MainWindow
{
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
}
