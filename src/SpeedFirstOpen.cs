using System.IO;
using System.Windows;

namespace ModSwitcher2;

/// <summary>
/// First open: a custom walk speed (hero_speed 10..20) and a custom camera FOV (fov_angle) in mod\GOWR_MIO.ini are put back
/// to the game's own values (a ';' in front, the value stays), and one dialog says what was reset. MIO's sample inis
/// (GOWR_MIO.tweeks.ini, GOWR_MIO.full.ini) have both on, so a player who copies one runs too fast and sees another FOV.
/// It runs once per install (AppSettings.SpeedChecked). Later, the Game Tweaks tab has the same resets.
/// </summary>
public partial class MainWindow
{
    async Task FirstOpenSpeedAsync()
    {
        if (_settings.SpeedChecked || !GameLocator.IsGameDir(_settings.GamePath)) return;
        var game = _settings.GamePath!;
        // no ini yet (MIO not installed) or the game runs: not checked, so the next open tries again
        if (!File.Exists(Path.Combine(game, Tweaks.MioIni)) || _gameRunning == true) return;
        int? speedWas, fovWas;
        await _io.WaitAsync();
        try
        {
            (speedWas, fovWas) = await Task.Run(() =>
            {
                var s = Tweaks.SpeedGet(game);
                if (s != null) Tweaks.SpeedReset(game, () => GameLocator.IsGameRunningIn(game));
                var f = Tweaks.FovActiveGet(game);
                if (f != null) Tweaks.FovReset(game, () => GameLocator.IsGameRunningIn(game));
                return (s, f);
            });
        }
        catch (Exception ex) { Log("Walk speed / FOV check failed (nothing more was changed): " + ex.Message, "Warn"); return; }
        finally { _io.Release(); }

        _settings.SpeedChecked = true;
        TrySave();
        if (speedWas == null && fovWas == null) return;
        if (speedWas != null) Log($"Kratos walk speed was {speedWas} in GOWR_MIO.ini: set back to the game's own (old file: GOWR_MIO.ini.sbg_original)", "Ok");
        if (fovWas != null) Log($"Camera FOV was {fovWas} in GOWR_MIO.ini: set back to the game's own (old file: GOWR_MIO.ini.sbg_original)", "Ok");
        LoadTweaksFromDisk();

        bool hasTweaks = TabTweaks.Visibility == Visibility.Visible;
        string text = FirstOpenText(speedWas, fovWas, hasTweaks);
        string title = speedWas != null && fovWas != null ? "Speed and camera set to default"
                     : speedWas != null ? "Kratos speed set to default" : "Camera FOV set to default";
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
        if (hasTweaks) TabTweaks.IsChecked = true;
    }

    /// <summary>The text of the first-open dialog: what was reset, and how to turn each back on.</summary>
    internal static string FirstOpenText(int? speedWas, int? fovWas, bool hasTweaks)
    {
        var sb = new System.Text.StringBuilder("Your GOWR_MIO.ini had settings that change the game (MIO's sample files do this). We set them back to the game's own:\n\n");
        if (speedWas != null) sb.Append($"  - Kratos speed was {speedWas}: he ran faster than the game normally lets him.\n");
        if (fovWas != null) sb.Append($"  - Camera FOV was {fovWas}: if you set it yourself on purpose, it was reset once too.\n");
        sb.Append("\nRestart the game for it to take effect. Your old file is saved as GOWR_MIO.ini.sbg_original.\n\n");
        if (hasTweaks)
        {
            sb.Append("To turn back on:\n");
            if (fovWas != null) sb.Append("  - Camera FOV: Game Tweaks, Camera FOV, switch it on and pick your value.\n");
            if (speedWas != null) sb.Append("  - Kratos speed: this app never makes Kratos faster. Remove the ; in front of hero_speed in mod\\GOWR_MIO.ini.\n");
            sb.Append("\nThe Game Tweaks tab opens when you press OK.");
        }
        else
        {
            sb.Append("To turn back on, open mod\\GOWR_MIO.ini and remove the ; in front of the ");
            sb.Append(speedWas != null && fovWas != null ? "hero_speed or fov_angle line." : speedWas != null ? "hero_speed line." : "fov_angle line.");
        }
        return sb.ToString();
    }
}
