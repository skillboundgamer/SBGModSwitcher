using System.Windows;
using System.Windows.Controls;

namespace ModSwitcher2;

/// <summary>
/// The VOTG row on the Overview tab (plan 2026-11-17, batch C; design B 2026-11-18): hidden when VOTG is not found. The scan
/// and the repair run off the UI thread, once per game folder (RefreshStatus calls it, the answer is kept), never on a click.
/// The UI work is one text set and one button visibility. The button removes only our own files (VOTG keeps every byte).
/// </summary>
public partial class MainWindow
{
    Border? _votgCard;
    TextBlock? _votgText;
    Button? _votgOff;
    string? _votgScanned;   // the game folder already scanned (case-insensitive); null = none yet
    bool _votgBusy;

    void BuildVotgRow()
    {
        if (_votgCard != null) return;
        _votgText = new TextBlock { FontSize = 12, Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap, MaxWidth = 560,
                                    Margin = new Thickness(22, 1, 0, 0) };
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(IconPath("IcInfo", "Warn", 0.55));
        top.Children.Add(new TextBlock { Text = "VOTG mod loader", FontSize = 13, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        _votgOff = new Button { Style = (Style)FindResource("Btn"), Content = "Turn off our Kratos hook", Padding = new Thickness(8, 2, 8, 2),
                                Margin = new Thickness(22, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        _votgOff.Click += async (_, _) => await TurnOffKratosHookAsync();
        var col = new StackPanel();
        col.Children.Add(top); col.Children.Add(_votgText); col.Children.Add(_votgOff);
        _votgCard = new Border { Style = (Style)FindResource("CardBox"), Width = 640, HorizontalAlignment = HorizontalAlignment.Left,
                                 Margin = new Thickness(0, 18, 16, 0), Child = col, Visibility = Visibility.Collapsed };
        ViewOverview.Children.Insert(_edition?.Id == "tyr" ? 2 : 1, _votgCard);
    }

    async Task RefreshVotgAsync()
    {
        BuildVotgRow();
        var game = _settings.GamePath;
        if (!GameLocator.IsGameDir(game)) { _votgCard!.Visibility = Visibility.Collapsed; _votgScanned = null; return; }
        if (_votgBusy || string.Equals(_votgScanned, game, StringComparison.OrdinalIgnoreCase)) return;   // once per folder
        _votgBusy = true;
        try
        {
            string? data = _dataRoot;
            var (r, repair, mine) = await Task.Run(() =>
            {
                var rep = Votg.Repair(game!, data);   // reads; writes only when our hook went missing (never a VOTG file)
                return (Votg.Check(game!, data), rep, Loader.Mine(game!).Count > 0);
            });
            _votgScanned = game;
            foreach (var l in repair) Log(l, "Warn");
            var t = r.Text();
            _votgText!.Text = t ?? "";
            _votgOff!.Visibility = r.Info != null && mine ? Visibility.Visible : Visibility.Collapsed;
            _votgCard!.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex) { Log("VOTG check failed: " + ex.Message, "Warn"); }
        finally { _votgBusy = false; }
    }

    async Task TurnOffKratosHookAsync()
    {
        var game = _settings.GamePath;
        if (!GameLocator.IsGameDir(game)) return;
        _votgOff!.IsEnabled = false;
        try
        {
            foreach (var l in await Task.Run(() => Loader.Remove(game!))) Log("  " + l);
            Log("Our Kratos hook is off. VOTG was not changed.", "Ok");
            _votgScanned = null;   // scan again
            await RefreshVotgAsync();
        }
        catch (Exception ex) { Log("Turn off failed: " + ex.Message, "Bad"); }
        finally { _votgOff.IsEnabled = true; }
    }
}
