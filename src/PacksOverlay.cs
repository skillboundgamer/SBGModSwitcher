using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace ModSwitcher2;

/// <summary>
/// The pack install / remove overlay (moved out of PacksView.cs 2026-10-26): steps, progress bar, ETA, the end state
/// with its buttons, and the fade / slide animations of pack cards and rows.
/// </summary>
public partial class MainWindow
{
    // ---------- the overlay: steps, progress, ETA, result ----------

    sealed class PackPanel
    {
        public required Border IconBox;
        public required TextBlock Title, Sub, Eta;
        public required StackPanel Steps, Buttons;
        public required ScaleTransform Bar;
        public required Border Track;
        public readonly List<(FrameworkElement Mark, TextBlock Text)> Rows = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public double Shown;   // the bar's target, 0..1
    }

    PackPanel OpenPackPanel(string title, string sub, string[] steps)
    {
        PackOverlay.Children.Clear();
        var box = new Border
        {
            Width = 520, Background = Brush("Panel"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.96, 0.96),
        };
        var sp = new StackPanel();
        box.Child = sp;
        var icon = new Border { Style = (Style)FindResource("IcoBox"), Width = 58, Height = 58, CornerRadius = new CornerRadius(14), Background = Brush("AccBg"), Child = Spinner(), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1) };
        icon.Child.Visibility = Visibility.Visible;
        sp.Children.Add(icon);
        var t = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 4), TextTrimming = TextTrimming.CharacterEllipsis };
        sp.Children.Add(t);
        var s = new TextBlock { Text = sub, Foreground = Brush("Dim"), HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
        sp.Children.Add(s);
        var rows = new StackPanel { Margin = new Thickness(40, 0, 40, 0) };
        sp.Children.Add(rows);
        var bar = new ScaleTransform(0, 1);
        var track = new Border
        {
            Height = 6, CornerRadius = new CornerRadius(3), Background = Brush("Well"), Margin = new Thickness(0, 18, 0, 6), ClipToBounds = true,
            Child = new Border { CornerRadius = new CornerRadius(3), RenderTransform = bar, Background = new LinearGradientBrush(Color.FromRgb(0x2A, 0x5D, 0x8A), Color.FromRgb(0x5F, 0xB4, 0xFF), 0) },
        };
        sp.Children.Add(track);
        var eta = new TextBlock { FontSize = 12, Foreground = Brush("Dim"), HorizontalAlignment = HorizontalAlignment.Right, Text = "Starting…" };
        sp.Children.Add(eta);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0), Visibility = Visibility.Collapsed };
        sp.Children.Add(buttons);
        PackOverlay.Children.Add(box);

        var p = new PackPanel { IconBox = icon, Title = t, Sub = s, Eta = eta, Steps = rows, Buttons = buttons, Bar = bar, Track = track };
        foreach (var name in steps)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Opacity = 0.55 };
            var mark = new Ellipse { Width = 10, Height = 10, Stroke = Brush("Dim"), StrokeThickness = 1.5, Margin = new Thickness(4, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(mark);
            var txt = new TextBlock { Text = name, Foreground = Brush("Dim") };
            row.Children.Add(txt);
            rows.Children.Add(row);
            p.Rows.Add((mark, txt));
        }

        PackOverlay.Visibility = Visibility.Visible;
        PackOverlay.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PackOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        ((ScaleTransform)box.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)box.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        // the bar's inner border needs the track width: size it once it is laid out
        track.Loaded += (_, _) => ((Border)track.Child).Width = track.ActualWidth;
        return p;
    }

    /// <summary>Step <paramref name="i"/> is running (earlier ones done); <paramref name="frac"/> = the bar's share at its start.</summary>
    void PackStep(PackPanel p, int i, double frac)
    {
        for (int k = 0; k < p.Rows.Count; k++)
        {
            var (mark, text) = p.Rows[k];
            var row = (DockPanel)text.Parent;
            if (k < i) SetRowDone(row, mark);
            else if (k == i)
            {
                row.Opacity = 1;
                text.Foreground = Brush("Txt");
                if (mark is Ellipse el) { el.Stroke = Brush("Acc"); el.Fill = Brush("AccBg"); el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(500)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }); }
            }
        }
        PackProgress(p, frac);
    }

    void SetRowDone(DockPanel row, FrameworkElement mark)
    {
        if (row.Children[0] is not Ellipse) return;
        row.Opacity = 1;
        var check = IconPath("IcCheck", "Ok", 0.5);
        check.Width = 18; check.Height = 18; check.Margin = new Thickness(0, 0, 8, 0);
        row.Children.RemoveAt(0);
        row.Children.Insert(0, check);
        ((TextBlock)row.Children[1]).Foreground = Brush("Dim");
    }

    /// <summary>Move the bar smoothly to <paramref name="frac"/> and show the time left, from the speed so far.</summary>
    void PackProgress(PackPanel p, double frac)
    {
        frac = Math.Clamp(frac, 0, 1);
        if (frac < p.Shown) return;
        p.Shown = frac;
        p.Bar.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(frac, TimeSpan.FromMilliseconds(240)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        double sec = p.Clock.Elapsed.TotalSeconds;
        p.Eta.Text = frac >= 1 ? $"Done in {sec:0.0} s"
                   : frac < 0.08 ? "Working…"
                   : (sec / frac * (1 - frac)) is var left && left < 1.5 ? $"{(int)(frac * 100)}% · a moment left" : $"{(int)(frac * 100)}% · about {Math.Ceiling(left)} s left";
    }

    /// <summary>The end state: a popping check (or a red cross) and the buttons.</summary>
    void PackFinish(PackPanel p, bool ok, string title, string sub, params (string Label, bool Primary, Action Click)[] buttons)
    {
        for (int k = 0; k < p.Rows.Count && ok; k++) SetRowDone((DockPanel)p.Rows[k].Text.Parent, p.Rows[k].Mark);
        if (!ok)
            foreach (var (mark, text) in p.Rows)
                if (mark is Ellipse el && text.Foreground == Brush("Txt")) { el.BeginAnimation(OpacityProperty, null); el.Stroke = Brush("Bad"); el.Fill = Brush("BadBg"); text.Foreground = Brush("Bad"); }
        if (ok) PackProgress(p, 1); else p.Eta.Text = "";
        if (!ok) p.Track.Visibility = Visibility.Collapsed;
        p.IconBox.Background = Brush(ok ? "OkBg" : "BadBg");
        var icon = IconPath(ok ? "IcCheck" : "IcWarn", ok ? "Ok" : "Bad", 1.2);
        p.IconBox.Child = icon;
        var st = (ScaleTransform)p.IconBox.RenderTransform;
        var pop = new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        if (ok)
        {
            // a soft green ring that spreads out once behind the check
            p.IconBox.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x4A, 0xD2, 0x95), BlurRadius = 30, ShadowDepth = 0, Opacity = 0 };
            p.IconBox.Effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, new DoubleAnimation(0, 0.9, TimeSpan.FromMilliseconds(380)) { AutoReverse = true });
        }
        p.Title.Text = title;
        p.Sub.Text = sub;
        p.Sub.Foreground = Brush(ok ? "Dim" : "Bad");
        p.Buttons.Children.Clear();
        foreach (var (label, primary, click) in buttons)
        {
            var b = new Button { Style = (Style)FindResource(primary ? "BtnPri" : "Btn"), Content = label, Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(5, 0, 5, 0) };
            b.Click += (_, _) => click();
            p.Buttons.Children.Add(b);
        }
        p.Buttons.Visibility = Visibility.Visible;
        p.Buttons.Opacity = 0;
        p.Buttons.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)) { BeginTime = TimeSpan.FromMilliseconds(150) });
    }

    /// <summary>
    /// The preview before an install: the game files the pack's mods may change, by mod, then Cancel / Install.
    /// The step rows and the bar wait hidden until the answer. Built once per install (a few dozen text lines).
    /// </summary>
    async Task<bool> PackPreviewAsync(PackPanel p, IReadOnlyList<PackGameFile> files, bool update)
    {
        var host = (StackPanel)p.Steps.Parent;
        p.Steps.Visibility = Visibility.Collapsed;
        p.Track.Visibility = Visibility.Collapsed;
        p.Eta.Visibility = Visibility.Collapsed;
        var view = new StackPanel();
        view.Children.Add(new TextBlock
        {
            Text = files.Count == 0 ? "Its mods change no game files." : $"Its mods may change {files.Count} game file{(files.Count == 1 ? "" : "s")}, only when you turn them on:",
            Foreground = Brush("Dim"), FontSize = 12, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap,
        });
        var list = new StackPanel { Margin = new Thickness(12, 8, 12, 8) };
        foreach (var mod in files.GroupBy(f => f.Mod))
        {
            list.Children.Add(new TextBlock { Text = mod.Key, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 8, 0, 2) });
            foreach (var f in mod)
                list.Children.Add(new TextBlock { Text = f.Path, FontSize = 11.5, Foreground = Brush("Dim"), TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = f.Path });
        }
        if (files.Count > 0)
            view.Children.Add(new Border
            {
                Background = Brush("Well"), CornerRadius = new CornerRadius(10),
                Child = new ScrollViewer { MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list },
            });
        view.Children.Add(new TextBlock
        {
            Text = update ? "The old version's mods are turned off first (your game files go back to vanilla)." : "Nothing in the game changes now: you turn mods on in their cards, then Apply.",
            Foreground = Brush("Dim"), FontSize = 12, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
        });
        host.Children.Insert(host.Children.IndexOf(p.Steps), view);
        FadeSlideIn(view, 0);

        var answer = new TaskCompletionSource<bool>();
        p.IconBox.Child = IconPath("IcShield", "Acc", 1.2);
        p.Buttons.Children.Clear();
        var no = new Button { Style = (Style)FindResource("Btn"), Content = "Cancel", Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(5, 0, 5, 0), IsCancel = true };
        var yes = new Button { Style = (Style)FindResource("BtnPri"), Content = "Install", Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(5, 0, 5, 0), IsDefault = true };
        no.Click += (_, _) => answer.TrySetResult(false);
        yes.Click += (_, _) => answer.TrySetResult(true);
        p.Buttons.Children.Add(no);
        p.Buttons.Children.Add(yes);
        p.Buttons.Visibility = Visibility.Visible;
        bool ok = await answer.Task;

        host.Children.Remove(view);
        p.Buttons.Visibility = Visibility.Collapsed;
        p.Buttons.Children.Clear();
        p.Steps.Visibility = Visibility.Visible;
        p.Track.Visibility = Visibility.Visible;
        p.Eta.Visibility = Visibility.Visible;
        var spin = Spinner();
        spin.Visibility = Visibility.Visible;
        p.IconBox.Child = spin;
        p.Clock.Restart();   // the ETA counts from the yes, not from the reading time
        return ok;
    }

    void ClosePackPanel()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => { if (PackOverlay.Opacity == 0) { PackOverlay.Visibility = Visibility.Collapsed; PackOverlay.Children.Clear(); } };
        PackOverlay.BeginAnimation(OpacityProperty, fade);
        _packBusy = false;
    }

    // ---------- card animations ----------

    static void FadeSlideIn(FrameworkElement el, int delayMs)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.Opacity = 0;
        var move = new TranslateTransform(0, 16);
        el.RenderTransform = move;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(340)) { BeginTime = begin, EasingFunction = ease };
        fade.Completed += (_, _) => { el.BeginAnimation(OpacityProperty, null); el.Opacity = 1; el.RenderTransform = Transform.Identity; };
        el.BeginAnimation(OpacityProperty, fade);
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(380)) { BeginTime = begin, EasingFunction = ease });
    }

    static void FadeOutRemove(FrameworkElement el, Panel parent)
    {
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform(1, 1);
        el.RenderTransform = sc;
        el.IsHitTestVisible = false;
        var d = TimeSpan.FromMilliseconds(260);
        var fade = new DoubleAnimation(0, d) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => parent.Children.Remove(el);
        el.BeginAnimation(OpacityProperty, fade);
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, d));
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, d));
    }
}
