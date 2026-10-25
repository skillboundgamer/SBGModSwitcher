using System.Windows;
using System.Windows.Controls;

namespace ModSwitcher2;

/// <summary>
/// "Use Penalty of Breaching" on the Kratos loadout card. After the Valhalla door the game puts Penalty of Breaching into one
/// Cartouche glyph slot, so with 15 chosen glyphs the 15th has no slot. Ticked = the glyph cap is 14 (cfg line "breaching = true").
/// </summary>
public partial class MainWindow
{
    CheckBox _loBreaching = null!;
    bool LoBreachingOn => _loBreaching?.IsChecked == true;
    /// <summary>How many normal glyphs fit: 14 with Penalty of Breaching, else 15.</summary>
    int LoMaxGlyphs => LoBreachingOn ? Loadout.MaxGlyphsBreaching : Loadout.MaxGlyphs;

    /// <summary>The check box under the glyph counter in the card header.</summary>
    void BuildBreachingBox(StackPanel titles)
    {
        _loBreaching = new CheckBox
        {
            Content = "Use Penalty of Breaching", FontSize = 12, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = "The game puts Penalty of Breaching into one glyph slot after the door: max 14 glyphs",
        };
        _loBreaching.Checked += (_, _) => LoBreachingChanged();
        _loBreaching.Unchecked += (_, _) => LoBreachingChanged();
        titles.Children.Add(_loBreaching);
    }

    void LoBreachingChanged()
    {
        if (_loFilling) return;
        if (LoBreachingOn && _loGlyphs.Count > Loadout.MaxGlyphsBreaching)
        {
            _loFilling = true;
            _loBreaching.IsChecked = false;   // refused: no slot for it while 15 glyphs are chosen
            _loFilling = false;
            ShowToast("Remove one glyph first: max 14 with Penalty of Breaching", "Warn");
            return;
        }
        LoUpdateState();
    }
}
