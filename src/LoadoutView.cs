using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ModSwitcher2;

/// <summary>The "Kratos loadout" card on the Valhalla tab: glyphs, stat glyphs, runic attacks, relic.</summary>
public partial class MainWindow
{
    List<LoadoutItem> _loItems = new();
    LoadoutState _loSaved = new();
    bool _loOnNow;
    readonly List<string> _loGlyphs = new(), _loStats = new();
    readonly Dictionary<string, string> _loSlots = new();
    string _loKind = "";            // "" = all kinds
    bool _loFilling;                // true while the controls are filled from the file (no "changed" events)

    ItemsControl _loList = null!;   // virtualized: only the rows in view are laid out (LoNewList, LoadoutRows.cs)
    WrapPanel _loChosen = null!, _loKinds = null!;
    TextBlock _loCount = null!, _loHint = null!;
    StackPanel _loVer = null!;      // the plug-in version line under the counters
    LoadoutCode? _loCode;
    string? _loCodeSkip, _loCodeErr; // the stale state the player dropped from the Apply list; the last read error logged
    bool LoCodeDiffers => _loCode is { AppExists: true, Game: not null, Same: false };
    /// <summary>The game has another copy of the plug-in than the app (e.g. the old Switcher put v0.3a back): Apply fixes it.</summary>
    bool LoCodeStale => LoCodeDiffers && LoCodeKey != _loCodeSkip;
    string LoCodeKey => $"{_loCode?.Game}|{_loCode?.App}";

    /// <summary>Trash / Discard: drop the unsaved picks, and a plug-in update too (until the app restarts or the game copy changes).</summary>
    void LoDropEdits()
    {
        if (LoCodeStale) _loCodeSkip = LoCodeKey;
        LoadLoadoutFromDisk();
    }
    TextBox _loSearch = null!;
    CheckBox _loSwitch = null!;
    Border _loEdited = null!;
    readonly Dictionary<string, ComboBox> _loCombos = new();

    void BuildLoadoutCard()
    {
        if (_dataRoot == null) return;
        _loItems = CurrentItems();   // may be empty on a first start: the list is built in the background (8b)

        var root = new Border { Style = (Style)FindResource("CardBox"), Width = 1036 };
        var body = new StackPanel();
        root.Child = body;

        // header: icon, title, counters | badge, on/off, Apply
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        // no Apply button of its own: unsaved picks are one row of the shared Apply list (Pending.cs)
        _loEdited = EditedPill();
        DockPanel.SetDock(_loEdited, Dock.Right);
        head.Children.Add(_loEdited);
        _loSwitch = new CheckBox { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 16, 0),
                                   ToolTip = "On: this loadout. Off: the game's own loadout (glyphs given here are taken out again)" };
        _loSwitch.Checked += (_, _) => LoChanged();
        _loSwitch.Unchecked += (_, _) => LoChanged();
        DockPanel.SetDock(_loSwitch, Dock.Right);
        head.Children.Add(_loSwitch);
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Background = Brush("OkBg"),
                                 Child = IconPath("IcBolt", "Ok", 0.65), ToolTip = "No restart: picked up in ~2 s", VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        head.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Child = IconPath("IcShield", "Acc", 1),
                                       ToolTip = "Valhalla: Kratos' glyphs, runic attacks and relic" });
        var titles = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Kratos loadout", FontSize = 15, FontWeight = FontWeights.SemiBold });
        _loCount = new TextBlock { Foreground = Brush("Dim"), FontSize = 12 };
        titles.Children.Add(_loCount);
        BuildBreachingBox(titles);   // LoadoutBreaching.cs
        _loVer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), Background = Brushes.Transparent };
        titles.Children.Add(_loVer);
        _loLoader = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), Background = Brushes.Transparent };
        titles.Children.Add(_loLoader);
        head.Children.Add(titles);
        body.Children.Add(head);

        var cols = new Grid();
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(cols);

        // left: search, kind filter, item list
        var left = new StackPanel();
        cols.Children.Add(left);
        var searchGrid = new Grid();
        _loSearch = new TextBox { ToolTip = "Search by name or effect" };
        _loHint = new TextBlock { Text = "Search glyphs…", Foreground = Brush("Dim"), Margin = new Thickness(11, 8, 0, 0), IsHitTestVisible = false };
        _loSearch.TextChanged += (_, _) => { _loHint.Visibility = _loSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; LoFilter(); };
        searchGrid.Children.Add(_loSearch);
        searchGrid.Children.Add(_loHint);
        left.Children.Add(searchGrid);
        _loKinds = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        foreach (var k in new[] { "" }.Concat(Loadout.GlyphKinds))
        {
            var label = new TextBlock { Text = k == "" ? "All" : k };
            var pill = new Border { Style = (Style)FindResource("Pill"), Child = label, Tag = k };
            var b = new Button { Style = (Style)FindResource("Bare"), Content = pill, ToolTip = k == "" ? "Every glyph" : k + " glyphs" };
            b.Click += (_, _) => { _loKind = k; FillLoKinds(); LoFilter(); };
            _loKinds.Children.Add(b);
        }
        left.Children.Add(_loKinds);
        _loList = LoNewList();
        left.Children.Add(new Border
        {
            Background = Brush("Well"), CornerRadius = new CornerRadius(10), Height = 330, Padding = new Thickness(4), Child = _loList,
        });

        // right: chosen glyphs, runic attacks and relic
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        cols.Children.Add(right);
        right.Children.Add(new TextBlock { Text = "Chosen", Style = (Style)FindResource("Section"), Margin = new Thickness(0, 0, 0, 8) });
        _loChosen = new WrapPanel { MinHeight = 40 };
        right.Children.Add(_loChosen);
        right.Children.Add(new TextBlock { Text = "Runic attacks and relic", Style = (Style)FindResource("Section") });
        var slots = new Grid();
        slots.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        slots.ColumnDefinitions.Add(new ColumnDefinition());
        int row = 0;
        foreach (var (slot, label) in Loadout.Slots)
        {
            slots.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = new TextBlock { Text = label, Foreground = Brush("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 6) };
            Grid.SetRow(t, row);
            slots.Children.Add(t);
            var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
            FillSlotCombo(cb, slot);
            cb.SelectionChanged += (_, _) =>
            {
                if (_loFilling) return;
                var v = (string)((ComboBoxItem)cb.SelectedItem).Tag;
                if (v.Length == 0) _loSlots.Remove(slot); else _loSlots[slot] = v;
                LoChanged();
            };
            Grid.SetRow(cb, row); Grid.SetColumn(cb, 1);
            slots.Children.Add(cb);
            _loCombos[slot] = cb;
            row++;
        }
        right.Children.Add(slots);

        BuildBurdenBox(body);   // the burden glyphs under both columns (LoadoutBurdens.cs)

        _loadoutRoot = root;   // packs hide it when the last loadout pack goes (PacksView.cs)
        ViewValhalla.Children.Add(root);
        FillLoKinds();
        LoadLoadoutFromDisk();
    }

    void FillSlotCombo(ComboBox cb, string slot)
    {
        cb.Items.Clear();
        cb.Items.Add(new ComboBoxItem { Content = "None", Tag = "" });
        foreach (var it in _loItems.Where(i => i.Kind == slot))
            cb.Items.Add(new ComboBoxItem { Content = it.Label, Tag = it.Name, ToolTip = it.Desc.Length > 0 ? it.Desc : null });
    }

    /// <summary>A new item list (built on this PC, or another language): refill the picker, keep the choices.</summary>
    void ReloadItems()
    {
        if (_loList == null) return;
        _loItems = CurrentItems();
        _loFilling = true;
        foreach (var (slot, cb) in _loCombos)
        {
            FillSlotCombo(cb, slot);
            _loSlots.TryGetValue(slot, out var v);
            cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (v ?? "")) ?? cb.Items[0];
        }
        _loFilling = false;
        FillBurdenCombos();
        FillLoList();
        FillLoChosen();
    }

    /// <summary>Fill the card from mod\sbg_loadout_cfg.lua (start-up, after Apply, or Refresh when nothing is unsaved).</summary>
    void LoadLoadoutFromDisk()
    {
        if (_loList == null || !GameLocator.IsGameDir(_settings.GamePath)) return;
        try { _loSaved = Loadout.Read(_settings.GamePath!); }
        catch (Exception ex) { Log("loadout file could not be read: " + ex.Message, "Bad"); _loSaved = new LoadoutState(); }
        bool rowsOk = LoRowsBuilt;   // the rows were built from this item list
        if (rowsOk && LoShowsSaved()) { LoCheckCode(); LoUpdateState(); return; }   // the card already shows the file (re-activate, after Apply)
        _loFilling = true;
        _loOnNow = _loSaved.On;
        _loSwitch.IsChecked = _loSaved.On;
        _loBreaching.IsChecked = _loSaved.Breaching;
        _loGlyphs.Clear(); _loGlyphs.AddRange(_loSaved.Glyphs);
        _loStats.Clear(); _loStats.AddRange(_loSaved.Stats);
        for (int i = 0; i < _loBurdenBox.Length; i++) _loBurdenBox[i] = i < _loSaved.Burdens.Count ? _loSaved.Burdens[i] : "";
        _loSlots.Clear();
        foreach (var kv in _loSaved.Slots) _loSlots[kv.Key] = kv.Value;
        foreach (var (slot, cb) in _loCombos)
        {
            _loSlots.TryGetValue(slot, out var v);
            cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (v ?? "")) ?? cb.Items[0];
        }
        _loFilling = false;
        if (rowsOk) { LoBurdenSync(); LoUpdateAllRows(); LoChosenSync(); }   // same item list: only the items / rows / chips that changed
        else { FillBurdenCombos(); FillLoList(); FillLoChosen(); }
        LoCheckCode();
        LoUpdateState();
    }

    /// <summary>The version line: is mod\sbg_loadout.lua the app's copy? Read only; an old copy makes the loadout a pending row.</summary>
    void LoCheckCode()
    {
        if (_dataRoot == null) return;
        string icon, tone, text, tip;
        try { _loCode = Loadout.Code(_settings.GamePath!, _dataRoot); _loCodeErr = null; }
        catch (Exception ex)
        {
            _loCode = null;
            if (ex.Message != _loCodeErr) Log("loadout plug-in could not be read: " + ex.Message, "Bad"); // once, not every refresh
            _loCodeErr = ex.Message;
        }
        var c = _loCode;
        if (c == null) { icon = "IcWarn"; tone = "Bad"; text = "Plug-in: could not read it"; tip = "See Activity for the reason"; }
        else if (!c.AppExists) { icon = "IcWarn"; tone = "Bad"; text = "App copy of the plug-in is missing"; tip = "Missing: " + Loadout.CodeSrc; }
        else if (c.Game == null)
        {
            icon = "IcInfo"; tone = "Dim"; text = "Plug-in not installed";
            tip = $"Pick something and Apply: the app installs plug-in v{c.App}";
        }
        else if (c.Same)
        {
            icon = "IcCheck"; tone = "Ok"; text = $"Plug-in v{c.App} · up to date";
            tip = $"{Loadout.CodeGame} is the same file as this app's copy ({Loadout.CodeSrc})";
        }
        else
        {
            icon = "IcWarn"; tone = "Warn";
            text = c.Game == c.App ? $"Game has another v{c.Game}: Apply updates it" : $"Game has v{c.Game}: Apply updates it";
            tip = $"{Loadout.CodeGame} is not this app's copy (the old Mod Switcher may have put it back).\n"
                  + $"Apply puts plug-in v{c.App} in. No restart."
                  + (LoCodeStale ? "" : "\nDropped from the Apply list: change a pick, or reopen the app, to get it back.");
        }
        _loVer.Children.Clear();
        _loVer.Children.Add(IconPath(icon, tone, 0.5));
        _loVer.Children.Add(new TextBlock { Text = text, Foreground = Brush(tone), FontSize = 12, Margin = new Thickness(5, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _loVer.ToolTip = tip;
        LoCheckLoader();
    }

    bool LoDirty =>
        _loOnNow != _loSaved.On || LoBreachingOn != _loSaved.Breaching
        || !_loGlyphs.SequenceEqual(_loSaved.Glyphs) || !_loStats.SequenceEqual(_loSaved.Stats)
        || !_loBurdens.SequenceEqual(_loSaved.Burdens)
        || _loSlots.Count != _loSaved.Slots.Count || _loSlots.Any(kv => !_loSaved.Slots.TryGetValue(kv.Key, out var v) || v != kv.Value);

    void LoChanged()
    {
        if (_loFilling) return;
        _loOnNow = _loSwitch.IsChecked == true;
        if (_loOnNow) _extra.Remove("loader");   // On needs the loader: a waiting "Remove loader" row is dropped
        LoUpdateState();
    }

    void LoUpdateState()
    {
        _loCount.Text = $"{(_loOnNow ? "On" : "Off")} · glyphs {_loGlyphs.Count}/{LoMaxGlyphs} · stat {_loStats.Count}/{Loadout.MaxStats} · "
                        + $"{_loSlots.Count}/{Loadout.Slots.Length} slots"
                        + (_loBurdens.Count > 0 ? $" · burden {_loBurdens.Count}/{Loadout.MaxBurdens}" : "")
                        + (_loGlyphs.Count > LoMaxGlyphs ? " · too many glyphs: take one out" : "")
                        + (LoDirty ? " · edited" : "");
        _loCount.Foreground = LoDirty || _loGlyphs.Count > LoMaxGlyphs ? Brush("Warn") : Brush("Dim");   // e.g. 15 glyphs + breaching from the file
        _loEdited.Visibility = LoDirty ? Visibility.Visible : Visibility.Collapsed;
        UpdatePendingUi();
    }

    void FillLoKinds()
    {
        foreach (Button b in _loKinds.Children)
        {
            var pill = (Border)b.Content;
            bool on = (string)pill.Tag == _loKind;
            pill.Background = on ? Brush("Acc2") : Brush("Well");
            ((TextBlock)pill.Child).Foreground = on ? Brushes.White : Brush("Dim");
        }
    }

    /// <summary>Build every glyph row (item list or file (re)loaded); the search box and the kind pills only hide rows (LoFilter).</summary>
    void FillLoList()
    {
        var list = new List<UIElement>();
        _loRows.Clear();
        _loRowsItems = _loItems;
        foreach (var it in _loItems)
        {
            if (!Loadout.GlyphKinds.Contains(it.Kind)) continue;
            var row = new DockPanel { Margin = new Thickness(10, 6, 10, 6) };
            var kind = new TextBlock { Text = it.Kind, Foreground = Brush("Dim"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(kind, Dock.Right);
            row.Children.Add(kind);
            var mark = IconPath("IcPlus", "Dim", 0.6);   // mark, label colour and tooltip: LoSetRow (LoadoutRows.cs)
            row.Children.Add(mark);
            var label = new TextBlock { Text = it.Label, Margin = new Thickness(6, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                                        TextTrimming = TextTrimming.CharacterEllipsis };
            row.Children.Add(label);
            var box = new Border { Child = row, CornerRadius = new CornerRadius(8), Background = Brushes.Transparent };
            var b = new Button { Style = (Style)FindResource("Bare"), Content = box };
            b.MouseEnter += (_, _) => box.Background = Brush("Card");
            b.MouseLeave += (_, _) => box.Background = Brushes.Transparent;
            b.Click += (_, _) => ToggleGlyph(it);
            LoAddRow(new LoRow(it, b, mark, label));
            list.Add(b);
        }
        _loEmpty = new TextBlock { Margin = new Thickness(10), TextWrapping = TextWrapping.Wrap };   // shown when no row is visible
        list.Add(_loEmpty);
        _loList.ItemsSource = list;
        LoFilter();
    }

    void FillLoChosen()
    {
        _loChosen.Children.Clear();
        foreach (var name in _loGlyphs.Concat(_loStats)) _loChosen.Children.Add(LoChip(name));
        if (_loChosen.Children.Count == 0)
            _loChosen.Children.Add(new TextBlock { Text = "No glyphs chosen: pick from the list", Foreground = Brush("Dim") });
    }

    /// <summary>One chip of the chosen list (Tag = the glyph name, for LoChosenSync).</summary>
    Button LoChip(string name)
    {
        var it = _loItems.FirstOrDefault(i => i.Name == name);
        var chip = new Button { Style = (Style)FindResource("Chip"), Content = $"{it?.Label ?? name}  ✕", Tag = name,
                                ToolTip = (it?.Desc.Length > 0 ? it.Desc + "\n" : "") + "Click to take it out" };
        chip.Click += (_, _) => { if (it != null) ToggleGlyph(it); else { _loGlyphs.Remove(name); _loStats.Remove(name); LoGlyphChanged(name); } };
        return chip;
    }

    void ToggleGlyph(LoadoutItem it)
    {
        var list = it.Kind == "Stat" ? _loStats : _loGlyphs;
        int max = it.Kind == "Stat" ? Loadout.MaxStats : LoMaxGlyphs;
        bool burdenBoxes = it.Kind != "Stat";   // Stat glyphs are not in the burden boxes: no refill
        if (list.Remove(it.Name)) { LoGlyphChanged(it.Name, burdenBoxes); return; }
        if (_loBurdenBox.Contains(it.Name)) { ShowToast($"{it.Label} is a burden glyph: set that box to None first", "Warn"); return; }
        if (list.Count >= max) { ShowToast($"At most {max} {(it.Kind == "Stat" ? "stat glyphs" : "glyphs")} fit: take one out first", "Warn"); return; }
        list.Add(it.Name);
        LoGlyphChanged(it.Name, burdenBoxes);   // only this row + chips + counter (LoadoutRows.cs)
    }

    /// <summary>Writes the loadout (from the Apply list). False when it failed: the picks stay, so it can be retried.</summary>
    async Task<bool> WriteLoadoutAsync()
    {
        if (_dataRoot == null || !GameLocator.IsGameDir(_settings.GamePath)) return false;
        bool on = _loOnNow;
        var glyphs = _loGlyphs.ToList(); var stats = _loStats.ToList(); var slots = new Dictionary<string, string>(_loSlots);
        var burdens = _loBurdens;
        bool breaching = LoBreachingOn;
        string game = _settings.GamePath!, data = _dataRoot;
        await _io.WaitAsync();
        try
        {
            var log = await Task.Run(() => Loadout.Write(game, data, on, glyphs, stats, slots, burdens, breaching));
            foreach (var l in log) Log("  " + l);
            Log("Kratos loadout: written, the game picks it up in ~2 s (no restart)", "Ok");
            LoadLoadoutFromDisk(); // only on success: a failed write keeps the picks so the player can retry
            return true;
        }
        catch (Exception ex)
        {
            Log("Kratos loadout: FAILED: " + ex.Message, "Bad");
            return false;
        }
        finally { _io.Release(); }
    }
}
