using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ModSwitcher2;

/// <summary>
/// Settings card "Updates" and, in the GitHub build, the top bar's "Update available" button.
/// GitHub build: one check at start, off the UI thread (Updater.cs); a newer release shows the button with its notes;
/// a click asks first, then downloads + checks off the UI thread and hands over to the helper (UpdateHelper.cs).
/// Nexus build (SBG_NEXUS): no network code at all, the card only says where new versions are.
/// </summary>
public partial class MainWindow
{
    TextBlock _updStatus = null!;

    TextBlock UpdateCardHead(StackPanel sp)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Child = IconPath("IcInfo", "Acc", 1) });
        head.Children.Add(new TextBlock { Text = "Updates", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        var status = new TextBlock { Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap };
        return status;
    }

#if SBG_NEXUS
    void BuildUpdateCard()
    {
        var card = new Border { Style = (Style)FindResource("CardBox"), Width = 460 };
        var sp = new StackPanel();
        card.Child = sp;
        _updStatus = UpdateCardHead(sp);
        _updStatus.Text = "New version: see the GitHub page or the Nexus page of the Mod Switcher. This build does not go online.";
        sp.Children.Add(_updStatus);
        ViewSettings.Children.Add(card);
    }

    void StartUpdateCheck() { }
#else
    CheckBox _updSwitch = null!;
    Button? _updButton;
    ReleaseInfo? _update;
    bool _updChecked, _updBusy;
    string? _updLast;   // the last check's status text: shown again when the switch goes off and on

    void BuildUpdateCard()
    {
        var card = new Border { Style = (Style)FindResource("CardBox"), Width = 460 };
        var sp = new StackPanel();
        card.Child = sp;
        _updStatus = UpdateCardHead(sp);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _updSwitch = new CheckBox { Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
                                    IsChecked = _settings.CheckUpdates, ToolTip = "Once at start: asks GitHub for the newest version. Nothing about you is sent." };
        row.Children.Add(_updSwitch);
        row.Children.Add(new TextBlock { Text = "Check for updates", VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(row);
        sp.Children.Add(_updStatus);
        _updStatus.Text = _settings.CheckUpdates ? "Not checked yet." : "Off. New versions: see the GitHub page.";
        _updSwitch.Click += (_, _) =>
        {
            _settings.CheckUpdates = _updSwitch.IsChecked == true;
            TrySave();
            if (_settings.CheckUpdates) StartUpdateCheck();
            else _updStatus.Text = "Off. New versions: see the GitHub page.";
        };
        ViewSettings.Children.Add(card);
    }

    /// <summary>Once per start (and when the switch is turned on): the check runs off the UI thread; only its result touches the UI.</summary>
    void StartUpdateCheck()
    {
        if (!_settings.CheckUpdates) return;
        if (_updChecked)   // once per start; the switch back on shows the result (and its button) again
        {
            _updStatus.Text = _updLast ?? "Checking...";
            if (_update is { } u) ShowUpdateButton(u);
            return;
        }
        if (_edition != null && !_packMode) { _updStatus.Text = "This edition is updated with its own download."; return; }
        _updChecked = true;
        _updStatus.Text = "Checking...";
        _ = CheckUpdateAsync();
    }

    async Task CheckUpdateAsync()
    {
        try
        {
            var cur = Updater.Current;
            var r = await Task.Run(async () => { UpdateHelper.CleanTemp(); return await Updater.CheckAsync(cur); });
            _update = r;
            if (r == null) { SetUpdStatus($"Up to date (v{Updater.Current})."); return; }
            SetUpdStatus($"Version {r.Version} is out (you have {Updater.Current}). Use the Update button at the top.");
            if (_settings.CheckUpdates) ShowUpdateButton(r);   // turned off while the check ran: the button waits for the switch
        }
        catch (UpdateError ex) { SetUpdStatus(ex.Message); }
        catch (Exception) { SetUpdStatus("The update check did not work this time."); }
    }

    /// <summary>Keeps the result and shows it, unless the switch was turned off while the check ran.</summary>
    void SetUpdStatus(string text)
    {
        _updLast = text;
        if (_settings.CheckUpdates) _updStatus.Text = text;
    }

    void ShowUpdateButton(ReleaseInfo r)
    {
        if (_updButton == null)
        {
            _updButton = new Button { Style = (Style)FindResource("BtnPri"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 10, 0),
                                      VerticalAlignment = VerticalAlignment.Center };
            _updButton.Click += UpdateButton_Click;
            TopChips.Children.Insert(0, _updButton);
        }
        _updButton.Content = $"Update available · v{r.Version}";
        _updButton.ToolTip = "What is new:\n" + Updater.ShortNotes(r.Notes, 600);
        System.Windows.Automation.AutomationProperties.SetName(_updButton, $"Update available, version {r.Version}");
    }

    async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updBusy || _update is not { } r) return;
        if (PendingCount > 0)
        {
            ShowToast("Apply or discard your changes first, then update.", "Warn");
            return;
        }
        _updBusy = true;   // a second click while this one waits does nothing
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            if (!await Task.Run(() => UpdateHelper.CanWrite(dir)))   // a tiny file write: off the UI thread
            {
                MessageBox.Show(this, "This app's folder cannot be written, so it cannot update itself. Download the new version from the GitHub page.",
                                "Mod Switcher update", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var ask = $"Version {r.Version} is out (you have {Updater.Current}).\n\nWhat is new:\n{Updater.ShortNotes(r.Notes)}\n\n" +
                      "Update now? The app downloads it, checks it, closes and starts again. Your settings and mods stay.";
            if (MessageBox.Show(this, ask, "Mod Switcher update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            await DownloadAndRestartAsync(r);
        }
        finally { _updBusy = false; }
    }

    async Task DownloadAndRestartAsync(ReleaseInfo r)
    {
        _updButton!.IsEnabled = false;
        _updButton.Content = "Downloading...";
        var progress = new Progress<int>(p => { if (_updButton != null) _updButton.Content = $"Downloading... {p}%"; });
        try
        {
            var staging = Path.Combine(UpdateHelper.TempRoot, r.Version.ToString());
            var newDir = await Task.Run(() => UpdateHelper.PrepareAsync(r, staging, progress));
            await Task.Run(() => UpdateHelper.StartHelper(staging, newDir));
            Log($"Update {r.Version}: downloaded and checked, the app restarts to install it", "Ok");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            var msg = ex is UpdateError ? ex.Message : "The update did not work: " + ex.Message + " Nothing was changed.";
            Log(msg, "Warn");
            MessageBox.Show(this, msg, "Mod Switcher update", MessageBoxButton.OK, MessageBoxImage.Warning);
            _updButton.IsEnabled = true;
            ShowUpdateButton(r);
        }
    }
#endif
}
