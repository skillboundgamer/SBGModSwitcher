namespace ModSwitcher2;

/// <summary>The Lab's UI is not part of the public source (see Lab.cs). These empty parts keep the app building.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, (uint Now, uint Orig)> _slotSize = new();
    readonly Dictionary<string, uint?> _slotTarget = new();

    void BuildLabCards() { }

    void LoadSlotsFromDisk() { }

    void SlotEdited(Lab.SlotInfo s) { }
}
