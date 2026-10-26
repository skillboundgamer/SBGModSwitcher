using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace ModSwitcher2;

/// <summary>
/// Mod packs on the Overview page (Packs.cs does the file work): the installed packs, the "Install mod" drop zone, the
/// install / remove overlay with its steps, progress bar and ETA, and the glow while a zip is dragged over the window.
/// Pack mode = no mods.json of the app's own (the release app) or env SBG_PACKS_ONLY=1 (development tests).
/// Speed: a click only shows the overlay; checking and unpacking run on the thread pool; an install adds only the new
/// pack's cards and row, a remove takes only its own away (never a rebuild of the tabs).
/// </summary>
public partial class MainWindow
{
    bool _packMode;
    List<InstalledPack> _packs = new();
    bool _packBusy, _loadoutBuilt;
    StackPanel? _packRows;
    TextBlock? _packHeader;
    readonly Dictionary<string, Border> _packRowById = new();
    Border? _loadoutRoot, _itemsCard;   // built once; hidden while no installed pack has a loadout

    /// <summary>The Kratos loadout card and the item list card shown or hidden (never rebuilt).</summary>
    void ShowLoadoutCards(bool on)
    {
        var v = on ? Visibility.Visible : Visibility.Collapsed;
        if (_loadoutRoot != null) _loadoutRoot.Visibility = v;
        if (_itemsCard != null) _itemsCard.Visibility = v;
    }

    // ---------- load ----------

    void LoadPacks()
    {
        _packMode = DataRoot.Find() == null || Environment.GetEnvironmentVariable("SBG_PACKS_ONLY") == "1";
        if (!_packMode) return;
        var errs = new List<string>();
        try { _packs = Packs.List(errs); }
        catch (Exception ex) { errs.Add(ex.Message); }
        if (errs.Count > 0) _startError = "a mod pack could not be read: " + string.Join("; ", errs);
    }

    /// <summary>All packs as one edition: their groups and tabs, the first community links. No pack = Overview only.</summary>
    Edition MergedPacksEdition()
    {
        var ed = new Edition { Id = "packs", Title = "SBG Mod Switcher" };
        foreach (var p in _packs) MergeIntoEdition(ed, p.Info);
        return ed;
    }

    static void MergeIntoEdition(Edition ed, Edition p)
    {
        ed.Groups.AddRange(p.Groups.Where(g => !ed.Groups.Contains(g)));
        ed.Tabs.AddRange(p.Tabs.Where(t => !ed.ShowsTab(t)));
        ed.Loadout |= p.Loadout;
        if (ed.Discord.Length == 0) ed.Discord = Edition.SafeUrl(p.Discord);
        if (ed.Nexus.Length == 0) ed.Nexus = Edition.SafeUrl(p.Nexus);
    }

    /// <summary>A pack's groups into the catalog (Root and PackId set); a group id that is already there is skipped.</summary>
    List<ModGroup> AddPackGroups(InstalledPack p) => AddPackGroups(p, LoadPackCatalog(p));

    List<ModGroup> AddPackGroups(InstalledPack p, ModCatalog? cat)
    {
        var added = new List<ModGroup>();
        if (cat == null || _catalog == null) return added;
        foreach (var g in cat.Groups)
        {
            if (_catalog.Groups.Any(x => x.Id == g.Id)) { Log($"{p.Info.Title}: \"{g.Name}\" is already shown by another pack, skipped", "Warn"); continue; }
            _catalog.Groups.Add(g);
            added.Add(g);
        }
        _catalog.Rules.AddRange(cat.Rules);
        return added;
    }

    /// <summary>The pack's mods.json (file work: off the UI thread for an install); null + a log line when broken.</summary>
    ModCatalog? LoadPackCatalog(InstalledPack p)
    {
        try
        {
            var cat = PackManifest.FromFolder(p.Root, PackManifest.AppVersion).Catalog;   // checked at install too; again for a folder changed by hand
            foreach (var g in cat.Groups) { g.Root = p.Root; g.PackId = p.Info.Id; }
            return cat;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or PackError)
        {
            _startError = $"{p.Info.Title}: {ex.Message}";
            return null;
        }
    }

    void BuildLoadoutOnce()
    {
        if (_loadoutBuilt || _dataRoot == null) return;
        _loadoutBuilt = true;
        BuildLoadoutCard();
        BuildItemsSettingsCard();
    }

    // ---------- the Overview section ----------

    void BuildPacksSection()
    {
        _packHeader = new TextBlock { FontSize = 12, Foreground = Brush("Dim"), Margin = new Thickness(0, 22, 0, 10), Typography = { Capitals = FontCapitals.AllSmallCaps } };
        PacksSection.Children.Add(_packHeader);
        _packRows = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
        PacksSection.Children.Add(_packRows);
        foreach (var p in _packs) _packRows.Children.Add(MakePackRow(p));
        PacksSection.Children.Add(MakeDropZone());
        UpdatePackHeader();
    }

    void UpdatePackHeader()
    {
        if (_packHeader != null) _packHeader.Text = _packs.Count == 0 ? "Mod packs" : $"Mod packs · {_packs.Count}";
        if (_packs.Count == 0 && _catalog?.Groups.Count == 0)
        {
            ActiveEmpty.Content = "No mods yet: drop a mod zip below, or press Install mod";
            ActiveEmpty.Visibility = Visibility.Visible;
        }
    }

    Border MakePackRow(InstalledPack p)
    {
        var row = new Border
        {
            Background = Brush("Card"), BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), Width = 560,
        };
        var dock = new DockPanel();
        row.Child = dock;
        var remove = new Button { Style = (Style)FindResource("Btn"), Padding = new Thickness(10, 6, 10, 6), Foreground = Brush("Bad"), ToolTip = "Turn its mods off, then delete it" };
        var rsp = new StackPanel { Orientation = Orientation.Horizontal };
        rsp.Children.Add(new Path { Style = (Style)FindResource("IconFg"), Data = (Geometry)FindResource("IcTrash"), Margin = new Thickness(0, -4, 6, -4), LayoutTransform = new ScaleTransform(0.75, 0.75) });
        rsp.Children.Add(new TextBlock { Text = "Remove" });
        remove.Content = rsp;
        remove.Click += (_, _) => _ = RemovePackAsync(p.Info.Id);
        DockPanel.SetDock(remove, Dock.Right);
        dock.Children.Add(remove);
        dock.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Child = IconPath("IcShield", "Acc", 1) });
        var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = p.Info.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        int n = p.Info.Groups.Count;
        text.Children.Add(new TextBlock { Text = $"v{p.Info.Version} · {n} mod{(n == 1 ? "" : "s")}", FontSize = 12, Foreground = Brush("Dim") });
        dock.Children.Add(text);
        _packRowById[p.Info.Id] = row;
        return row;
    }

    Border MakeDropZone()
    {
        var zone = new Border
        {
            Width = 560, Height = 88, CornerRadius = new CornerRadius(14), Background = Brush("Well"), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Left,
            BorderBrush = Brush("LineHi"), BorderThickness = new Thickness(0), ToolTip = "Install a mod pack (.zip from Nexus)",
        };
        // dashed outline: a Rectangle, because a Border cannot draw dashes
        var grid = new Grid();
        grid.Children.Add(new Rectangle { RadiusX = 14, RadiusY = 14, Stroke = Brush("LineHi"), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } });
        var sp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Background = Brush("AccBg"), Child = IconPath("IcPlus", "Acc", 1) });
        var t = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(new TextBlock { Text = "Install mod", FontWeight = FontWeights.SemiBold, Foreground = Brush("Acc") });
        t.Children.Add(new TextBlock { Text = "Drop a mod zip here, or click to choose one", FontSize = 12, Foreground = Brush("Dim") });
        sp.Children.Add(t);
        grid.Children.Add(sp);
        zone.Child = grid;
        zone.MouseEnter += (_, _) => zone.Background = Brush("Card");
        zone.MouseLeave += (_, _) => zone.Background = Brush("Well");
        zone.MouseLeftButtonUp += (_, _) => PickZip();
        return zone;
    }

    void PickZip()
    {
        if (_packBusy) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Mod pack (*.zip)|*.zip", Title = "Install a mod pack" };
        if (dlg.ShowDialog(this) == true) _ = InstallPackAsync(dlg.FileName);
    }

    // ---------- drag and drop ----------

    static string? DroppedZip(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } f
        && f[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? f[0] : null;

    void Pack_DragEnter(object sender, DragEventArgs e)
    {
        Pack_DragOver(sender, e);
        if (DroppedZip(e) != null && _packMode && !_packBusy) ShowDropGlow(true);
    }

    void Pack_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedZip(e) != null && _packMode && !_packBusy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Pack_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when the pointer moves onto a child: hide only when it really left the window
        var p = e.GetPosition(this);
        if (p.X <= 0 || p.Y <= 0 || p.X >= ActualWidth || p.Y >= ActualHeight) ShowDropGlow(false);
    }

    void Pack_Drop(object sender, DragEventArgs e)
    {
        ShowDropGlow(false);
        var zip = DroppedZip(e);
        if (zip != null && _packMode && GuideAllowsDrop()) _ = InstallPackAsync(zip);
    }

    void ShowDropGlow(bool on)
    {
        if (!on) { DropGlow.Visibility = Visibility.Collapsed; return; }
        if (DropGlow.Children.Count == 0)
        {
            var glow = new Border
            {
                Margin = new Thickness(14), CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(2),
                BorderBrush = Brush("Acc"), Background = new SolidColorBrush(Color.FromArgb(0x38, 0x0D, 0x22, 0x38)),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Color.FromRgb(0x5F, 0xB4, 0xFF), BlurRadius = 40, ShadowDepth = 0, Opacity = 0.9 },
            };
            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new Border { Style = (Style)FindResource("IcoBox"), Width = 72, Height = 72, CornerRadius = new CornerRadius(20), Background = Brush("AccBg"), Child = IconPath("IcPlus", "Acc", 1.6) });
            sp.Children.Add(new TextBlock { Text = "Drop to install", FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4) });
            sp.Children.Add(new TextBlock { Text = "The zip is checked before anything is written", Foreground = Brush("Dim"), HorizontalAlignment = HorizontalAlignment.Center });
            glow.Child = sp;
            DropGlow.Children.Add(glow);
            // a slow breathing glow while the zip is held over the window
            var pulse = new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(900)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            glow.Effect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, pulse);
        }
        DropGlow.Visibility = Visibility.Visible;
        DropGlow.Opacity = 0;
        DropGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    // ---------- install ----------

    async Task InstallPackAsync(string zip)
    {
        if (_packBusy || _catalog == null) return;
        _packBusy = true;
        var name = System.IO.Path.GetFileName(zip);
        var sw = Stopwatch.StartNew();
        var p = OpenPackPanel("Installing " + name, "Checking it is a safe mod pack", new[] { "Check the zip", "Unpack", "Turn the old version off", "Put it in place", "Add the mod cards" });
        PackPerf("install click: overlay open", sw);
        InstalledPack? old = null, oldOff = null;   // the installed version; set once its mods were turned off and its cards taken away
        string? shownId = null;                     // set once the new pack's cards were (partly) added
        var inst = new PackInstaller
        {
            Taken = _catalog.Groups.Where(g => g.PackId.Length > 0).ToDictionary(g => g.Id, g => (g.PackId, _packs.FirstOrDefault(x => x.Info.Id == g.PackId)?.Info.Title ?? g.PackId)),
            UnpackProgress = new Progress<double>(f => PackProgress(p, 0.2 + 0.3 * f)),
            Step = (i, frac) => PackStep(p, i, frac),
            Confirm = async man =>
            {
                p.Title.Text = "Installing " + man.Info.Title;
                p.Sub.Text = $"Version {man.Info.Version} · {man.Info.Groups.Count} mod{(man.Info.Groups.Count == 1 ? "" : "s")}";
                old = _packs.FirstOrDefault(x => x.Info.Id == man.Info.Id);
                if (old == null) ((FrameworkElement)p.Rows[2].Text.Parent).Visibility = Visibility.Collapsed;   // a fresh install has no old version
                else p.Rows[2].Text.Text = $"Turn version {old.Info.Version} off";
                var sw2 = Stopwatch.StartNew();
                var ask = PackPreviewAsync(p, man.GameFiles, old != null);
                PackPerf("install: preview shown", sw2);
                return await ask;
            },
            TurnOldOff = async man =>
            {
                if (old == null) return true;
                if (!await TurnPackOffAsync(old)) { RefreshAfterPackChange(); return false; }
                RemovePackCards(old.Info.Id, animate: true);
                oldOff = old;
                await DemoPause();
                return true;
            },
            Show = async (pack, man) =>
            {
                var cat = await Task.Run(() => LoadPackCatalog(pack));
                if (cat == null) throw new PackError(_startError ?? "mods.json could not be read");
                sw.Restart();
                shownId = pack.Info.Id;
                AddInstalledPack(pack, cat, old != null);
                UpdatePendingUi();
                PackPerf("install: new cards + row added", sw);
            },
        };
        try
        {
            await DemoPause();
            var pack = await inst.InstallAsync(zip, PackManifest.AppVersion);
            if (pack == null) { ClosePackPanel(); return; }
            var info = pack.Info;
            Log($"installed {info.Title} {info.Version} ({name})", "Ok");
            var first = _catalog.Groups.FirstOrDefault(g => g.PackId == info.Id);
            PackFinish(p, true, info.Title + " is installed", old != null ? $"Updated to {info.Version}. Its mods are off: turn them on in their cards." : "Turn its mods on in their cards, then Apply.",
                ("Done", false, ClosePackPanel),
                ("Show mods", true, () => { ClosePackPanel(); if (first != null) GoToCard(first); }));
            _ = RefreshModsAsync();
        }
        catch (Exception ex)   // every failure ends in the panel, never a stuck overlay; PackInstaller has undone the files
        {
            Log($"install {name} failed: {ex.Message}", "Bad");
            if (ex is not (PackError or IOException or UnauthorizedAccessException or InvalidDataException)) Log("  " + ex, "Bad");
            var msg = ex.Message;
            if (shownId != null)
            {
                // the new cards were (partly) added before the failure: the files are undone, so they go too
                RemovePackCards(shownId, animate: false);
                _packs.RemoveAll(x => x.Info.Id == shownId);
                if (_packRowById.Remove(shownId, out var r0)) _packRows?.Children.Remove(r0);
            }
            if (inst.UndoFailed && old != null)
            {
                // neither version shows until the next start finishes the undo: the old row goes too
                _packs.RemoveAll(x => x.Info.Id == old.Info.Id);
                if (_packRowById.Remove(old.Info.Id, out var r1)) _packRows?.Children.Remove(r1);
                UpdatePackHeader();
            }
            if (oldOff != null && !inst.UndoFailed && Directory.Exists(oldOff.Root))   // undo failed = neither version shows until the next start
            {
                // the update failed after the old version was turned off: it is back in its folder, show its cards again
                if (!_packs.Any(x => x.Info.Id == oldOff.Info.Id)) _packs.Add(oldOff);
                if (_packRows != null && !_packRowById.ContainsKey(oldOff.Info.Id)) _packRows.Children.Add(MakePackRow(oldOff));
                var cat = await Task.Run(() => LoadPackCatalog(oldOff));
                if (cat != null) AddGroupCards(AddPackGroups(oldOff, cat));
                msg += $" The old version ({oldOff.Info.Version}) is still installed, but its mods were turned off.";
            }
            if (shownId != null || oldOff != null) { UpdatePackHeader(); RefreshAfterPackChange(); }
            PackFinish(p, false, "Not installed", msg, ("Close", true, ClosePackPanel));
        }
    }

    /// <summary>The new pack's row and cards, faded and slid in; its tabs shown. Only the new things are built.</summary>
    void AddInstalledPack(InstalledPack pack, ModCatalog cat, bool replaced)
    {
        _packs.RemoveAll(x => x.Info.Id == pack.Info.Id);
        _packs.Add(pack);
        var added = AddPackGroups(pack, cat);
        AddGroupCards(added);
        // tabs, title links and the loadout of the merged edition
        if (_edition != null)
        {
            MergeIntoEdition(_edition, pack.Info);
            foreach (var (tab, key) in TabKeys) if (_edition.ShowsTab(key)) tab.Visibility = Visibility.Visible;
            ShowCommunity();
            if (pack.Info.Loadout && _loadoutBuilt)
            {
                // built before, hidden by a remove: shown again with its picks read from the new folder
                _dataRoot = pack.Root;
                ShowLoadoutCards(true);
            }
            else if (pack.Info.Loadout && _dataRoot == null)
            {
                _dataRoot = pack.Root;
                // the one-time build of the big loadout card (~50 ms, the same work as at app start): after the success
                // panel has drawn, so the install itself stays quick
                Dispatcher.InvokeAsync(() =>
                {
                    var sw = Stopwatch.StartNew();
                    BuildLoadoutOnce();
                    PackPerf("install: Kratos loadout card (once, deferred)", sw);
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        if (_packRows != null)
        {
            if (_packRowById.TryGetValue(pack.Info.Id, out var oldRow)) _packRows.Children.Remove(oldRow);
            var row = MakePackRow(pack);
            _packRows.Children.Add(row);
            FadeSlideIn(row, 0);
        }
        UpdatePackHeader();
        UpdateActiveChips();
    }

    void AddGroupCards(List<ModGroup> groups)
    {
        int i = 0;
        foreach (var g in groups)
        {
            var card = MakeCard(g);
            _cards[g.Id] = card;
            // mod cards stay above the Kratos loadout card, also after a remove and a new install
            var panel = TabPanel(g.Tab);
            int at = _loadoutRoot != null ? panel.Children.IndexOf(_loadoutRoot) : -1;
            if (at >= 0) panel.Children.Insert(at, card.Root); else panel.Children.Add(card.Root);
            FadeSlideIn(card.Root, 80 * i++);
        }
    }

    /// <summary>UI-thread time of one pack step (code only; the demo mode adds the time to idle): written by --pack-demo.</summary>
    readonly List<string> _packPerf = new();
    void PackPerf(string what, Stopwatch sw) => _packPerf.Add($"{what,-40} code {sw.Elapsed.TotalMilliseconds,6:0.0} ms");

    /// <summary>--pack-demo only: a short wait between steps so the screenshots can catch the progress bar.</summary>
    public static int DemoDelayMs;
    static Task DemoPause() => DemoDelayMs > 0 ? Task.Delay(DemoDelayMs) : Task.CompletedTask;

    // ---------- remove ----------

    async Task RemovePackAsync(string id)
    {
        if (_packBusy) return;
        var pack = _packs.FirstOrDefault(x => x.Info.Id == id);
        if (pack == null) return;
        _packBusy = true;
        // first a question, in the same panel
        var p = OpenPackPanel($"Remove {pack.Info.Title}?", "Its mods are turned off first, so your game files go back to vanilla. Then the pack is deleted.", new[] { "Turn its mods off", "Delete the pack" });
        p.Track.Visibility = Visibility.Collapsed;
        p.Eta.Visibility = Visibility.Collapsed;
        p.IconBox.Child = IconPath("IcTrash", "Bad", 1.2);
        p.IconBox.Background = Brush("BadBg");
        var answer = new TaskCompletionSource<bool>();
        p.Buttons.Visibility = Visibility.Visible;
        var no = new Button { Style = (Style)FindResource("Btn"), Content = "Cancel", Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(5, 0, 5, 0), IsCancel = true };
        var yes = new Button { Style = (Style)FindResource("BtnPri"), Content = "Remove", Padding = new Thickness(18, 9, 18, 9), Margin = new Thickness(5, 0, 5, 0) };
        no.Click += (_, _) => answer.TrySetResult(false);
        yes.Click += (_, _) => answer.TrySetResult(true);
        p.Buttons.Children.Add(no);
        p.Buttons.Children.Add(yes);
        if (!await answer.Task) { ClosePackPanel(); return; }

        p.Buttons.Visibility = Visibility.Collapsed;
        p.Track.Visibility = Visibility.Visible;
        p.Eta.Visibility = Visibility.Visible;
        p.Clock.Restart();
        p.IconBox.Background = Brush("AccBg");
        var spin = Spinner();
        spin.Visibility = Visibility.Visible;
        p.IconBox.Child = spin;
        p.Title.Text = "Removing " + pack.Info.Title;
        try
        {
            PackStep(p, 0, 0.05);
            bool forced = false;
            bool turnedOff = await TurnPackOffAsync(pack);
            if (!turnedOff)
            {
                forced = true;
                // the player decides: keep it (and try again later), or delete it and leave those files as they are
                Log($"remove {pack.Info.Title}: a mod could not be turned off", "Bad");
                var force = new TaskCompletionSource<bool>();
                PackFinish(p, false, "A mod could not be turned off",
                           "See the message and Activity. Keep the pack and try again, or remove it anyway: the files of that mod stay as they are now.",
                           ("Keep it", true, () => force.TrySetResult(false)), ("Remove anyway", false, () => force.TrySetResult(true)));
                if (!await force.Task) { ClosePackPanel(); return; }
                p.Buttons.Visibility = Visibility.Collapsed;
                p.Track.Visibility = Visibility.Visible;
                p.Sub.Foreground = Brush("Dim");
                p.Sub.Text = "Removing it anyway";
            }
            await RemoveLoaderIfLastAsync(pack);   // also on "Remove anyway": no pack left could remove it later
            PackStep(p, 1, 0.7);
            await Task.Run(() => Packs.Delete(id));
            var sw = Stopwatch.StartNew();
            _packs.Remove(pack);
            RemovePackCards(id, animate: true);
            if (_packRows != null && _packRowById.Remove(id, out var row)) FadeOutRemove(row, _packRows);
            if (_edition != null)
            {
                // the merged edition again from the packs left: tabs no pack uses go, the links follow
                var merged = MergedPacksEdition();
                _edition.Groups = merged.Groups; _edition.Tabs = merged.Tabs; _edition.Discord = merged.Discord; _edition.Nexus = merged.Nexus;
                foreach (var (tab, key) in TabKeys)
                    if (!_edition.ShowsTab(key)) tab.Visibility = Visibility.Collapsed;
                ShowCommunity();
            }
            if (pack.Info.Loadout && pack.Root == _dataRoot)
            {
                // the loadout card follows the packs: another loadout pack keeps it, else it is hidden (not rebuilt)
                var other = _packs.FirstOrDefault(x => x.Info.Loadout);
                _dataRoot = other?.Root;
                if (other == null) ShowLoadoutCards(false);
            }
            UpdatePackHeader();
            UpdateActiveChips();
            UpdatePendingUi();
            PackPerf("remove: cards + row taken away", sw);
            Log($"removed {pack.Info.Title} {pack.Info.Version}" + (forced ? " (a mod was left as it was)" : ""), forced ? "Warn" : "Ok");
            var sub = forced ? "The mod that could not be turned off kept its files (see Activity)." : "Its game files are vanilla again.";
            PackFinish(p, true, pack.Info.Title + " is removed", sub, ("Done", true, ClosePackPanel));
        }
        catch (Exception ex)   // every failure ends in the panel, never a stuck overlay
        {
            Log($"remove {pack.Info.Title} failed: {ex.Message}", "Bad");
            if (ex is not (PackError or IOException or UnauthorizedAccessException)) Log("  " + ex, "Bad");
            RefreshAfterPackChange();
            PackFinish(p, false, "Not removed", ex.Message, ("Close", true, ClosePackPanel));
        }
    }

    /// <summary>
    /// Every group of the pack back to its vanilla preset (the normal apply, with its backups); with "loadout", the loader
    /// chain too when no other pack needs it. False when one failed (the pack is then kept). Pending edits of it are dropped.
    /// </summary>
    async Task<bool> TurnPackOffAsync(InstalledPack pack)
    {
        if (_catalog == null) return true;
        var groups = _catalog.Groups.Where(g => g.PackId == pack.Info.Id).ToList();
        if (!GameLocator.IsGameDir(_settings.GamePath))
        {
            if (groups.Count == 0) return true;
            throw new PackError("Set the game folder first (Settings): its mods must be turned off before it can go.");
        }
        foreach (var g in groups) _pending.Remove(g.Id);
        bool ok = true;
        foreach (var g in groups)
        {
            int idx;
            await _io.WaitAsync();   // never check while a preset is being written (same as RefreshModsAsync)
            try { idx = await Task.Run(() => Matcher(g.Root).Check(g)); }
            finally { _io.Release(); }
            if (idx == g.Vanilla) continue;
            var r = await ApplyAsync(g, g.Presets[g.Vanilla]);
            ok &= r != null;
        }
        return ok;
    }

    /// <summary>Remove: the loader chain of the Kratos loadout goes too when no other pack has a loadout.</summary>
    async Task RemoveLoaderIfLastAsync(InstalledPack pack)
    {
        if (!pack.Info.Loadout || _packs.Any(x => x.Info.Id != pack.Info.Id && x.Info.Loadout) || !GameLocator.IsGameDir(_settings.GamePath)) return;
        var game = _settings.GamePath!;
        await _io.WaitAsync();
        try { foreach (var line in await Task.Run(() => Loader.Remove(game))) Log("  " + line); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log("  loader: " + ex.Message, "Bad"); }
        finally { _io.Release(); }
    }

    /// <summary>After a turn-off that stopped half-way: cards, chips and the Apply bar show what is on the disk now.</summary>
    void RefreshAfterPackChange()
    {
        UpdateActiveChips();
        UpdatePendingUi();
        _ = RefreshModsAsync();
    }

    /// <summary>Take one pack's cards, chips, rules and catalog groups away (the rest stays as it is).</summary>
    void RemovePackCards(string id, bool animate)
    {
        if (_catalog == null) return;
        var gone = _catalog.Groups.Where(g => g.PackId == id).ToList();
        foreach (var g in gone)
        {
            if (_cards.Remove(g.Id, out var c) && c.Root.Parent is Panel parent)
            {
                if (animate) FadeOutRemove(c.Root, parent); else parent.Children.Remove(c.Root);
            }
            _active.Remove(g.Id);
            _pending.Remove(g.Id);
        }
        var ids = gone.Select(g => g.Id).ToHashSet();
        _catalog.Groups.RemoveAll(g => ids.Contains(g.Id));
        _catalog.Rules.RemoveAll(r => ids.Contains(r.Edit) || ids.Contains(r.While.Split('=')[0]));
    }
}
