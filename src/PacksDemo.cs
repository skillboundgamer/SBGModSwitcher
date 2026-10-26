using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ModSwitcher2;

public partial class App
{
    /// <summary>--pack-demo OUT_DIR APPDATA_DIR ZIP: an off-screen window in pack mode runs the install and remove flow and exits.</summary>
    void StartPackDemo(string outDir, string appData, string zip)
    {
        AppSettings.DirOverride = appData;
        Environment.SetEnvironmentVariable("SBG_PACKS_ONLY", "1");
        ModSwitcher2.MainWindow.PackDemoOut = outDir;
        ModSwitcher2.MainWindow.PackDemoZip = zip;
        new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false }.Show();
    }
}

/// <summary>
/// Hidden demo mode (not in any help text): SBGModSwitcher.exe --pack-demo OUT_DIR APPDATA_DIR ZIP
/// APPDATA_DIR holds a settings.json whose GamePath is a sandbox game folder. The window opens off screen in pack mode,
/// installs ZIP (3-preview: presses Install), opens the pack's tab, removes the pack, and saves a PNG of each moment (1-empty .. 8-removed) and
/// OUT_DIR\perf.txt: the UI-thread time of each pack step, code and to idle. Only APPDATA_DIR and the sandbox are written.
/// </summary>
public partial class MainWindow
{
    public static string? PackDemoOut, PackDemoZip;

    async Task RunPackDemoAsync()
    {
        var lines = new List<string>();
        try
        {
            Directory.CreateDirectory(PackDemoOut!);
            DemoDelayMs = 450;
            await Task.Delay(300);
            await Shot("1-empty");

            ShowDropGlow(true);
            await Task.Delay(500);
            await Shot("2-dropglow");
            ShowDropGlow(false);

            var install = InstallPackAsync(PackDemoZip!);
            Button? go = null;
            for (int i = 0; i < 100 && go == null; i++)   // the preview: wait for its Install button (check runs first)
            {
                await Task.Delay(50);
                go = FindAll<Button>(PackOverlay).FirstOrDefault(b => b.Content as string == "Install");
            }
            await Task.Delay(500);
            await Shot("3-preview");
            if (go == null) throw new InvalidOperationException("the preview did not show");
            go.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await Task.Delay(700);
            await Shot("3b-progress");
            await install;
            await Idle();
            await Task.Delay(700);
            await Shot("4-success");
            var sw = Stopwatch.StartNew();
            ClosePackPanel();
            await Idle();
            lines.Add($"{"close overlay",-40} to idle {sw.Elapsed.TotalMilliseconds,6:0.0} ms");
            await Task.Delay(400);
            await Shot("5-overview");

            var tab = _catalog?.Groups.FirstOrDefault()?.Tab switch { "companions" => TabCompanions, "valhalla" => TabValhalla, "tweaks" => TabTweaks, null => TabOverview, _ => TabBoss };
            sw.Restart();
            tab.IsChecked = true;
            await Idle();
            lines.Add($"{"open the pack's tab",-40} to idle {sw.Elapsed.TotalMilliseconds,6:0.0} ms");
            await Task.Delay(500);
            await Shot("6-tab");
            TabOverview.IsChecked = true;
            await Idle();

            var id = _packs.FirstOrDefault()?.Info.Id;
            if (id != null)
            {
                var remove = RemovePackAsync(id);
                await Task.Delay(400);
                await Shot("7-remove-confirm");
                var yes = FindAll<Button>(PackOverlay).First(b => b.Content as string == "Remove");
                yes.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Task.Delay(900);   // the turn-off step; in the sandbox it fails (no real game files), then "Remove anyway"
                if (FindAll<Button>(PackOverlay).FirstOrDefault(b => b.Content as string == "Remove anyway") is { } anyway)
                {
                    await Shot("7b-remove-failed");
                    anyway.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                await remove;
                await Idle();
                await Task.Delay(700);
                await Shot("8-removed");
                ClosePackPanel();
                lines.Add($"after remove: loadout card {(_loadoutRoot == null ? "never built" : _loadoutRoot.Visibility.ToString())}");

                // reinstall: the mod cards must come back above the Kratos loadout card
                var again = InstallPackAsync(PackDemoZip!);
                Button? go2 = null;
                for (int i = 0; i < 100 && go2 == null; i++)
                {
                    await Task.Delay(50);
                    go2 = FindAll<Button>(PackOverlay).FirstOrDefault(b => b.Content as string == "Install");
                }
                go2?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await again;
                await Idle();
                ClosePackPanel();
                var kids = ViewValhalla.Children.Cast<UIElement>().ToList();
                int lo = _loadoutRoot == null ? -1 : kids.IndexOf(_loadoutRoot);
                var cardsAt = _cards.Values.Where(c => kids.Contains(c.Root)).Select(c => kids.IndexOf(c.Root)).ToList();
                lines.Add($"after reinstall: loadout card {_loadoutRoot?.Visibility.ToString() ?? "never built"}, " +
                          $"valhalla cards {(cardsAt.Count > 0 && (lo < 0 || cardsAt.All(x => x < lo)) ? "above the loadout card" : "BELOW the loadout card")}");
                TabValhalla.IsChecked = true;
                await Idle();
                await Task.Delay(500);
                await Shot("9-reinstalled");
            }
            lines.AddRange(_packPerf);
            lines.Add($"packs left {_packs.Count}, cards {_cards.Count}, groups {_catalog?.Groups.Count}");
        }
        catch (Exception ex) { lines.Add("ERROR " + ex); }
        File.WriteAllLines(Path.Combine(PackDemoOut!, "perf.txt"), lines);
        File.WriteAllText(Path.Combine(PackDemoOut!, "activity.txt"), _activity.ToString());
        Application.Current.Shutdown(0);
    }

    static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindAll<T>(c)) yield return x;
        }
    }

    async Task Shot(string name)
    {
        await Idle();
        var host = (FrameworkElement)Content;
        var bmp = new RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brush("Bg"), null, new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            dc.DrawRectangle(new VisualBrush(host), null, new Rect(0, 0, host.ActualWidth, host.ActualHeight));
        }
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(Path.Combine(PackDemoOut!, name + ".png"));
        enc.Save(f);
    }
}
