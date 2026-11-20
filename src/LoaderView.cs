using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ModSwitcher2;

/// <summary>The loader line of the Kratos loadout card (Loader.cs): can this game run the loadout, is the loader in.
/// Turning the loader on and off moved to the Overview (OverviewView.cs, revamp batch B); "Remove" stays a row of the
/// Apply list.</summary>
public partial class MainWindow
{
    StackPanel _loLoader = null!;   // the loader line (Loader.cs): can this game run the loadout, is the loader in
    string? _loProblem;             // Loader.Problem: why the loadout cannot run here (the switch stays off), null = it can

    /// <summary>The loader line: the player's MIO can run it (else the switch stays off and the line says why), and is it in.</summary>
    void LoCheckLoader()
    {
        string game = _settings.GamePath!;
        try { _loProblem = Loader.Problem(game); }
        catch (Exception ex) { _loProblem = "Could not read the GoWR MIO files: " + ex.Message; }
        bool present = false;
        try { present = Loader.Present(game); } catch (System.IO.IOException) { }
        string icon, tone, text, tip;
        if (_loProblem != null)
        {
            icon = "IcWarn"; tone = "Bad"; text = "Loadout off: " + _loProblem;
            tip = present
                ? "The loader is in the game, but GoWR MIO cannot run it now: Kratos' script may not load.\n"
                  + "Fix the GoWR MIO setting above, or switch the loadout Off, Apply, and use Remove loader."
                : "The loadout needs GoWR MIO with Lua dump + replace on. Nothing was changed in your game.";
        }
        else if (present)
        {
            icon = "IcCheck"; tone = "Ok"; text = "Loader in place";
            tip = "Kratos' script loads the loadout plug-in (" + string.Join(", ", Loader.Files.Select(f => f.Game)) + ")";
        }
        else
        {
            icon = "IcInfo"; tone = "Dim"; text = "Loader: put in on the first Apply with On";
            tip = "Three small files of ours go into the game's mod folder; anything already there is backed up first.\n"
                  + "After that Apply: load a checkpoint (or restart the game) once.";
        }
        // the switch stays off while the game cannot run it (an On choice saved earlier can still be switched Off)
        _loSwitch.IsEnabled = _loProblem == null || _loSwitch.IsChecked == true;
        _loLoader.Children.Clear();
        _loLoader.Children.Add(IconPath(icon, tone, 0.5));
        _loLoader.Children.Add(new TextBlock { Text = text, Foreground = Brush(tone), FontSize = 12, Margin = new Thickness(5, 0, 0, 0),
                                               VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
        _loLoader.ToolTip = tip + "\nTurn the script loader on or off on the Overview page.";
    }

    /// <summary>"Remove" on the Overview's script loader card (or Restore all): one row of the Apply list that names the
    /// mods that stop. Only while the loadout is Off on disk and in the picks: the plug-in needs the loader to give the
    /// player's own items back (2026-10-21).</summary>
    bool StageLoaderRemove(bool quiet = false)
    {
        if (!GameLocator.IsGameDir(_settings.GamePath)) return false;
        string game = _settings.GamePath!;
        if (_ldr is { Mine: false } || (_ldr == null && Loader.Mine(game).Count == 0)) return false;
        if (_loSaved.On || _loOnNow)
        {
            if (!quiet) ShowToast("Loadout Off + Apply first, then visit the Valhalla shore once: your own items come back", "Warn");
            return false;
        }
        _extra.Remove("loaderon");
        var stops = LoaderUsers();
        _extra["loader"] = new ExtraEdit("overview", "IcPlug", "Script loader",
            "In the game → removed (earlier files come back)" + (stops.Count == 0 ? "" : ". Stops: " + string.Join(", ", stops)),
            "checkpoint", () => RemoveLoaderAsync(game), () => { _extra.Remove("loader"); UpdateScriptLoaderCard(); });
        UpdatePendingUi();
        UpdateScriptLoaderCard();
        if (!quiet && stops.Count > 0) ShowToast("After Apply these stop until the loader is on again: " + string.Join(", ", stops), "Warn");
        return true;
    }

    async Task<bool> RemoveLoaderAsync(string game)
    {
        if (_loSaved.On) { Log("Loadout loader: not removed, the loadout is On (switch it Off and Apply first)", "Warn"); return false; }
        await _io.WaitAsync();
        try
        {
            var log = await Task.Run(() => Loader.Remove(game));
            foreach (var l in log) Log("  " + l);
            Log("Loadout loader removed: load a checkpoint (or restart the game)", "Ok");
            _extra.Remove("loader");
            return true;
        }
        catch (Exception ex)
        {
            Log("Loadout loader: remove FAILED: " + ex.Message, "Bad");
            return false;
        }
        finally
        {
            _io.Release();
            if (_loList != null) LoCheckLoader();
            _ = RefreshScriptLoaderAsync();
            _ = RefreshModsAsync();   // its mods now wait for the loader (Overview banner)
            _votgScanned = null; _ = RefreshVotgAsync();   // the VOTG card is never stale after the loader changed
        }
    }
}
