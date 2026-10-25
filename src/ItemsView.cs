using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ModSwitcher2;

/// <summary>
/// Phase 8b: the loadout item list is built on this PC from the player's own game files (ItemsGen), in the
/// background at start, once per language; Settings has the language and a "Rebuild" button.
/// The dev copy switcher\valhalla_items.tsv is only a fallback while nothing is built yet.
/// </summary>
public partial class MainWindow
{
    string? _itemsNote;          // why there is no built list (missing MIO dump, ...), shown in the loadout card
    bool _itemsBuilding, _itemsAgain, _itemsAgainForce;   // a request during a build runs after it (never dropped)
    ComboBox? _langBox;

    /// <summary>
    /// The built list for this language AND this game: the name holds a short key of the game folder and the size /
    /// date of the files it is built from, so another install or a game update builds a fresh list by itself.
    /// </summary>
    string ItemsPath(string lang)
    {
        var game = _settings.GamePath ?? "";
        var sb = new System.Text.StringBuilder(game.ToLowerInvariant());
        foreach (var rel in new[] { ItemsGen.PermDcb, ItemsGen.DumpScript, ItemsGen.LangWad(lang) })
        {
            var f = new FileInfo(Path.Combine(game, rel));
            sb.Append('|').Append(f.Exists ? f.Length + ":" + f.LastWriteTimeUtc.Ticks : "-");
        }
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sb.ToString())))[..10].ToLowerInvariant();
        return Path.Combine(AppSettings.Dir, $"valhalla_items.{lang}.{key}.tsv");
    }

    List<LoadoutItem> CurrentItems()
    {
        var built = ItemsPath(_settings.ItemsLanguage);
        if (File.Exists(built)) return Loadout.ItemsFrom(built);
        return _dataRoot != null ? Loadout.Items(_dataRoot) : new List<LoadoutItem>();
    }

    /// <summary>Build the list for the chosen language when it is missing (or always, with force).</summary>
    async Task EnsureItemsAsync(bool force)
    {
        if (_edition is { Loadout: false }) return;   // an edition without the loadout has no item list
        if (_itemsBuilding) { _itemsAgain = true; _itemsAgainForce |= force; return; }
        if (!GameLocator.IsGameDir(_settings.GamePath)) return;
        var lang = _settings.ItemsLanguage;
        var target = ItemsPath(lang);
        if (!force && File.Exists(target)) return;
        _itemsBuilding = true;
        string game = _settings.GamePath!;
        try
        {
            var text = await Task.Run(() => ItemsGen.Build(game, lang));
            Directory.CreateDirectory(AppSettings.Dir);
            var tmp = target + ".tmp";
            await File.WriteAllTextAsync(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, target, overwrite: true);
            _itemsNote = null;
            Log($"Loadout item list built from your game files ({lang}, {text.Split('\n').Length - 2} items)", "Ok");
            if (force) ShowToast("Item list rebuilt", "Ok");
        }
        catch (Exception ex)
        {
            _itemsNote = "Item list not built: " + ex.Message;
            Log(_itemsNote, "Warn");
            if (force) ShowToast(_itemsNote, "Bad");
        }
        finally { _itemsBuilding = false; ReleaseMemory(); }
        ReloadItems();
        if (_itemsAgain)
        {
            bool again = _itemsAgainForce;
            _itemsAgain = _itemsAgainForce = false;
            await EnsureItemsAsync(again);   // the language / folder may have changed meanwhile
        }
    }

    void BuildItemsSettingsCard()
    {
        var card = new Border { Style = (Style)FindResource("CardBox"), Width = 460 };
        var sp = new StackPanel();
        card.Child = sp;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Child = IconPath("IcShield", "Acc", 1) });
        head.Children.Add(new TextBlock { Text = "Loadout item list", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(head);
        sp.Children.Add(new TextBlock { Text = "Built from your own game files (needs the GoWR MIO dump). Pick the language the names are in.",
                                        Foreground = Brush("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var row = new DockPanel();
        var rebuild = new Button { Style = (Style)FindResource("Btn"), Content = "Rebuild", Margin = new Thickness(8, 0, 0, 0), ToolTip = "Build the list again (after a game update)" };
        rebuild.Click += async (_, _) => await EnsureItemsAsync(true);
        DockPanel.SetDock(rebuild, Dock.Right);
        row.Children.Add(rebuild);
        _langBox = new ComboBox { ToolTip = "Language of item names and descriptions" };
        row.Children.Add(_langBox);
        sp.Children.Add(row);
        ViewSettings.Children.Add(card);
        FillLanguages();
        _langBox.SelectionChanged += async (_, _) =>
        {
            if (_langBox.SelectedItem is not ComboBoxItem it || (string)it.Tag == _settings.ItemsLanguage) return;
            _settings.ItemsLanguage = (string)it.Tag;
            TrySave();
            ReloadItems();                 // the cached list of that language, if built before
            await EnsureItemsAsync(false);
        };
    }

    static readonly Dictionary<string, string> LangNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English", ["en-gb"] = "English (UK)", ["ar"] = "Arabic", ["bp"] = "Portuguese (Brazil)",
        ["chs"] = "Chinese (Simplified)", ["cht"] = "Chinese (Traditional)", ["cs"] = "Czech", ["de"] = "German",
        ["el"] = "Greek", ["es"] = "Spanish (Spain)", ["fr"] = "French", ["hr"] = "Croatian", ["hu"] = "Hungarian",
        ["it"] = "Italian", ["ja"] = "Japanese", ["ko"] = "Korean", ["mx"] = "Spanish (Latin America)", ["nl"] = "Dutch",
        ["pl"] = "Polish", ["pt"] = "Portuguese (Portugal)", ["ru"] = "Russian", ["th"] = "Thai", ["tr"] = "Turkish",
    };

    /// <summary>"German" for "de"; an unknown code stays as it is.</summary>
    static string LangName(string code) => LangNames.TryGetValue(code, out var n) ? n : code;

    void FillLanguages()
    {
        if (_langBox == null) return;
        _langBox.Items.Clear();
        var langs = GameLocator.IsGameDir(_settings.GamePath) ? ItemsGen.Languages(_settings.GamePath!) : new List<string> { "en" };
        if (langs.Count == 0) langs.Add("en");
        // names, English first, then by name (codes are the game's own r_lang_<code>.wad names)
        foreach (var l in langs.OrderBy(c => c == "en" ? "" : LangName(c), StringComparer.OrdinalIgnoreCase))
            _langBox.Items.Add(new ComboBoxItem { Content = LangName(l), Tag = l, ToolTip = $"r_lang_{l}.wad" });
        _langBox.SelectedItem = _langBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.ItemsLanguage) ?? _langBox.Items[0];
    }
}
