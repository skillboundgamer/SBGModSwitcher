using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ModSwitcher2;

/// <summary>One line of the Setup card. Ok: true = done, false = missing, null = not known. Fix: the row has a "Fix it" button.</summary>
public sealed record SetupRow(string Key, string Name, bool? Ok, string Next, bool Fix = false);

/// <summary>
/// The Setup checks of the Straight To Tyr edition, in the order the player has to do them (plan 2026-10-25).
/// Reads files only; reuses Loader (ini, Kratos dump) and Recipe.DumpPath (the door's dump). Each row says what to do next.
/// </summary>
public static class SetupCheck
{
    /// <summary>The script the door recipe is built from (recipes\val_val100_realmhub_door_gate.recipe, source dump).</summary>
    public const string ShoreScript = @"mod\int9\gameart\visualscripts\valhalla\levels\valhalla\valhalla\val_val100_realmhub\val_val100_realmhub.visualscript";

    /// <summary>The game version the app's hashes are for (data\switcher2\vanilla.json "game_version"), or null.</summary>
    public static string? PinnedVersion(string dataRoot)
    {
        try
        {
            var p = Path.Combine(dataRoot, "switcher2", "vanilla.json");
            if (!File.Exists(p)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.TryGetProperty("game_version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    /// <summary>The file version of GoWR.exe as a.b.c.d, or null when the exe has none.</summary>
    public static string? ExeVersion(string gameRoot)
    {
        try
        {
            var exe = Path.Combine(gameRoot, "GoWR.exe");
            if (!File.Exists(exe)) return null;
            var vi = FileVersionInfo.GetVersionInfo(exe);
            if (vi.FileMajorPart + vi.FileMinorPart + vi.FileBuildPart + vi.FilePrivatePart == 0) return null;
            return $"{vi.FileMajorPart}.{vi.FileMinorPart}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The rows while no game folder is set: names only, one next step.</summary>
    public static List<SetupRow> NoFolder() => new()
    {
        new("mio", "GoWR MIO installed", null, "Set the game folder first"),
        new("ini", "MIO settings (dump + replace on)", null, "Set the game folder first"),
        new("version", "Game version", null, "Set the game folder first"),
        new("shore", "Shore script saved by MIO", null, "Set the game folder first"),
        new("kratos", "Kratos script saved by MIO", null, "Set the game folder first"),
        new("door", "Door applied", null, "Set the game folder first"),
    };

    /// <summary>The rows. doorApplied: null = not known yet. exeVersion: only for tests (null = read GoWR.exe).</summary>
    public static List<SetupRow> Run(string gameRoot, string? dataRoot, bool? doorApplied, string? exeVersion = null)
    {
        var rows = new List<SetupRow>();
        bool mio = File.Exists(Path.Combine(gameRoot, Loader.Ini));
        rows.Add(new("mio", "GoWR MIO installed", mio, mio ? "Done." : "Install GoWR MIO first (it is a separate mod loader)."));

        bool? iniOk = null; string iniNext = "Install GoWR MIO first.";
        if (mio)
        {
            try { iniOk = Loader.IniAllOn(File.ReadAllText(Path.Combine(gameRoot, Loader.Ini))); }
            catch (IOException) { iniOk = null; }
            iniNext = iniOk == true ? "Done." : iniOk == false ? @"Press Fix it: dump and replace get turned on in mod\GOWR_MIO.ini." : "Could not read the ini.";
        }
        rows.Add(new("ini", "MIO settings (dump + replace on)", mio ? iniOk : false, iniNext, Fix: mio && iniOk == false));

        var pinned = dataRoot == null ? null : PinnedVersion(dataRoot);
        var have = exeVersion ?? ExeVersion(gameRoot);
        if (pinned == null || have == null)
            rows.Add(new("version", "Game version", null, have == null ? "Unknown: this game's exe has no version. Nothing to do." : "Unknown: no version to compare with."));
        else if (have == pinned)
            rows.Add(new("version", "Game version", true, "Done. " + have));
        else
            rows.Add(new("version", "Game version", false, $"Your game is {have}, this app is made for {pinned}. Update the game (or the app), then press Refresh."));

        bool shore = File.Exists(Path.Combine(gameRoot, Recipe.DumpPath(ShoreScript)));
        // a missing dump is not a problem: Apply takes it from the player's own wad (DumpFill)
        rows.Add(new("shore", "Shore script (from your game files)", shore ? true : null, shore ? "Done."
            : "Nothing to do: Apply changes takes it from your own game files."));

        bool kHave = File.Exists(Path.Combine(gameRoot, Loader.Dump));
        var kd = Loader.DumpProblem(gameRoot);   // null when missing but the wad is there (Apply fills it)
        rows.Add(new("kratos", "Kratos script (from your game files)", kHave || kd != null ? kd == null : null,
            kd ?? (kHave ? "Done." : "Nothing to do: Apply changes takes it from your own game files.")));

        rows.Add(new("door", "Door applied", doorApplied,
            doorApplied == true ? "Done. The door works after the Valhalla story; before that it stays normal."
            : "On the Valhalla tab pick Door -> Tyr, then Apply."));
        return rows;
    }
}

/// <summary>The Setup card on the Overview tab of the Tyr edition: built once, a check only changes the rows that changed.</summary>
public partial class MainWindow
{
    sealed class SetupLine
    {
        public required System.Windows.Shapes.Path Icon; public required TextBlock Next; public required Button FixButton; public SetupRow? Shown;
    }
    readonly Dictionary<string, SetupLine> _setupLines = new();
    List<string> _setupOrder = new();
    bool _setupBusy, _setupAgain;

    void BuildSetupCard()
    {
        if (_edition?.Id != "tyr") return;
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Setup", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var key in new[] { "mio", "ini", "version", "shore", "kratos", "door" })
        {
            var icon = IconPath("IcInfo", "Dim", 0.55);
            var name = new TextBlock { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            var next = new TextBlock { FontSize = 12, Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap, MaxWidth = 560 };
            var fix = new Button { Style = (Style)FindResource("Btn"), Content = "Fix it", Padding = new Thickness(8, 2, 8, 2),
                                   Margin = new Thickness(10, 0, 0, 0), FontSize = 12, Visibility = Visibility.Collapsed,
                                   VerticalAlignment = VerticalAlignment.Center };
            fix.Click += async (_, _) => await FixIniAsync();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(icon); top.Children.Add(name); top.Children.Add(fix);
            name.Margin = new Thickness(6, 0, 0, 0);
            name.Tag = key;
            var col = new StackPanel { Margin = new Thickness(0, 0, 0, 7) };
            col.Children.Add(top);
            next.Margin = new Thickness(22, 1, 0, 0);
            col.Children.Add(next);
            stack.Children.Add(col);
            _setupLines[key] = new SetupLine { Icon = icon, Next = next, FixButton = fix };
            _setupOrder.Add(key);
            _setupNames[key] = name;
        }
        var card = new Border { Style = (Style)FindResource("CardBox"), Width = 640, HorizontalAlignment = HorizontalAlignment.Left,
                                Margin = new Thickness(0, 18, 16, 0), Child = stack };
        ViewOverview.Children.Insert(1, card);
    }
    readonly Dictionary<string, TextBlock> _setupNames = new();

    /// <summary>The checks run off the UI thread; then only the rows that changed are touched.</summary>
    async Task RefreshSetupAsync()
    {
        if (_setupLines.Count == 0) return;
        if (!GameLocator.IsGameDir(_settings.GamePath)) { ShowSetupRows(SetupCheck.NoFolder()); return; }
        if (_setupBusy) { _setupAgain = true; return; }
        _setupBusy = true;
        try
        {
            do
            {
                _setupAgain = false;
                string game = _settings.GamePath!; string? data = _dataRoot;
                bool? door = _catalog?.Groups.FirstOrDefault(g => g.Id == "valdoor") is { } dg && _active.TryGetValue(dg.Id, out var i)
                    ? (i < 0 ? null : i != dg.Vanilla) : null;   // -1 = matches no preset: not known
                var rows = await Task.Run(() => SetupCheck.Run(game, data, door));
                ShowSetupRows(rows);
            } while (_setupAgain);
        }
        catch (Exception ex) { Log("Setup check failed: " + ex.Message, "Warn"); }
        finally { _setupBusy = false; }
    }

    void ShowSetupRows(List<SetupRow> rows)
    {
        foreach (var r in rows)
        {
            var line = _setupLines[r.Key];
            if (line.Shown == r) continue;                     // unchanged: nothing is touched
            line.Shown = r;
            _setupNames[r.Key].Text = r.Name;
            var (icon, tone) = r.Ok == true ? ("IcCheck", "Ok") : ("IcInfo", "Dim");
            line.Icon.Data = (Geometry)FindResource(icon);
            line.Icon.Stroke = Brush(tone);
            line.Next.Text = r.Next;
            line.FixButton.Visibility = r.Fix ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    async Task FixIniAsync()
    {
        if (!GameLocator.IsGameDir(_settings.GamePath)) return;
        string game = _settings.GamePath!;
        await _io.WaitAsync();
        try
        {
            var msg = await Task.Run(() => Loader.IniFix(game));
            Log("GoWR MIO: " + msg, "Ok");
            ShowToast(msg, "Ok");
        }
        catch (Exception ex) { Log("GoWR MIO fix FAILED: " + ex.Message, "Bad"); ShowToast(ex.Message, "Bad"); }
        finally { _io.Release(); }
        if (_loLoader != null) LoCheckLoader();
        _ = RefreshSetupAsync();
    }

    /// <summary>Restore all: puts the player's first GOWR_MIO.ini back. Only when no loader file of ours would be left without it.</summary>
    async Task<bool> StageIniRestoreAsync()
    {
        if (!GameLocator.IsGameDir(_settings.GamePath)) return false;
        string game = _settings.GamePath!;
        var (backup, loaderFiles) = await Task.Run(() => (Loader.IniBackupExists(game), Loader.Mine(game).Count > 0));   // file reads off the UI thread
        if (!backup) return false;
        if (loaderFiles && !_extra.ContainsKey("loader")) return false;   // the loadout loader still needs the ini
        _extra["iniback"] = new ExtraEdit("valhalla", "IcPlug", "GoWR MIO settings", @"mod\GOWR_MIO.ini → back as it was",
            "full", async () =>
            {
                await _io.WaitAsync();
                try { var m = await Task.Run(() => Loader.IniRestore(game)); if (m != null) Log("GoWR MIO: " + m, "Ok"); _extra.Remove("iniback"); return true; }
                catch (Exception ex) { Log("GoWR MIO restore FAILED: " + ex.Message, "Bad"); return false; }
                finally { _io.Release(); _ = RefreshSetupAsync(); }
            }, () => _extra.Remove("iniback"));
        return true;
    }
}
