using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ModSwitcher2;

/// <summary>Apply one preset (called from the review list in Pending.cs), the restart banner, the toast and the activity log.</summary>
public partial class MainWindow
{
    readonly List<string> _restartPending = new(); // groups changed in a way that needs a full game restart
    bool _restartSeenStop;                           // the game was closed after the change (so its next start loads it)
    readonly StringBuilder _activity = new();

    /// <summary>The matcher of one data root (a pack folder or the app's own); null = the app's data root. One per root, kept
    /// while the game folder stays the same, so its hash cache lives on.</summary>
    ModMatcher Matcher(string? root = null)
    {
        root ??= _dataRoot!;
        if (_matcherGame != _settings.GamePath) { _matchers.Clear(); _matcherGame = _settings.GamePath; }
        if (!_matchers.TryGetValue(root, out var m)) _matchers[root] = m = new ModMatcher(_settings.GamePath!, root);
        return m;
    }

    // ---------- apply ----------

    /// <summary>Applies one preset with its card busy. Returns null on failure, else whether a full restart is needed.</summary>
    async Task<bool?> ApplyAsync(ModGroup g, ModPreset p)
    {
        var c = _cards[g.Id];
        c.Spinner.Visibility = Visibility.Visible;
        var m = Matcher(g.Root);
        var applier = new ModApplier(_settings.GamePath!, g.Root, ModApplier.DefaultRecycle, m);
        await _io.WaitAsync();
        try
        {
            var r = await Task.Run(() => applier.Apply(p));
            foreach (var line in r.Log) Log($"  {line}");
            bool full = r.FullRestart || (!r.NoChange && g.Reload == "full");
            Log($"{g.Name} -> {p.Label}: " + (r.NoChange ? "no change" : full ? "done, full game restart needed" : "done, " + ReloadBadge(g.Reload).Tip), "Ok");
            if (full) AddRestartPending(g.Name);
            return full;
        }
        catch (Exception ex)
        {
            Log($"{g.Name} -> {p.Label}: FAILED: {ex.Message}", "Bad");
            ShowToast($"{g.Name}: {ex.Message}", "Bad");
            return null;
        }
        finally
        {
            // read the card's state back while still holding the lock, so a queued apply cannot write under it
            int idx = await Task.Run(() => m.Check(g));
            _io.Release();
            _active[g.Id] = idx;
            ShowActive(c, idx);
            UpdateActiveChips();
            _ = RefreshSetupAsync();
        }
    }

    /// <summary>The old PowerShell Switcher writes the same files; it is spotted by its window title.</summary>
    static bool OldSwitcherOpen()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("powershell"))
                using (p) if (p.MainWindowTitle.StartsWith("GoWR Mod Switcher")) return true;
        }
        catch (Exception) { }
        return false;
    }

    // ---------- restart banner ----------

    void AddRestartPending(string groupName)
    {
        if (!_restartPending.Contains(groupName)) _restartPending.Add(groupName);
        _restartSeenStop = _gameRunning != true; // game closed now: its next start loads the change
        UpdateBanner();
    }

    /// <summary>Called on every game start / stop seen by the watcher.</summary>
    void RestartWatch(bool running)
    {
        if (_restartPending.Count == 0) return;
        if (!running) _restartSeenStop = true;
        else if (_restartSeenStop) { Log("game restarted: pending changes are now loaded"); _restartPending.Clear(); }
    }

    // ---------- toast + activity ----------

    void ShowToast(string text, string tone)
    {
        ToastText.Text = text;
        ToastText.Foreground = Brush(tone);
        ToastIcon.Stroke = Brush(tone);
        ToastIcon.Data = (Geometry)FindResource(tone == "Bad" ? "IcWarn" : tone == "Ok" ? "IcCheck" : "IcInfo");
        Toast.Background = Brush(tone + "Bg");
        Toast.BorderBrush = Brush(tone);
        Toast.Visibility = Visibility.Visible;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tone == "Bad" ? 7 : 4))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(tone == "Bad" ? 7.6 : 4.6))));
        Toast.BeginAnimation(OpacityProperty, fade);
    }

    void Log(string text, string tone = "Dim")
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        _activity.AppendLine(line);
        ActivityText.Text = _activity.ToString().TrimEnd();
        ActivityScroll.ScrollToEnd();
        ActivityLast.Text = text.Trim();
        ActivityDot.Fill = Brush(tone);
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "activity.log"), $"{DateTime.Now:yyyy-MM-dd} {line}{Environment.NewLine}");
        }
        catch (Exception) { /* the on-screen log still has it */ }
    }

    void ActivityToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = ActivityList.Visibility != Visibility.Visible;
        ActivityList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        ActivityArrow.Text = open ? "Activity ▾" : "Activity ▴";
        if (open) ActivityScroll.ScrollToEnd();
    }

    void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_activity.Length == 0 ? "(empty)" : _activity.ToString()); ShowToast("Activity log copied", "Acc"); }
        catch (Exception ex) { ShowToast("Could not copy: " + ex.Message, "Bad"); }
    }
}
