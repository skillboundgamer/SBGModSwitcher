using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ModSwitcher2;

/// <summary>
/// The glyph rows of the Kratos loadout card. FillLoList builds them once per item list / file load; a glyph click or a
/// burden pick changes only the rows it touches, the search box and the kind pills only hide rows (a full rebuild of the
/// 155 rows cost ~170 ms per click once a drop-down had opened: research/burdens/switcher2_map.md).
/// </summary>
public partial class MainWindow
{
    /// <summary>The parts of one glyph row that a click changes: the row button (tooltip), its mark icon, its label.</summary>
    sealed record LoRow(LoadoutItem Item, Button Button, System.Windows.Shapes.Path Mark, TextBlock Label);

    /// <summary>
    /// The glyph list: an ItemsControl over the row elements with a VirtualizingStackPanel in a ScrollViewer (pixel scrolling,
    /// no horizontal bar, as the old StackPanel in a ScrollViewer). All rows exist (in-place updates, LoFilter), but only the
    /// rows in view are in the visual tree: a pill that shows 75 more rows lays out ~15 (with UI Automation on, every laid-out
    /// row is costly).
    /// </summary>
    static ItemsControl LoNewList()
    {
        var sv = new FrameworkElementFactory(typeof(ScrollViewer));
        sv.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        sv.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        sv.SetValue(ScrollViewer.CanContentScrollProperty, true);
        sv.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        sv.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter)));
        var list = new ItemsControl
        {
            Template = new ControlTemplate(typeof(ItemsControl)) { VisualTree = sv },
            ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
            Focusable = false,
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Standard);   // the items are the row elements: nothing to recycle
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        return list;
    }

    /// <summary>Glyph name -> its row(s) in the list (filled by FillLoList).</summary>
    readonly Dictionary<string, List<LoRow>> _loRows = new();
    TextBlock _loEmpty = null!;   // the last child of the list: "No glyph matches" / the item list note
    List<LoadoutItem>? _loRowsItems;   // the item list the rows were built from (another list = rebuild)
    /// <summary>The rows were built from the current item list. Not yet with no game folder (the file is never read): then the
    /// first search key / pill / burden pick builds them, as the full rebuild did before.</summary>
    bool LoRowsBuilt => ReferenceEquals(_loRowsItems, _loItems);

    /// <summary>Every row from the chosen lists and the burden boxes; LoSetRow touches only what changed (a reload of the file).</summary>
    void LoUpdateAllRows()
    {
        foreach (var rows in _loRows.Values)
            foreach (var r in rows) LoSetRow(r);
    }

    /// <summary>The card shows exactly the file just read: nothing unsaved and each burden box in the file's order (a reload would change nothing).</summary>
    bool LoShowsSaved() =>
        !LoDirty && _loSwitch.IsChecked == _loSaved.On
        && _loBurdenBox.Select((n, i) => (n ?? "") == (i < _loSaved.Burdens.Count ? _loSaved.Burdens[i] : "")).All(x => x);

    /// <summary>The search box and the kind pill: which rows show (Visibility only, the rows stay built).</summary>
    void LoFilter()
    {
        if (!LoRowsBuilt) { FillLoList(); return; }   // FillLoList ends with LoFilter
        var q = _loSearch.Text.Trim();
        bool any = false;
        foreach (var rows in _loRows.Values)
            foreach (var r in rows)
            {
                var it = r.Item;
                bool show = (_loKind.Length == 0 || it.Kind == _loKind)
                            && (q.Length == 0 || it.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || it.Desc.Contains(q, StringComparison.OrdinalIgnoreCase));
                var v = show ? Visibility.Visible : Visibility.Collapsed;
                if (r.Button.Visibility != v) r.Button.Visibility = v;
                any |= show;
            }
        var text = _loItems.Count == 0 ? _itemsNote ?? "Building the item list from your game files…" : "No glyph matches";
        if (_loEmpty.Text != text) _loEmpty.Text = text;
        var fg = Brush(_itemsNote != null && _loItems.Count == 0 ? "Warn" : "Dim");
        if (!ReferenceEquals(_loEmpty.Foreground, fg)) _loEmpty.Foreground = fg;
        var ev = any ? Visibility.Collapsed : Visibility.Visible;
        if (_loEmpty.Visibility != ev) _loEmpty.Visibility = ev;
    }

    void LoAddRow(LoRow r)
    {
        if (!_loRows.TryGetValue(r.Item.Name, out var rows)) _loRows[r.Item.Name] = rows = new List<LoRow>();
        rows.Add(r);
        LoSetRow(r);
    }

    /// <summary>Mark, label colour and tooltip of one row, from the chosen lists and the burden boxes.</summary>
    void LoSetRow(LoRow r)
    {
        var it = r.Item;
        bool chosen = _loGlyphs.Contains(it.Name) || _loStats.Contains(it.Name);
        var data = (Geometry)FindResource(chosen ? "IcCheck" : "IcPlus");
        if (!ReferenceEquals(r.Mark.Data, data)) r.Mark.Data = data;
        var stroke = Brush(chosen ? "Ok" : "Dim");
        if (!ReferenceEquals(r.Mark.Stroke, stroke)) r.Mark.Stroke = stroke;
        var fg = chosen ? Brush("Acc") : Brush("Txt");
        if (!ReferenceEquals(r.Label.Foreground, fg)) r.Label.Foreground = fg;
        var tip = (it.Desc.Length > 0 ? it.Desc + "\n" : "")
                  + (chosen ? "Click to take it out" : _loBurdenBox.Contains(it.Name) ? "In the burden glyphs: take it out there first" : "Click to add");
        if (!Equals(r.Button.ToolTip, tip)) r.Button.ToolTip = tip;
    }

    /// <summary>Only the rows of these glyphs (a glyph click, a burden pick). A name without a row is skipped.</summary>
    void LoUpdateRows(params string?[] names)
    {
        if (!LoRowsBuilt) { FillLoList(); return; }
        foreach (var n in names)
            if (!string.IsNullOrEmpty(n) && _loRows.TryGetValue(n, out var rows))
                foreach (var r in rows) LoSetRow(r);
    }

    /// <summary>After one glyph went in or out: its row, the burden boxes (not for Stat glyphs: never in them), chips, counter.</summary>
    void LoGlyphChanged(string name, bool burdenBoxes = true)
    {
        if (burdenBoxes) LoBurdenSync();   // one item in or out of each box (LoadoutBurdens.cs)
        LoUpdateRows(name);
        LoChosenSync();
        LoUpdateState();
    }

    /// <summary>Add or remove only the chip that changed; the "No glyphs chosen" text in or out, or more, = FillLoChosen.</summary>
    void LoChosenSync()
    {
        var want = _loGlyphs.Concat(_loStats).ToList();
        var chips = _loChosen.Children.OfType<Button>().ToList();
        if (want.Count == 0 || chips.Count == 0 || chips.Count != _loChosen.Children.Count) { FillLoChosen(); return; }
        var (op, k) = OneEdit(chips.Select(c => (string)c.Tag).ToList(), want);
        if (op == -1) _loChosen.Children.RemoveAt(k);
        else if (op == 1) _loChosen.Children.Insert(k, LoChip(want[k]));
        else if (op == 2) FillLoChosen();
    }

    /// <summary>have -> want in one step? (0 = same, -1 = remove have[At], 1 = insert want[At] at At, 2 = more than one change)</summary>
    static (int Op, int At) OneEdit(IReadOnlyList<string> have, IReadOnlyList<string> want)
    {
        int n = Math.Min(have.Count, want.Count), k = 0;
        while (k < n && have[k] == want[k]) k++;
        if (have.Count == want.Count) return k == n ? (0, 0) : (2, 0);
        if (have.Count == want.Count + 1)
        {
            for (int j = k; j < want.Count; j++) if (have[j + 1] != want[j]) return (2, 0);
            return (-1, k);
        }
        if (want.Count == have.Count + 1)
        {
            for (int j = k; j < have.Count; j++) if (want[j + 1] != have[j]) return (2, 0);
            return (1, k);
        }
        return (2, 0);
    }
}
