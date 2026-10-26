using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ModSwitcher2;

/// <summary>
/// Edit first, apply on purpose: a click on a preset only marks an edit. The Apply bar (every tab)
/// opens a review list of every edit, each with a delete button; only "Apply" in that list writes game files.
/// The Kratos loadout joins the same list as one row while it has unsaved picks.
/// </summary>
public partial class MainWindow
{
    readonly Dictionary<string, int> _pending = new(); // group id -> preset index the player picked, not applied yet

    /// <summary>An edit that is not a preset (the Game Tweaks): its row text, how to write it, how to undo it on screen.</summary>
    sealed record ExtraEdit(string Tab, string Icon, string Name, string Change, string Reload, Func<Task<bool>> Write, Action Revert);
    readonly Dictionary<string, ExtraEdit> _extra = new();
    bool _reviewOpen;

    int PendingCount => _pending.Count + _extra.Count + (LoadoutEdited ? 1 : 0);
    bool LoadoutEdited => _loList != null && (LoDirty || LoCodeStale); // an old plug-in copy in the game is one row too

    // ---------- edits ----------

    void StageEdit(Card c, int index)
    {
        var g = c.Group;
        if (g.Presets[index].Disabled) return;
        bool isOnDisk = _active.TryGetValue(g.Id, out var cur) && cur == index;
        // a King's Grave preset and the Lab figures write the same script: the newer edit wins
        if (g.Id == "kings" && _extra.TryGetValue("figures", out var fig)) fig.Revert();
        if (isOnDisk || (_pending.TryGetValue(g.Id, out var p) && p == index)) _pending.Remove(g.Id); // back to what the disk has
        else _pending[g.Id] = index;
        RenderCard(c);
        UpdatePendingUi();
    }

    /// <summary>On disk = solid blue. Edited choice = outlined, with the card's "Edited" tag.</summary>
    void RenderCard(Card c)
    {
        int idx = _active.TryGetValue(c.Group.Id, out var a) ? a : int.MinValue;
        bool hasEdit = _pending.TryGetValue(c.Group.Id, out var pend);
        for (int i = 0; i < c.Presets.Count; i++)
        {
            var (box, text) = c.Presets[i];
            bool on = i == idx, picked = hasEdit && i == pend;
            box.Background = on ? Brush("Acc") : picked ? Brush("Acc2") : Brush("Well");
            box.Opacity = c.Group.Presets[i].Disabled || c.Missing.ContainsKey(i) ? 0.35 : on && hasEdit ? 0.55 : 1; // the old choice fades while an edit waits
            box.Tag = picked ? Brush("Warn") : null;
            box.BorderBrush = (Brush?)box.Tag ?? Brushes.Transparent;
            box.BorderThickness = new Thickness(picked ? 2 : 1);
            text.Foreground = on ? new SolidColorBrush(Color.FromRgb(0x05, 0x12, 0x1E)) : picked ? Brushes.White : Brush("Dim");
            text.FontWeight = on || picked ? FontWeights.SemiBold : FontWeights.Normal;
        }
        c.CustomPill.Visibility = idx == -1 ? Visibility.Visible : Visibility.Collapsed;
        c.EditedTag.Visibility = hasEdit ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdatePendingUi()
    {
        int n = PendingCount;
        ApplyBar.Visibility = n > 0 && !_reviewOpen ? Visibility.Visible : Visibility.Collapsed;
        ApplyBarText.Text = n == 1 ? "1 edit not applied" : $"{n} edits not applied";
        bool Tab(string t) => _catalog?.Groups.Any(g => g.Tab == t && _pending.ContainsKey(g.Id)) == true;
        DotBoss.Visibility = Show(Tab("boss"));
        DotCompanions.Visibility = Show(Tab("companions"));
        DotValhalla.Visibility = Show(Tab("valhalla") || LoadoutEdited || _extra.Values.Any(x => x.Tab == "valhalla"));
        DotTweaks.Visibility = Show(Tab("tweaks") || _extra.Values.Any(x => x.Tab == "tweaks"));
        ShowRuleWarnings();
    }

    void DiscardAll_Click(object sender, RoutedEventArgs e)
    {
        _pending.Clear();
        foreach (var c in _cards.Values) RenderCard(c);
        if (LoadoutEdited) LoDropEdits();
        foreach (var x in _extra.Values.ToList()) x.Revert();
        UpdatePendingUi();
        ShowToast("Edits discarded: nothing was changed", "Acc");
    }

    // ---------- Restore all: fills the list, the player reviews it ----------

    async void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog == null || _restoring) return;
        _restoring = true;
        try { await RestoreAllAsync(); } finally { _restoring = false; }
    }
    bool _restoring;   // a double click while the ini check runs counts once

    async Task RestoreAllAsync()
    {
        int n = 0;
        foreach (var g in _catalog.Groups)
            if (_active.TryGetValue(g.Id, out var i) && i != g.Vanilla) { _pending[g.Id] = g.Vanilla; n++; }
        // memory slots back to their original size too (the old app's Restore EVERYTHING did this)
        foreach (var s in Lab.Slots)
            if (_slotSize.TryGetValue(s.Wad, out var sz) && sz.Now != sz.Orig) { _slotTarget[s.Wad] = sz.Orig; SlotEdited(s); n++; }
        if (_extra.TryGetValue("figures", out var figEdit)) figEdit.Revert(); // King's Grave goes to Vanilla instead
        if (_loList != null && StageLoaderRemove(quiet: true)) n++;          // the loadout loader files this app put in (only while Off)
        if (await StageIniRestoreAsync()) n++;                                         // the player's first GOWR_MIO.ini (Setup card Fix it)
        foreach (var c in _cards.Values) RenderCard(c);
        UpdatePendingUi();
        if (n == 0 && PendingCount == 0) { ShowToast("Nothing to restore: the game is vanilla", "Acc"); return; }
        OpenReview();
    }

    // ---------- review list ----------

    void ReviewApply_Click(object sender, RoutedEventArgs e) => OpenReview();

    void OpenReview()
    {
        if (PendingCount == 0) return;
        _reviewOpen = true;
        FillReview();
        Confirm.Visibility = Visibility.Visible;
        MainArea.IsEnabled = Rail.IsEnabled = false;
        UpdatePendingUi();
        ConfirmNo.Focus();
    }

    void CloseReview()
    {
        _reviewOpen = false;
        Confirm.Visibility = Visibility.Collapsed;
        MainArea.IsEnabled = Rail.IsEnabled = true;
        UpdatePendingUi();
    }

    void FillReview()
    {
        ConfirmRows.Children.Clear();
        bool full = false;
        if (_catalog != null)
            foreach (var g in _catalog.Groups)
            {
                if (!_pending.TryGetValue(g.Id, out var to)) continue;
                int from = _active.TryGetValue(g.Id, out var a) ? a : -1;
                full |= g.Reload == "full";
                var gid = g.Id;
                ConfirmRows.Children.Add(ReviewRow(g.Icon, g.Name,
                    $"{(from < 0 ? "Custom" : g.Presets[from].Label)}  →  {g.Presets[to].Label}", g.Reload,
                    () => { _pending.Remove(gid); RenderCard(_cards[gid]); }, RowMarks(g.Id, g.Presets[to], g.Reload, g.Root)));
            }
        foreach (var (key, x) in _extra.ToList())
        {
            full |= x.Reload == "full";
            ConfirmRows.Children.Add(ReviewRow(x.Icon, x.Name, x.Change, x.Reload, x.Revert, RowMarks(key, null, x.Reload)));
        }
        if (LoadoutEdited)
            ConfirmRows.Children.Add(ReviewRow("IcShield", "Kratos loadout",
                $"{(_loSaved.On ? "On" : "Off")} → {(_loOnNow ? "On" : "Off")} · glyphs {_loSaved.Glyphs.Count} → {_loGlyphs.Count} · "
                + $"stat {_loSaved.Stats.Count} → {_loStats.Count} · slots {_loSaved.Slots.Count} → {_loSlots.Count}"
                + (_loSaved.Burdens.Count > 0 || _loBurdens.Count > 0 ? $" · burden {_loSaved.Burdens.Count} → {_loBurdens.Count}" : "")
                + (LoBreachingOn != _loSaved.Breaching ? $" · breaching {(_loSaved.Breaching ? "on" : "off")} → {(LoBreachingOn ? "on" : "off")}" : "")
                + (LoCodeStale ? $" · plug-in v{_loCode!.Game} → v{_loCode.App}" : ""), "hot",
                () => LoDropEdits()));

        int n = PendingCount;
        ConfirmTitle.Text = n == 1 ? "Apply 1 change?" : $"Apply {n} changes?";
        ConfirmYes.Content = n == 1 ? "Apply 1" : $"Apply {n}";
        ConfirmNote.Text = (full ? "Some changes need a full game restart. " : "")
                           + "Trash = drop that edit (its card goes back). Nothing is written before Apply.";
    }

    FrameworkElement ReviewRow(string icon, string name, string change, string reload, Action drop, IList<string>? marks = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var del = new Button { Style = (Style)FindResource("Btn"), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(10, 0, 0, 0),
                               ToolTip = "Remove this edit (nothing is written)", Content = IconPath("IcTrash", "Bad", 0.75) };
        del.Click += (_, _) =>
        {
            drop();
            UpdatePendingUi();
            if (PendingCount == 0) { CloseReview(); ShowToast("No edits left: nothing was changed", "Acc"); }
            else FillReview();
        };
        DockPanel.SetDock(del, Dock.Right);
        row.Children.Add(del);
        var (bIcon, bTone, bTip) = ReloadBadge(reload);
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Brush(bTone + "Bg"),
                                 Child = IconPath(bIcon, bTone, 0.65), ToolTip = bTip, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);
        row.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Width = 34, Height = 34, Child = IconPath(icon, "Acc", 0.8) });
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = change, Foreground = Brush("Dim"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        foreach (var m in marks ?? Array.Empty<string>())
            text.Children.Add(new TextBlock { Text = "⚠ " + m, Foreground = Brush("Warn"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        row.Children.Add(text);
        return new Border { Background = Brush("Card"), CornerRadius = new CornerRadius(12), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), Child = row };
    }

    void ConfirmNo_Click(object sender, RoutedEventArgs e) => CloseReview();

    async void ConfirmYes_Click(object sender, RoutedEventArgs e)
    {
        // Apply exactly what the list showed, with the window locked until it is done: an edit made while
        // writing could otherwise be written without ever being reviewed.
        var reviewed = new Dictionary<string, int>(_pending);
        var extras = _extra.Values.ToList();
        bool loadout = LoadoutEdited;
        Confirm.Visibility = Visibility.Collapsed;
        try { await ApplyPendingAsync(reviewed, extras, loadout); }
        finally { CloseReview(); }
    }

    // ---------- apply ----------

    async Task ApplyPendingAsync(Dictionary<string, int> reviewed, List<ExtraEdit> extras, bool loadout)
    {
        if (_catalog == null || !GameLocator.IsGameDir(_settings.GamePath))
        {
            ShowToast("Nothing applied: set the game folder first (Settings)", "Bad");
            return;
        }
        int ok = 0, failed = 0; bool full = false;
        foreach (var g in _catalog.Groups.ToList())
        {
            if (!reviewed.TryGetValue(g.Id, out var to)) continue;
            var r = await ApplyAsync(g, g.Presets[to]);
            if (r == null) failed++; // the edit stays in the list, so the player can retry
            else { ok++; full |= r.Value; _pending.Remove(g.Id); RenderCard(_cards[g.Id]); }
        }
        foreach (var x in extras)
        {
            if (await x.Write()) { ok++; full |= x.Reload == "full"; } else failed++; // a failed one stays edited
        }
        if (loadout && LoadoutEdited)
        {
            if (await WriteLoadoutAsync()) ok++; else failed++;
        }
        UpdatePendingUi();
        if (extras.Count > 0) _ = RefreshModsAsync(); // the figures / slots change what the preset cards match
        if (OldSwitcherOpen()) Log("note: the old Mod Switcher is open; its window now shows an old state", "Warn");
        ShowToast(failed > 0 ? $"Applied {ok}, {failed} failed (still listed; see Activity)"
                             : $"Applied {ok} change{(ok == 1 ? "" : "s")}" + (full ? ": restart the game" : ""),
                  failed > 0 ? "Bad" : "Ok");
    }
}
