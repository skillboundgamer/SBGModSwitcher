using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ModSwitcher2;

/// <summary>
/// The first-start guide of pack mode (batch B, 2026-10-27): game folder -> mod loader (GoWR MIO) -> disclaimer ->
/// install your first mod. Each step shows a green check or a fix button. Shown once (settings GuideDone), again from
/// Settings > Guide. Speed: one step is ~15 small elements built on a step change; the checks are File.Exists only,
/// the Steam search runs off the UI thread.
/// </summary>
public partial class MainWindow
{
    public const string MioUrl = "https://www.nexusmods.com/godofwarragnarok/mods/202";
    static readonly string[] GuideNames = { "Game folder", "Mod loader", "Before you start", "First mod" };

    int _guideStep;
    readonly List<(Border Dot, Border Line)> _guideDots = new();
    Border? _guideBody;
    StackPanel? _guideFoot;
    bool _guideAgree;

    bool GuideFolderOk => GameLocator.IsGameDir(_settings.GamePath);
    bool GuideMioOk => GuideFolderOk && GameLocator.HasModLoader(_settings.GamePath!);
    bool GuideStepOk(int i) => i switch
    {
        0 => GuideFolderOk,
        1 => GuideMioOk,
        2 => _settings.DisclaimerAccepted >= DisclaimerVersion,
        _ => _packs.Count > 0,
    };

    void ShowGuide_Click(object sender, RoutedEventArgs e) => ShowGuide();

    void ShowGuide(int step = 0)
    {
        _guideAgree = _settings.DisclaimerAccepted >= DisclaimerVersion;
        GuideOverlay.Children.Clear();
        _guideDots.Clear();
        var box = new Border
        {
            Width = 580, Background = Brush("Panel"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20), Padding = new Thickness(30, 26, 30, 24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.96, 0.96),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 50, ShadowDepth = 0, Opacity = 0.6 },
        };
        var sp = new StackPanel();
        box.Child = sp;
        sp.Children.Add(new TextBlock { Text = "WELCOME", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brush("Acc"), HorizontalAlignment = HorizontalAlignment.Center });
        sp.Children.Add(new TextBlock { Text = "Set up in four steps", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 20) });
        sp.Children.Add(GuideStepper());
        _guideBody = new Border { MinHeight = 210, Margin = new Thickness(0, 22, 0, 0), RenderTransform = new TranslateTransform() };
        sp.Children.Add(_guideBody);
        _guideFoot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        sp.Children.Add(_guideFoot);
        GuideOverlay.Children.Add(box);
        GuideOverlay.Visibility = Visibility.Visible;
        MainArea.IsEnabled = Rail.IsEnabled = false;   // keeps Tab focus inside the guide

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        GuideOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        box.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        box.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        GuideGo(step, 0);
    }

    /// <summary>Four dots joined by lines; done steps turn green with a check, the current one glows blue.</summary>
    FrameworkElement GuideStepper()
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        for (int i = 0; i < GuideNames.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var line = new Border { Width = 56, Height = 2, CornerRadius = new CornerRadius(1), Background = Brush("Line"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 15, 0, 0), Visibility = i == 0 ? Visibility.Collapsed : Visibility.Visible };
            var dot = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(2), BorderBrush = Brush("Line"),
                Background = Brush("Well"), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
            var col = new StackPanel { Width = 92 };
            col.Children.Add(dot);
            col.Children.Add(new TextBlock { Text = GuideNames[i], FontSize = 11, Foreground = Brush("Dim"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
            var cell = new StackPanel { Orientation = Orientation.Horizontal };
            if (i > 0) cell.Children.Add(line);
            cell.Children.Add(col);
            int at = i;
            dot.Cursor = Cursors.Hand;
            dot.MouseLeftButtonUp += (_, _) => { if (at <= 2 || GuideStepOk(2)) GuideGo(at, at > _guideStep ? 1 : -1); };
            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
            _guideDots.Add((dot, line));
        }
        return grid;
    }

    void GuideDots()
    {
        for (int i = 0; i < _guideDots.Count; i++)
        {
            var (dot, line) = _guideDots[i];
            bool ok = GuideStepOk(i), now = i == _guideStep;
            dot.BorderBrush = Brush(ok ? "Ok" : now ? "Acc" : "Line");
            dot.Background = Brush(ok ? "OkBg" : now ? "AccBg" : "Well");
            dot.Effect = now ? new System.Windows.Media.Effects.DropShadowEffect { Color = ok ? Color.FromRgb(0x4A, 0xD2, 0x95) : Color.FromRgb(0x5F, 0xB4, 0xFF), BlurRadius = 18, ShadowDepth = 0, Opacity = 0.8 } : null;
            dot.Child = ok ? IconPath("IcCheck", "Ok", 0.7)
                           : new TextBlock { Text = (i + 1).ToString(), FontWeight = FontWeights.SemiBold, Foreground = Brush(now ? "Acc" : "Dim"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            line.Background = Brush(i > 0 && GuideStepOk(i - 1) ? "Ok" : "Line");
            if (now)
                dot.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.5 } });
            if (now)
                dot.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { Amplitude = 0.5 } });
        }
    }

    /// <summary>Show step i; dir = +1 slides in from the right, -1 from the left, 0 no slide (a refresh).</summary>
    void GuideGo(int i, int dir)
    {
        _guideStep = Math.Clamp(i, 0, GuideNames.Length - 1);
        GuideDots();
        _guideBody!.Child = _guideStep switch { 0 => GuideFolder(), 1 => GuideMio(), 2 => GuideDisclaimer(), _ => GuideFirstMod() };
        GuideFooter();
        if (dir == 0) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _guideBody.RenderTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(28 * dir, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        _guideBody.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
    }

    void GuideFooter()
    {
        _guideFoot!.Children.Clear();
        if (GuideStepOk(2))
        {
            var skip = new Button { Style = (Style)FindResource("Bare"), Content = "Skip guide", Foreground = Brush("Dim"), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand };
            skip.Click += (_, _) => CloseGuide();
            _guideFoot.Children.Add(skip);
        }
        if (_guideStep > 0)
        {
            var back = new Button { Style = (Style)FindResource("Btn"), Content = "Back", Margin = new Thickness(0, 0, 8, 0) };
            back.Click += (_, _) => GuideGo(_guideStep - 1, -1);
            _guideFoot.Children.Add(back);
        }
        bool last = _guideStep == GuideNames.Length - 1;
        var next = new Button { Style = (Style)FindResource("BtnPri"), Content = last ? "Finish" : "Next", Padding = new Thickness(22, 9, 22, 9) };
        if (_guideStep == 2) next.IsEnabled = _guideAgree;
        next.Click += (_, _) =>
        {
            if (_guideStep == 2 && !GuideStepOk(2)) { _settings.DisclaimerAccepted = DisclaimerVersion; TrySave(); }
            if (last) CloseGuide(); else GuideGo(_guideStep + 1, 1);
        };
        _guideFoot.Children.Add(next);
    }

    void CloseGuide()
    {
        _settings.GuideDone = true;
        TrySave();
        MainArea.IsEnabled = Rail.IsEnabled = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => { GuideOverlay.Visibility = Visibility.Collapsed; GuideOverlay.Children.Clear(); GuideOverlay.BeginAnimation(OpacityProperty, null); };
        GuideOverlay.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- the four steps ----------

    /// <summary>A step's head: big icon, title, one line; then a status row (green check or the problem) and its buttons.</summary>
    StackPanel GuideHead(string icon, string title, string sub)
    {
        var sp = new StackPanel();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Background = Brush("AccBg"), Child = IconPath(icon, "Acc", 1.2) });
        var t = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold });
        t.Children.Add(new TextBlock { Text = sub, Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        row.Children.Add(t);
        sp.Children.Add(row);
        return sp;
    }

    FrameworkElement GuideStatus(bool ok, string okText, string badText, string badTone = "Warn")
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 18, 0, 0),
            Background = Brush(ok ? "OkBg" : badTone == "Bad" ? "BadBg" : "WarnBg"),
        };
        var row = new DockPanel();
        var mark = IconPath(ok ? "IcCheck" : "IcWarn", ok ? "Ok" : badTone, 0.85);
        mark.RenderTransformOrigin = new Point(0.5, 0.5);
        mark.RenderTransform = new ScaleTransform(1, 1);
        row.Children.Add(mark);
        row.Children.Add(new TextBlock { Text = ok ? okText : badText, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(ok ? "Txt" : badTone) });
        b.Child = row;
        if (ok)   // the check pops in
        {
            var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = new BackEase { Amplitude = 0.7 } };
            mark.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            mark.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        return b;
    }

    StackPanel GuideButtons(params (string Text, bool Primary, Action Click)[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        foreach (var (text, pri, click) in buttons)
        {
            var b = new Button { Style = (Style)FindResource(pri ? "BtnPri" : "Btn"), Content = text, Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => click();
            sp.Children.Add(b);
        }
        return sp;
    }

    FrameworkElement GuideFolder()
    {
        var sp = GuideHead("IcFolder", "Find your game", "The folder with GoWR.exe. Most Steam installs are found by themselves.");
        bool ok = GuideFolderOk;
        sp.Children.Add(GuideStatus(ok, _settings.GamePath ?? "", "Not found yet"));
        sp.Children.Add(ok ? GuideButtons(("Change folder", false, GuideBrowse))
                           : GuideButtons(("Find it for me", true, () => _ = GuideDetectAsync()), ("Browse…", false, GuideBrowse)));
        return sp;
    }

    void GuideBrowse()
    {
        Browse_Click(this, new RoutedEventArgs());
        GuideGo(_guideStep, 0);
    }

    async Task GuideDetectAsync()
    {
        var found = await Task.Run(GameLocator.Detect);
        if (found != null) { _settings.GamePath = found; TrySave(); RefreshStatus(); }
        GuideGo(_guideStep, 0);
        if (found == null) ShowBanner("Could not find the game in your Steam libraries. Use Browse.", "Warn", null, null);
    }

    FrameworkElement GuideMio()
    {
        var sp = GuideHead("IcPlug", "Install the mod loader", "GoWR MIO loads the mods into the game. It is a separate download on Nexus.");
        if (!GuideFolderOk) { sp.Children.Add(GuideStatus(false, "", "Set the game folder first (step 1)", "Warn")); return sp; }
        bool ok = GuideMioOk;
        sp.Children.Add(GuideStatus(ok, "GoWR MIO is installed", "GoWR MIO is not in your game folder"));
        if (!ok)
            sp.Children.Add(GuideButtons(("Get GoWR MIO", true, () =>
            {
                try { Process.Start(new ProcessStartInfo(MioUrl) { UseShellExecute = true })?.Dispose(); }
                catch { /* no browser: nothing to do */ }
            }), ("Check again", false, () => { RefreshStatus(); GuideGo(_guideStep, 0); })));
        return sp;
    }

    FrameworkElement GuideDisclaimer()
    {
        var sp = GuideHead("IcShield", "Before you start", "Please read these four lines.");
        var list = new StackPanel { Margin = new Thickness(66, 12, 0, 0) };
        foreach (var (ic, text) in new[] { ("IcInfo", "Unofficial fan tool. Use at your own risk."), ("IcShield", "Back up your saves first."),
                                           ("IcFile", "Changes only your own game files; ships none."), ("IcRestart", "Turn mods off before reporting game bugs.") })
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            r.Children.Add(IconPath(ic, "Dim", 0.8));
            r.Children.Add(new TextBlock { Text = text, Foreground = Brush("Dim"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            list.Children.Add(r);
        }
        sp.Children.Add(list);
        var agree = new CheckBox { Content = "I understand", IsChecked = _guideAgree, Margin = new Thickness(66, 12, 0, 0) };
        agree.Click += (_, _) => { _guideAgree = agree.IsChecked == true; GuideFooter(); };
        sp.Children.Add(agree);
        return sp;
    }

    FrameworkElement GuideFirstMod()
    {
        bool ok = GuideStepOk(3);
        var sp = GuideHead("IcPlus", ok ? "You have mods" : "Install your first mod", ok ? "Add more any time: drop a mod zip on this window." : "Drop a mod zip from Nexus here. It is checked before anything is written.");
        if (ok) { sp.Children.Add(GuideStatus(true, string.Join(", ", _packs.Select(p => p.Info.Title)), "")); return sp; }
        var zone = new Border
        {
            Height = 96, CornerRadius = new CornerRadius(14), Background = Brush("Well"), Cursor = Cursors.Hand, Margin = new Thickness(0, 18, 0, 0),
            ToolTip = "Install a mod pack (.zip from Nexus)",
        };
        var grid = new Grid();
        var dash = new Rectangle { RadiusX = 14, RadiusY = 14, Stroke = Brush("Acc"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, Opacity = 0.7 };
        dash.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(0, -14, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        grid.Children.Add(dash);
        var inner = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        inner.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Background = Brush("AccBg"), Child = IconPath("IcPlus", "Acc", 1) });
        var t = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = "Drop a mod zip", FontWeight = FontWeights.SemiBold, Foreground = Brush("Acc") });
        t.Children.Add(new TextBlock { Text = "or click to choose one", FontSize = 12, Foreground = Brush("Dim") });
        inner.Children.Add(t);
        grid.Children.Add(inner);
        zone.Child = grid;
        zone.MouseEnter += (_, _) => zone.Background = Brush("Card");
        zone.MouseLeave += (_, _) => zone.Background = Brush("Well");
        zone.MouseLeftButtonUp += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Mod pack (*.zip)|*.zip", Title = "Install a mod pack" };
            if (dlg.ShowDialog(this) == true) { CloseGuide(); _ = InstallPackAsync(dlg.FileName); }
        };
        sp.Children.Add(zone);
        return sp;
    }

    /// <summary>A zip dropped on the window while the guide is open: before the disclaimer is accepted the guide goes to
    /// that step and the drop is refused; after it, the guide closes and the install runs as usual.</summary>
    bool GuideAllowsDrop()
    {
        if (GuideOverlay.Visibility != Visibility.Visible) return true;
        if (!GuideStepOk(2)) { GuideGo(2, 2 > _guideStep ? 1 : 0); return false; }
        CloseGuide();
        return true;
    }

    /// <summary>The checks changed outside the guide (the Steam search at start): redraw the step shown.</summary>
    void GuideRefresh()
    {
        if (GuideOverlay.Visibility == Visibility.Visible && _guideBody != null) GuideGo(_guideStep, 0);
    }
}
