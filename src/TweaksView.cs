using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ModSwitcher2;

/// <summary>Game Tweaks tab (phase 5): camera FOV, boss bar names, on-screen messages. Edits go to the Apply list.</summary>
public partial class MainWindow
{
    // what the files hold now / what the controls show
    (bool On, int Deg) _fovSaved, _fovNow;
    string? _namesSaved;  string _namesNow = "";
    bool? _msgsSaved;     bool _msgsNow;
    bool _twFilling, _twBuilt, _twLoaded; // _twLoaded: the cards hold real values (no edits before the first read)

    CheckBox _fovSwitch = null!, _msgsSwitch = null!;
    Slider _fovSlider = null!;
    TextBlock _fovValue = null!, _namesPreview = null!, _namesNeed = null!, _msgsNeed = null!, _msgsWarn = null!;
    Path _fovCone = null!;
    ComboBox _namesMode = null!;
    TextBox _namesText = null!;
    Border _fovEdited = null!, _namesEdited = null!, _msgsEdited = null!;

    void BuildTweakCards()
    {
        // ----- camera FOV -----
        var (fovCard, fovBody, fovEdited) = TweakCard("IcEye", "Camera FOV", "full", "Field of view (mod\\GOWR_MIO.ini, read at game start)");
        _fovEdited = fovEdited;
        _fovCone = new Path { Stroke = Brush("Acc"), StrokeThickness = 1.6, Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x5F, 0xB4, 0xFF)),
                              Width = 200, Height = 92, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10) };
        fovBody.Children.Add(_fovCone);
        var fovRow = new DockPanel();
        _fovSwitch = new CheckBox { Style = (Style)FindResource("Switch"), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                                    ToolTip = "On: your FOV. Off: the game's own" };
        DockPanel.SetDock(_fovSwitch, Dock.Left);
        fovRow.Children.Add(_fovSwitch);
        _fovValue = new TextBlock { Width = 46, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.Bold, Foreground = Brush("Acc"),
                                    VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_fovValue, Dock.Right);
        fovRow.Children.Add(_fovValue);
        _fovSlider = new Slider { Minimum = Tweaks.FovMin, Maximum = Tweaks.FovMax, SmallChange = 1, LargeChange = 5, IsSnapToTickEnabled = true,
                                  TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        fovRow.Children.Add(_fovSlider);
        fovBody.Children.Add(fovRow);
        _fovSwitch.Checked += (_, _) => TweakChanged();
        _fovSwitch.Unchecked += (_, _) => TweakChanged();
        _fovSlider.ValueChanged += (_, _) => TweakChanged();

        // ----- boss bar names -----
        var (namesCard, namesBody, namesEdited) = TweakCard("IcCrown", "Boss bar names", "hot", "Name over each boss health bar (mod\\live.lua, ~2 s)");
        _namesEdited = namesEdited;
        _namesMode = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (label, tag) in new[] { ("Game default", ""), ("BOSS 1 / 2 / 3", "BOSS #"), ("Custom…", "custom") })
            _namesMode.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        namesBody.Children.Add(_namesMode);
        _namesText = new TextBox { MaxLength = Tweaks.NameMax, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8),
                                   ToolTip = "Your text; # = the boss number (HROLF # -> HROLF 1, HROLF 2)" };
        namesBody.Children.Add(_namesText);
        _namesPreview = new TextBlock { FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(2, 6, 0, 3) };
        namesBody.Children.Add(_namesPreview);
        namesBody.Children.Add(new Border
        {
            Height = 12, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.Black, BorderThickness = new Thickness(1),
            Background = new LinearGradientBrush(Color.FromRgb(0xB5, 0x25, 0x25), Color.FromRgb(0x7A, 0x13, 0x13), 0),
        });
        _namesNeed = new TextBlock { FontSize = 12, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        namesBody.Children.Add(_namesNeed);
        _namesMode.SelectionChanged += (_, _) => TweakChanged();
        _namesText.TextChanged += (_, _) => TweakChanged();

        // ----- on-screen messages -----
        var (msgsCard, msgsBody, msgsEdited) = TweakCard("IcVoice", "On-screen messages", "hot", "The Kratos probe's messages in game (mode changes, start hint)");
        _msgsEdited = msgsEdited;
        var msgsRow = new DockPanel();
        _msgsSwitch = new CheckBox { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_msgsSwitch, Dock.Right);
        msgsRow.Children.Add(_msgsSwitch);
        msgsRow.Children.Add(new TextBlock { Text = "Show messages in game", VerticalAlignment = VerticalAlignment.Center });
        msgsBody.Children.Add(msgsRow);
        _msgsWarn = new TextBlock { Text = "⚠ Shows text in game. Off is recommended (they still go to the log).", Foreground = Brush("Warn"),
                                    FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        msgsBody.Children.Add(_msgsWarn);
        _msgsNeed = new TextBlock { FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        msgsBody.Children.Add(_msgsNeed);
        _msgsSwitch.Checked += (_, _) => TweakChanged();
        _msgsSwitch.Unchecked += (_, _) => TweakChanged();

        ViewTweaks.Children.Add(fovCard);
        ViewTweaks.Children.Add(namesCard);
        ViewTweaks.Children.Add(msgsCard);
        _twBuilt = true;
        LoadTweaksFromDisk();
    }

    (Border Card, StackPanel Body, Border Edited) TweakCard(string icon, string name, string reload, string note)
    {
        var root = new Border { Style = (Style)FindResource("CardBox"), Width = 380 };
        var body = new StackPanel();
        root.Child = body;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var (bIcon, bTone, bTip) = ReloadBadge(reload);
        var badge = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(8), ToolTip = bTip, Background = Brush(bTone + "Bg"),
                                 Child = IconPath(bIcon, bTone, 0.65) };
        DockPanel.SetDock(badge, Dock.Right);
        head.Children.Add(badge);
        var edited = EditedPill();
        DockPanel.SetDock(edited, Dock.Right);
        head.Children.Add(edited);
        head.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Child = IconPath(icon, "Acc", 1), ToolTip = note });
        head.Children.Add(new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 8, 0),
                                          VerticalAlignment = VerticalAlignment.Center, ToolTip = note });
        body.Children.Add(head);
        return (root, body, edited);
    }

    // ---------- read ----------

    void LoadTweaksFromDisk()
    {
        if (!_twBuilt || !GameLocator.IsGameDir(_settings.GamePath)) return;
        var game = _settings.GamePath!;
        try { _fovSaved = Tweaks.FovGet(game); } catch (Exception ex) { Log("FOV could not be read: " + ex.Message, "Bad"); _fovSaved = (false, 90); }
        try { _namesSaved = Tweaks.BarNamesGet(game); } catch (Exception ex) { Log("boss bar names could not be read: " + ex.Message, "Bad"); _namesSaved = null; }
        try { _msgsSaved = Tweaks.MsgsGet(game); } catch (Exception ex) { Log("on-screen messages could not be read: " + ex.Message, "Bad"); _msgsSaved = null; }
        _twFilling = true;
        _fovNow = (_fovSaved.On, Math.Clamp(_fovSaved.Deg, Tweaks.FovMin, Tweaks.FovMax));
        _fovSwitch.IsChecked = _fovNow.On;
        _fovSlider.Value = _fovNow.Deg;
        _namesNow = _namesSaved ?? "";
        _namesMode.SelectedIndex = _namesNow == "" ? 0 : _namesNow == "BOSS #" ? 1 : 2;
        if (_namesMode.SelectedIndex == 2) _namesText.Text = _namesNow; else if (_namesText.Text.Length == 0) _namesText.Text = "HROLF #";
        _msgsNow = _msgsSaved == true;
        _msgsSwitch.IsChecked = _msgsNow;
        _twFilling = false;
        _twLoaded = true;
        TweakChanged();
    }

    // ---------- edits ----------

    void TweakChanged()
    {
        if (_twFilling || !_twBuilt || !_twLoaded || _namesMode.SelectedItem is not ComboBoxItem modeItem) return;
        _fovNow = (_fovSwitch.IsChecked == true, (int)_fovSlider.Value);
        var mode = (string)modeItem.Tag;
        _namesText.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
        _namesNow = mode == "custom" ? _namesText.Text.Trim() : mode;
        _msgsNow = _msgsSwitch.IsChecked == true;

        // FOV: value, cone (narrow = zoomed in, wide = far view)
        _fovSlider.IsEnabled = _fovNow.On;
        _fovValue.Text = _fovNow.On ? $"{_fovNow.Deg}°" : "Game";
        double half = Math.Tan(_fovNow.Deg * Math.PI / 360) * 84;
        half = Math.Min(half, 98);
        _fovCone.Data = Geometry.Parse(FormattableString.Invariant($"M100 90 L{100 - half:0.#} 4 L{100 + half:0.#} 4 Z"));
        _fovCone.Opacity = _fovNow.On ? 1 : 0.35;

        // names: preview of boss 1's bar, requirements
        bool namesOk = _namesSaved != null;
        _namesMode.IsEnabled = _namesText.IsEnabled = namesOk;
        _namesPreview.Text = (_namesNow == "" ? "HRÓLF KRAKI" : _namesNow.Replace("#", "1")).ToUpperInvariant();
        SetNeed(_namesNeed, namesOk ? null : @"mod\live.lua has no BAR_NAMES line (needs live v22+)");

        bool msgsOk = _msgsSaved != null;
        _msgsSwitch.IsEnabled = msgsOk;
        _msgsWarn.Visibility = _msgsNow ? Visibility.Visible : Visibility.Collapsed;
        SetNeed(_msgsNeed, msgsOk ? null : @"mod\live.lua has no SHOW_MSGS line (needs live v25+)");

        // the Apply list
        var game = _settings.GamePath ?? "";
        var fov = _fovNow;
        // compare with the saved value as the slider can show it (a hand-edited 130 shows as 120: not an edit by itself)
        var fovSavedShown = (_fovSaved.On, Math.Clamp(_fovSaved.Deg, Tweaks.FovMin, Tweaks.FovMax));
        SetExtra("fov", _fovEdited, fov != fovSavedShown && (fov.On || _fovSaved.On), "IcEye", "Camera FOV",
                 $"{FovText(_fovSaved)} → {FovText(fov)}", "full",
                 () => WriteTweakAsync("Camera FOV", () => Tweaks.FovSet(game, fov.On, fov.Deg, () => GameLocator.IsGameRunningIn(game)), LoadFov),
                 () => { _twFilling = true; _fovSwitch.IsChecked = _fovSaved.On; _fovSlider.Value = Math.Clamp(_fovSaved.Deg, Tweaks.FovMin, Tweaks.FovMax); _twFilling = false; TweakChanged(); });
        var names = _namesNow;
        SetExtra("names", _namesEdited, namesOk && names != (_namesSaved ?? "").Trim() && names.Length <= Tweaks.NameMax, "IcCrown", "Boss bar names",
                 $"{NamesText(_namesSaved ?? "")} → {NamesText(names)}", "hot",
                 () => WriteTweakAsync("Boss bar names", () => { Tweaks.BarNamesSet(game, names); return false; }, LoadNames),
                 () => { _twFilling = true; var s = _namesSaved ?? ""; _namesMode.SelectedIndex = s == "" ? 0 : s == "BOSS #" ? 1 : 2; if (_namesMode.SelectedIndex == 2) _namesText.Text = s; _twFilling = false; TweakChanged(); });
        var msgs = _msgsNow;
        SetExtra("msgs", _msgsEdited, msgsOk && msgs != _msgsSaved, "IcVoice", "On-screen messages",
                 $"{(_msgsSaved == true ? "On" : "Off")} → {(msgs ? "On" : "Off")}", "hot",
                 () => WriteTweakAsync("On-screen messages", () => { Tweaks.MsgsSet(game, msgs); return false; }, LoadMsgs),
                 () => { _twFilling = true; _msgsSwitch.IsChecked = _msgsSaved == true; _twFilling = false; TweakChanged(); });
        UpdatePendingUi();
    }

    static string FovText((bool On, int Deg) f) => f.On ? $"{f.Deg}°" : "game default";
    static string NamesText(string s) => s == "" ? "game default" : $"\"{s}\"";

    void SetNeed(TextBlock t, string? missing)
    {
        bool probe = _active.TryGetValue("probe", out var p) && p == 1;
        if (missing != null) { t.Text = "⚠ " + missing; t.Foreground = Brush("Bad"); }
        else if (!probe && ProbeOnMissing()) { t.Text = "⚠ Needs the Kratos probe, which is not in this version yet"; t.Foreground = Brush("Warn"); }
        else if (!probe) { t.Text = "⚠ Needs the Kratos probe On (card on this tab)"; t.Foreground = Brush("Warn"); }
        else { t.Text = "Kratos probe is on ✓"; t.Foreground = Brush("Dim"); }
    }

    /// <summary>The probe's On preset cannot be applied here (its source is left out of the release: notes/16 3.3).</summary>
    bool ProbeOnMissing() => _cards.TryGetValue("probe", out var c) && c.Missing.Count > 0;

    void SetExtra(string key, Border edited, bool isEdit, string icon, string name, string change, string reload,
                  Func<Task<bool>> write, Action revert)
    {
        edited.Visibility = isEdit ? Visibility.Visible : Visibility.Collapsed;
        if (isEdit) _extra[key] = new ExtraEdit("tweaks", icon, name, change, reload, write, revert);
        else _extra.Remove(key);
    }

    // ---------- write (from the Apply list) ----------

    /// <summary>One tweak, under the disk lock. write() returns true when a full game restart is needed.</summary>
    async Task<bool> WriteTweakAsync(string name, Func<bool> write, Action reload)
    {
        await _io.WaitAsync();
        try
        {
            bool full = await Task.Run(write);
            Log($"{name}: done" + (full ? ", full game restart needed" : ", live in ~2 s (no reload)"), "Ok");
            if (full) AddRestartPending(name);
            // the write is done and verified; a read hiccup here must not report it as failed
            try { reload(); } catch (Exception ex) { Log($"{name}: written, but could not be read back for the card: {ex.Message}", "Warn"); }
            return true;
        }
        catch (Exception ex)
        {
            Log($"{name}: FAILED: {ex.Message}", "Bad");
            return false;
        }
        finally { _io.Release(); }
    }

    // reload one tweak only: the other tweaks may still hold unapplied edits
    void LoadFov() { _fovSaved = Tweaks.FovGet(_settings.GamePath!); TweakChanged(); }
    void LoadNames() { _namesSaved = Tweaks.BarNamesGet(_settings.GamePath!); TweakChanged(); }
    void LoadMsgs() { _msgsSaved = Tweaks.MsgsGet(_settings.GamePath!); TweakChanged(); }
}
