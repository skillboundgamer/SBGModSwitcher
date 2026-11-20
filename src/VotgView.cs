using System.Windows;
using System.Windows.Controls;

namespace ModSwitcher2;

/// <summary>
/// VOTG on the Overview (plan 2026-11-17, batch C; design B 2026-11-18; revamp batch B 2026-11-19): the VOTG status card
/// (version, or "Not installed") and a one-line note banner (full text in its tooltip). The scan and the repair run off
/// the UI thread, once per game folder (RefreshStatus calls it, the answer is kept), never on a click. The UI work is a
/// few texts and one button visibility. "Turn off our Kratos hook" removes only our own files (VOTG keeps every byte).
/// </summary>
public partial class MainWindow
{
    string? _votgScanned;   // the game folder already scanned (case-insensitive); null = none yet
    bool _votgBusy;
    string? _votgLine;      // "VOTG mod 2.0.0 found ..." for the diagnostic info; null = no VOTG

    async Task RefreshVotgAsync()
    {
        var game = _settings.GamePath;
        if (!GameLocator.IsGameDir(game)) { ShowVotg(null, null, false); _votgScanned = null; return; }
        if (_votgBusy || string.Equals(_votgScanned, game, StringComparison.OrdinalIgnoreCase)) return;   // once per folder
        _votgBusy = true;
        try
        {
            string? data = _dataRoot;
            (VotgResult, List<string>, bool) res;
            await _io.WaitAsync();   // Repair may write: never while a preset is being written
            try
            {
                res = await Task.Run(() =>
                {
                    var rep = Votg.Repair(game!, data);   // reads; writes only when our hook went missing (never a VOTG file)
                    return (Votg.Check(game!, data), rep, Loader.Mine(game!).Count > 0);
                });
            }
            finally { _io.Release(); }
            var (r, repair, mine) = res;
            _votgScanned = game;
            foreach (var l in repair) Log(l, "Warn");
            ShowVotg(r, r.Text(), r.Info != null && mine);
        }
        catch (Exception ex) { Log("VOTG check failed: " + ex.Message, "Warn"); }
        finally { _votgBusy = false; }
    }

    /// <summary>Texts only: the status card and the note banner (short first sentence; the whole text in the tooltip).</summary>
    void ShowVotg(VotgResult? r, string? text, bool canTurnOff)
    {
        _votgLine = text;
        bool found = r?.Found == true;
        VotgValue.Text = r == null ? "—" : !found ? "Not installed"
            : (r.Info?.ModVersion is { Length: > 0 } v ? "Mod v" + v : "Found") + (r.Clash.Count > 0 ? " · clash" : " · beside ours");
        VotgStatCard.ToolTip = text ?? "VOTG mod loader: not found in this game folder";
        OvTone(VotgIco, VotgIcon, !found ? "ThAccent" : r!.Clash.Count > 0 || r.Info is { Tested: false } ? "ThWarn" : "ThOk");
        VotgBanner.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        if (text == null) return;
        VotgText.Text = text;      // one line; a long text ends in "…" and shows whole in the tooltip
        VotgText.ToolTip = text;
        VotgOff.Visibility = canTurnOff ? Visibility.Visible : Visibility.Collapsed;
    }

    async void VotgOff_Click(object sender, RoutedEventArgs e) => await TurnOffKratosHookAsync();

    async Task TurnOffKratosHookAsync()
    {
        var game = _settings.GamePath;
        if (!GameLocator.IsGameDir(game)) return;
        VotgOff.IsEnabled = false;
        try
        {
            List<string> removed;
            await _io.WaitAsync();   // never remove while a preset is being written
            try { removed = await Task.Run(() => Loader.Remove(game!)); }
            finally { _io.Release(); }
            foreach (var l in removed) Log("  " + l);
            Log("Our Kratos hook is off. VOTG was not changed.", "Ok");
            _votgScanned = null;   // scan again
            await RefreshVotgAsync();
            _ = RefreshScriptLoaderAsync();
            _ = RefreshModsAsync();
        }
        catch (Exception ex) { Log("Turn off failed: " + ex.Message, "Bad"); }
        finally { VotgOff.IsEnabled = true; }
    }
}
