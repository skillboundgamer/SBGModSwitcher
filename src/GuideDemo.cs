using System.IO;
using System.Windows;

namespace ModSwitcher2;

public partial class App
{
    /// <summary>--guide-demo OUT_DIR APPDATA_DIR: an off-screen window in pack mode shows each guide step and exits.</summary>
    void StartGuideDemo(string outDir, string appData)
    {
        AppSettings.DirOverride = appData;
        Environment.SetEnvironmentVariable("SBG_PACKS_ONLY", "1");
        ModSwitcher2.MainWindow.GuideDemoOut = outDir;
        new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false }.Show();
    }
}

/// <summary>
/// Hidden demo mode (not in any help text): SBGModSwitcher.exe --guide-demo OUT_DIR APPDATA_DIR
/// APPDATA_DIR's settings.json decides what the checks find (GamePath = a sandbox folder or none). Saves guide-1..4.png
/// and OUT_DIR\perf.txt (UI-thread ms of each step change). Only OUT_DIR and APPDATA_DIR are written.
/// </summary>
public partial class MainWindow
{
    public static string? GuideDemoOut;

    async Task RunGuideDemoAsync()
    {
        var lines = new List<string>();
        try
        {
            PackDemoOut = GuideDemoOut;   // Shot() writes there
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ShowGuide();
            lines.Add($"open\t{sw.Elapsed.TotalMilliseconds:F1}");
            await Task.Delay(600);
            await Shot("guide-1");
            for (int i = 1; i < GuideNames.Length; i++)
            {
                if (i == 3) _guideAgree = true;
                sw.Restart();
                GuideGo(i, 1);
                lines.Add($"step{i + 1}\t{sw.Elapsed.TotalMilliseconds:F1}");
                await Task.Delay(600);
                await Shot($"guide-{i + 1}");
            }
        }
        catch (Exception ex) { lines.Add("error\t" + ex.Message); }
        File.WriteAllLines(Path.Combine(GuideDemoOut!, "perf.txt"), lines);
        Application.Current.Shutdown(0);
    }
}
