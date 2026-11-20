using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ModSwitcher2;

// GitHub build only (left out of the Nexus build by the csproj).

/// <summary>
/// Headless update modes (App.xaml.cs). The tests use them with a local fake feed (env SBG_UPDATE_FEED):
///   --update-check [--current X]                      "current/latest/newer/zip/sha" lines, or "error&lt;TAB&gt;text" + exit 1
///   --update-prepare --staging DIR [--current X]      check + download + sha256 + unpack into DIR\new ("ready&lt;TAB&gt;DIR\new")
///   --update-apply --from DIR --to DIR [--pid N] [--restart EXE] [--gui]
///                                                     the helper: wait for process N, install, start EXE again.
///                                                     --gui = the player's run: a failure is a message box, then the old app starts
///   --update-rollback --to DIR                        put back the files the last update replaced (their .old copies)
/// </summary>
public static class UpdateCli
{
    public static int Run(string[] a, TextWriter o)
    {
        string? Opt(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        var current = Opt("--current") is { } c ? Updater.ParseVersion(c) ?? throw new ArgumentException("bad --current") : Updater.Current;
        try
        {
            switch (a[0])
            {
                case "--update-check":
                case "--update-prepare":
                {
                    o.WriteLine($"current\t{current}");
                    var r = Task.Run(() => Updater.CheckAsync(current)).GetAwaiter().GetResult();
                    o.WriteLine($"newer\t{(r != null ? "true" : "false")}");
                    if (r == null) return 0;
                    o.WriteLine($"latest\t{r.Version}");
                    o.WriteLine($"zip\t{r.ZipName}");
                    o.WriteLine($"sha\t{r.Sha256}");
                    if (a[0] == "--update-check") return 0;
                    var staging = Opt("--staging") ?? throw new ArgumentException("--update-prepare needs --staging <folder>");
                    o.WriteLine("ready\t" + Task.Run(() => UpdateHelper.PrepareAsync(r, staging)).GetAwaiter().GetResult());
                    return 0;
                }
                case "--update-apply":
                    return ApplyRun(Opt("--from") ?? throw new ArgumentException("--update-apply needs --from <folder>"),
                                    Opt("--to") ?? throw new ArgumentException("--update-apply needs --to <folder>"),
                                    Opt("--pid") is { } p ? int.Parse(p) : null, Opt("--restart"), Array.IndexOf(a, "--gui") >= 0, o);
                case "--update-rollback":
                    o.WriteLine("restored\t" + UpdateHelper.Rollback(Opt("--to") ?? throw new ArgumentException("--update-rollback needs --to <folder>")));
                    return 0;
                default:
                    return 2;
            }
        }
        catch (UpdateError ex)
        {
            o.WriteLine("error\t" + ex.Message);
            return 1;
        }
    }

    static int ApplyRun(string from, string to, int? pid, string? restart, bool gui, TextWriter o)
    {
        int code = 0;
        string? fail = null;
        int files = 0;
        try
        {
            if (pid is { } id && !WaitExit(id, TimeSpan.FromSeconds(60)))
                throw new UpdateError("The Mod Switcher did not close, so the update was not installed. Nothing was changed.");
            files = UpdateHelper.Apply(from, to, l => o.WriteLine(l));
            o.WriteLine("files\t" + files);
            o.WriteLine("ok");
        }
        catch (UpdateError ex)
        {
            fail = ex.Message;
            o.WriteLine("error\t" + fail);
            code = 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // e.g. the staging folder is gone: nothing was changed, but the player still gets a message + a restart
            fail = $"The update could not be installed ({ex.Message}). Nothing was changed.";
            o.WriteLine("error\t" + fail);
            code = 1;
        }
        Note(fail == null ? $"Update installed ({files} files; the old ones are kept as .old)" : "Update failed: " + fail);
        if (gui && fail != null)
            MessageBox.Show(fail, "Mod Switcher update", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (restart != null)
        {
            // always the updated exe, never a renamed old copy that Apply did not replace (review 2026-11-20)
            var exe = Path.Combine(to, UpdateHelper.AppExe);
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = to })?.Dispose(); }
            catch (Exception ex) { if (gui) MessageBox.Show("Could not start the Mod Switcher again: " + ex.Message, "Mod Switcher update"); }
        }
        return code;
    }

    static bool WaitExit(int pid, TimeSpan max)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.WaitForExit(max);
        }
        catch (ArgumentException) { return true; }   // already gone
    }

    /// <summary>One line in the app's activity.log, so the next start (and a bug report) shows how the update went.</summary>
    static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "activity.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
