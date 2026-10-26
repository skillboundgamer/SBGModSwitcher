using System.Windows;
using System.Windows.Controls;

namespace ModSwitcher2;

/// <summary>The "Burden glyphs" block of the Kratos loadout card: 3 pickers (the plug-in puts them in Kratos' Talisman GlyphSlot1-3).</summary>
public partial class MainWindow
{
    readonly string[] _loBurdenBox = new string[Loadout.MaxBurdens];   // what each burden box holds ("" = None)
    readonly ComboBox[] _loBurdenCombos = new ComboBox[Loadout.MaxBurdens];
    /// <summary>The burden glyphs picked now, in box order, without the empty boxes.</summary>
    List<string> _loBurdens => _loBurdenBox.Where(n => !string.IsNullOrEmpty(n)).ToList();
    static readonly string[] BurdenKinds = Loadout.GlyphKinds.Where(k => k != "Stat" && k != "Technique").ToArray();   // no Stat or "+" glyphs in the burden slots (the plug-in turns "+" skills on only in the glyph list)

    /// <summary>Under both columns of the card: the burden glyphs (3 pickers).</summary>
    void BuildBurdenBox(StackPanel body)
    {
        var burden = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        burden.Children.Add(new TextBlock { Text = "Burden glyphs", Style = (Style)FindResource("Section") });
        burden.Children.Add(new TextBlock { Text = "3 extra glyphs in the game's burden slots", Foreground = Brush("Dim"), FontSize = 12,
                                            Margin = new Thickness(0, 0, 0, 8) });
        var bgrid = new Grid();
        for (int i = 0; i < Loadout.MaxBurdens; i++)
        {
            if (i > 0) bgrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            bgrid.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int i = 0; i < Loadout.MaxBurdens; i++)
        {
            int idx = i;
            var cb = new ComboBox { Margin = new Thickness(0, 0, 0, 6) };
            cb.SelectionChanged += (_, _) =>
            {
                if (_loFilling || cb.SelectedItem is not ComboBoxItem sel) return;
                var old = _loBurdenBox[idx];
                _loBurdenBox[idx] = (string)sel.Tag;
                FillBurdenCombos(idx);   // the other two boxes drop this glyph from their lists
                LoUpdateRows(old, _loBurdenBox[idx]);   // the glyph list marks a burden pick: only those two rows (tooltips)
                LoChanged();
            };
            Grid.SetColumn(cb, i * 2);
            bgrid.Children.Add(cb);
            _loBurdenCombos[i] = cb;
        }
        burden.Children.Add(bgrid);
        body.Children.Add(burden);
        FillBurdenCombos();
    }

    /// <summary>
    /// Refill the 3 burden boxes (except box `skip`, the one the player just used). A box lists None + glyphs of kinds Perk / Axe /
    /// Blades / Spear, minus the glyphs in the normal list and the glyphs held by the other two boxes; its own pick always stays.
    /// </summary>
    void FillBurdenCombos(int skip = -1)
    {
        bool was = _loFilling;
        _loFilling = true;
        for (int i = 0; i < _loBurdenCombos.Length; i++)
        {
            if (i == skip || _loBurdenCombos[i] == null) continue;
            var cb = _loBurdenCombos[i];
            cb.Items.Clear();
            foreach (var (tag, it) in LoBurdenWant(i)) cb.Items.Add(BurdenItem(tag, it));
            LoBurdenSelect(i);
        }
        _loFilling = was;
    }

    /// <summary>What box i lists, in order: None, the free glyphs of the burden kinds, its own pick last when unknown.</summary>
    List<(string Tag, LoadoutItem? It)> LoBurdenWant(int i)
    {
        var mine = _loBurdenBox[i] ?? "";
        var taken = _loBurdenBox.Where((n, j) => j != i && !string.IsNullOrEmpty(n)).ToHashSet();
        var want = new List<(string Tag, LoadoutItem? It)> { ("", null) };
        foreach (var it in _loItems)
        {
            if (!BurdenKinds.Contains(it.Kind)) continue;
            if (it.Name != mine && (_loGlyphs.Contains(it.Name) || _loStats.Contains(it.Name) || taken.Contains(it.Name))) continue;
            want.Add((it.Name, it));
        }
        if (mine.Length > 0 && !want.Any(w => w.Tag == mine)) want.Add((mine, null));   // item list not built yet / glyph unknown
        return want;
    }

    static ComboBoxItem BurdenItem(string tag, LoadoutItem? it) =>
        tag.Length == 0 ? new ComboBoxItem { Content = "None", Tag = "" }
        : it == null ? new ComboBoxItem { Content = tag, Tag = tag }
        : new ComboBoxItem { Content = $"{it.Label}  ({it.Kind})", Tag = it.Name, ToolTip = it.Desc.Length > 0 ? it.Desc : null };

    void LoBurdenSelect(int i)
    {
        var cb = _loBurdenCombos[i];
        var mine = _loBurdenBox[i] ?? "";
        if ((cb.SelectedItem as ComboBoxItem)?.Tag as string == mine) return;
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == mine || mine.Length == 0 && (string)x.Tag == "");
    }

    /// <summary>
    /// After one glyph went in or out of the chosen list: in each box add or remove only that glyph's item (list order and the
    /// box's pick kept). Anything bigger than one item refills that box as FillBurdenCombos does.
    /// </summary>
    void LoBurdenSync()
    {
        bool was = _loFilling;
        _loFilling = true;
        for (int i = 0; i < _loBurdenCombos.Length; i++)
        {
            var cb = _loBurdenCombos[i];
            if (cb == null) continue;
            var want = LoBurdenWant(i);
            var have = cb.Items.Cast<ComboBoxItem>().Select(x => (string)x.Tag).ToList();
            var (op, k) = OneEdit(have, want.Select(w => w.Tag).ToList());
            if (op == -1) cb.Items.RemoveAt(k);
            else if (op == 1) cb.Items.Insert(k, BurdenItem(want[k].Tag, want[k].It));
            else if (op == 2) { cb.Items.Clear(); foreach (var (tag, it) in want) cb.Items.Add(BurdenItem(tag, it)); }
            LoBurdenSelect(i);
        }
        _loFilling = was;
    }
}
