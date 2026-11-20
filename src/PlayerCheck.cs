#if SBG_DEV
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ModSwitcher2;

public partial class App
{
    /// <summary>--player-check OUT APPDATA STEPS: an off-screen window in pack mode runs the steps a player clicks and writes what it shows.</summary>
    void StartPlayerCheck(string outFile, string appData, string stepsFile)
    {
        AppSettings.DirOverride = appData;
        Environment.SetEnvironmentVariable("SBG_PACKS_ONLY", "1");
        ModSwitcher2.MainWindow.PlayerCheckOut = outFile;
        ModSwitcher2.MainWindow.PlayerCheckSteps = stepsFile;
        new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false }.Show();
    }
}

/// <summary>
/// Hidden test mode (dev build only, not in any help text): SBGSwitcherDev.exe --player-check OUT APPDATA STEPS
/// APPDATA holds a settings.json whose GamePath is a SANDBOX game folder with a file sbg_sandbox.txt (else the mode refuses
/// to run). The window opens off screen in pack mode (SBG_PACKS_ONLY=1, no dev data root) like a player's app.
/// STEPS = one step per line: loader-on (the Overview button's handler), apply (Apply, no confirm box), update on / update off
/// (the Settings switch), wait (idle). After "start" and after each step, OUT gets key TAB value lines:
/// packmode, dataroot, toast (all toasts since the last step, joined by " || "), staged, overview, votg, update, files.
/// Only APPDATA and the sandbox game are written (by Apply).
/// </summary>
public partial class MainWindow
{
    public static string? PlayerCheckOut, PlayerCheckSteps;
    public static List<string>? PlayerCheckToasts;

    async Task RunPlayerCheckAsync()
    {
        var o = new List<string>();
        try
        {
            var game = _settings.GamePath;
            if (string.IsNullOrEmpty(game) || !File.Exists(Path.Combine(game, "sbg_sandbox.txt")))
                throw new InvalidOperationException("refused: the game folder has no sbg_sandbox.txt (never the real game)");
            PlayerCheckToasts = new List<string>();
            RefreshStatus();
            await RefreshScriptLoaderAsync();
            _votgScanned = null; await RefreshVotgAsync();
            await PcSettle();
            PcReport(o, "start", game);
            int n = 0;
            foreach (var raw in File.ReadAllLines(PlayerCheckSteps!))
            {
                var step = raw.Trim();
                if (step.Length == 0 || step.StartsWith("#")) continue;
                n++;
                switch (step)
                {
                    case "loader-on": StageLoaderOn(); break;
                    case "apply": await ApplyPendingAsync(new Dictionary<string, int>(_pending), _extra.Values.ToList(), LoadoutEdited); break;
                    case "wait": break;
#if !SBG_NEXUS
                    case "update on": _updSwitch.IsChecked = true; _updSwitch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
                    case "update off": _updSwitch.IsChecked = false; _updSwitch.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); break;
#endif
                    default: throw new InvalidOperationException("unknown step: " + step);
                }
                await PcSettle();
                if (step.StartsWith("update on"))   // wait for the check to end (a closed port: a few ms)
                    for (int i = 0; i < 200 && _updStatus.Text == "Checking..."; i++) await Task.Delay(100);
                await PcSettle();
                PcReport(o, $"{n} {step}", game);
            }
        }
        catch (Exception ex) { o.Add("error\t" + ex.Message.Replace('\n', ' ')); }
        File.WriteAllLines(PlayerCheckOut!, o);
        Application.Current.Shutdown(0);
    }

    async Task PcSettle()
    {
        await Idle();
        for (int i = 0; i < 100 && (_modChecking || _votgBusy); i++) await Task.Delay(100);
        await Task.Delay(250);
        await Idle();
    }

    void PcReport(List<string> o, string tag, string game)
    {
        o.Add($"== {tag}");
        o.Add("packmode\t" + (_packMode ? "true" : "false"));
        o.Add("dataroot\t" + (_dataRoot ?? "-"));
        o.Add("toast\t" + string.Join(" || ", PlayerCheckToasts!));
        PlayerCheckToasts!.Clear();
        o.Add("staged\t" + string.Join(" || ", _extra.Select(kv => $"{kv.Key}: {kv.Value.Name}: {kv.Value.Change}")));
        o.Add("overview\t" + string.Join(" | ", FindAll<TextBlock>(ViewOverview).Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t))));
        o.Add("votg\t" + VotgValue.Text + " | " + (VotgBanner.Visibility == Visibility.Visible ? VotgText.Text : "-"));
        o.Add("update\t" + _updStatus.Text);
        o.Add("files\t" + string.Join(" | ", Directory.EnumerateFiles(game, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(game, f).Replace('\\', '/')).OrderBy(x => x)));
    }
}
#endif
