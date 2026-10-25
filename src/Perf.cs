using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ModSwitcher2;

public partial class App
{
    /// <summary>--perf OUT.txt APPDATA_DIR: an off-screen window that times itself and exits (no single-app lock: the real app may be open).</summary>
    void StartPerf(string outFile, string appData)
    {
        AppSettings.DirOverride = appData;
        ModSwitcher2.MainWindow.PerfOut = outFile;
        new MainWindow { WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = 0, ShowActivated = false, ShowInTaskbar = false }.Show();
    }
}

/// <summary>
/// Hidden timing mode (not in any help text): SBGModSwitcher.exe --perf OUT.txt APPDATA_DIR
/// APPDATA_DIR holds a settings.json whose GamePath is a sandbox game folder (an empty GoWR.exe + mod\sbg_loadout_cfg.lua).
/// The window opens off screen, times the Kratos loadout card's handlers on the UI thread (glyph click, burden pick,
/// search, kind pill, runic pick; before and after a ComboBox dropdown has opened, which attaches UI Automation),
/// writes a behaviour trace (counter, toasts, rows) to compare two builds, writes OUT.txt and exits.
/// Nothing is applied: the game folder is only read. Env PERF_REFRESH=0 skips the Activated / RefreshModsAsync timing.
/// </summary>
public partial class MainWindow
{
    public static string? PerfOut;
    readonly List<string> _perf = new();
    readonly List<(string name, double before, double after)> _perfSum = new();

    Task Idle() => Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;

    /// <summary>One sample: the action, a forced layout, then until the dispatcher is idle (= what the player waits for).</summary>
    async Task<(double fn, double layout, double idle)> PerfOne(Action a)
    {
        await Idle();
        var sw = Stopwatch.StartNew();
        a();
        double fn = sw.Elapsed.TotalMilliseconds;
        UpdateLayout();
        double lay = sw.Elapsed.TotalMilliseconds;
        await Idle();
        return (fn, lay - fn, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>n samples of make(i); setup(i) runs untimed before each one. Returns the median to-idle time.</summary>
    async Task<double> PerfCase(string name, int n, Func<int, Action> make, Action<int>? setup = null)
    {
        var rs = new List<(double fn, double layout, double idle)>();
        for (int i = 0; i < n; i++)
        {
            if (setup != null) { setup(i); await Idle(); }
            rs.Add(await PerfOne(make(i)));
        }
        double Med(Func<(double fn, double layout, double idle), double> f) { var v = rs.Select(f).OrderBy(x => x).ToList(); return v[v.Count / 2]; }
        double med = Med(r => r.idle);
        _perf.Add($"{name,-46} median(n={n}): code {Med(r => r.fn),6:0.0} layout {Med(r => r.layout),6:0.0} to-idle {med,6:0.0} | first {rs[0].idle,6:0.0} max {rs.Max(r => r.idle),6:0.0}");
        return med;
    }

    static string PerfUia() => string.Join(",", Enum.GetValues<AutomationEvents>().Where(AutomationPeer.ListenerExists).Select(e => e.ToString()));

    static void PerfClick(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    Button PerfPill(string kind) => _loKinds.Children.OfType<Button>().First(b => (string)((Border)b.Content).Tag == kind);

    /// <summary>The visible glyph row with this label (the row layout: Button > Border > DockPanel [kind, mark, label]).</summary>
    Button PerfRow(string label) => _loList.Items.OfType<Button>().First(b => b.Visibility == Visibility.Visible && PerfRowLabel(b).Text == label);

    static TextBlock PerfRowLabel(Button b) => (TextBlock)((DockPanel)((Border)b.Content).Child).Children[2];

    string PerfBrush(Brush? b) => new[] { "Ok", "Dim", "Acc", "Txt", "Warn" }.FirstOrDefault(k => ReferenceEquals(Brush(k), b)) ?? b?.ToString() ?? "null";

    /// <summary>Every visible row of the glyph list as text: label, mark icon + colour, label colour, tooltip.</summary>
    List<string> PerfSnapshot()
    {
        var o = new List<string>();
        foreach (UIElement e in _loList.Items)
        {
            if (e.Visibility != Visibility.Visible) continue;
            if (e is TextBlock t) { o.Add("TEXT " + t.Text + " " + PerfBrush(t.Foreground)); continue; }
            var b = (Button)e;
            var row = (DockPanel)((Border)b.Content).Child;
            var mark = (System.Windows.Shapes.Path)row.Children[1];
            string icon = ReferenceEquals(mark.Data, FindResource("IcCheck")) ? "check" : ReferenceEquals(mark.Data, FindResource("IcPlus")) ? "plus" : "?";
            o.Add($"{PerfRowLabel(b).Text} | {((TextBlock)row.Children[0]).Text} | {icon} {PerfBrush(mark.Stroke)} | {PerfBrush(PerfRowLabel(b).Foreground)} | {b.ToolTip}".Replace("\n", " / "));
        }
        return o;
    }

    /// <summary>How many rows are laid out (in the visual tree, with a height) and a PNG of the list beside OUT.txt.</summary>
    void PerfView(string tag)
    {
        var inView = _loList.Items.OfType<FrameworkElement>().Where(e => e.Visibility == Visibility.Visible && VisualTreeHelper.GetParent(e) != null && e.ActualHeight > 0).ToList();
        _perf.Add($"  in view ({tag}): {inView.Count} rows laid out, first '{(inView.FirstOrDefault() is Button b ? PerfRowLabel(b).Text : (inView.FirstOrDefault() as TextBlock)?.Text)}'");
        try
        {
            var host = (FrameworkElement)VisualTreeHelper.GetParent(_loList);
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth, (int)host.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();   // through a brush: the list at (0, 0), not at its place in the window
            using (var dc = dv.RenderOpen()) dc.DrawRectangle(new VisualBrush(host), null, new Rect(0, 0, host.ActualWidth, host.ActualHeight));
            bmp.Render(dv);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var f = File.Create(PerfOut + "." + tag + ".png");
            enc.Save(f);
        }
        catch (Exception ex) { _perf.Add("    png: " + ex.Message); }
    }

    async Task PerfStep(string step, Action a)
    {
        ToastText.Text = "";
        a();
        await Idle();
        var snap = PerfSnapshot();
        _perf.Add($"  {step,-34} | {_loCount.Text} | toast: {ToastText.Text} | rows {snap.Count} | chips {_loChosen.Children.Count} | "
                  + $"burden boxes {string.Join("/", _loBurdenCombos.Select(c => $"{c.Items.Count}:{(c.SelectedItem as ComboBoxItem)?.Tag}"))} | pending {PendingCount}");
    }

    /// <summary>The same rows as a full rebuild would make? (in-place updates vs FillLoList)</summary>
    void PerfSame(string when, bool side = true)
    {
        int rows = PerfSnapshot().Count;
        var a = PerfSnapshot().Concat(side ? PerfSideSnapshot() : new()).ToList();
        if (side) { FillBurdenCombos(); FillLoChosen(); }
        FillLoList();
        var b = PerfSnapshot().Concat(side ? PerfSideSnapshot() : new()).ToList();
        var diff = a.Zip(b).Where(p => p.First != p.Second).Take(5).Select(p => $"\n      had  {p.First}\n      want {p.Second}");
        _perf.Add($"  same as a rebuild ({when}): " + (a.SequenceEqual(b) ? $"OK ({rows} rows{(side ? ", boxes + chips" : "")})" : $"FAIL ({a.Count} vs {b.Count} lines)" + string.Concat(diff)));
    }

    /// <summary>The 3 burden boxes (pick, every item) and the chips as text.</summary>
    List<string> PerfSideSnapshot()
    {
        var o = new List<string>();
        for (int i = 0; i < _loBurdenCombos.Length; i++)
        {
            var cb = _loBurdenCombos[i];
            o.Add($"box{i} pick {(cb.SelectedItem as ComboBoxItem)?.Tag} index {cb.SelectedIndex}");
            foreach (ComboBoxItem x in cb.Items) o.Add($"box{i} {x.Tag} | {x.Content} | {x.ToolTip}");
        }
        foreach (UIElement e in _loChosen.Children)
            o.Add(e is Button b ? $"chip {b.Content} | {b.ToolTip}" : $"chip text {((TextBlock)e).Text}");
        return o;
    }

    /// <summary>Back to the file on disk, no search, kind All (untimed, between cases).</summary>
    async Task PerfReset()
    {
        _loSearch.Text = "";
        if (_loKind != "") PerfClick(PerfPill(""));
        LoadLoadoutFromDisk();
        ToastText.Text = "";
        await Idle();
    }

    async Task PerfTimings(string phase)
    {
        _perf.Add($"--- {phase} (UIA listeners: {(PerfUia() is { Length: > 0 } u ? u : "none")})");
        var g = _loItems.First(i => i.Name == _loGlyphs[0]);
        var stat = _loItems.First(i => i.Kind == "Stat" && !_loStats.Contains(i.Name));
        var slot = _loCombos.Values.First();
        var bc = _loBurdenCombos[1];
        var res = new List<(string, double)>();
        await PerfReset();
        res.Add(("glyph click (out / in)", await PerfCase("glyph click (out / in)", 12, i => () => PerfClick(PerfRow(g.Label)))));
        await PerfReset();
        res.Add(("stat glyph click (in / out)", await PerfCase("stat glyph click (in / out)", 8, i => () => PerfClick(PerfRow(stat.Label)))));
        await PerfReset();
        res.Add(("burden pick", await PerfCase("burden pick (box 2: a glyph / None)", 12, i => () => bc.SelectedIndex = i % 2 == 0 ? 2 : 0)));
        await PerfReset();
        res.Add(("search first char", await PerfCase("search: first char ('' -> 's')", 7, i => () => _loSearch.Text = "s", i => _loSearch.Text = "")));
        res.Add(("search next char", await PerfCase("search: next char ('s' -> 'sh')", 7, i => () => _loSearch.Text = "sh", i => _loSearch.Text = "s")));
        res.Add(("search clear", await PerfCase("search: clear ('s' -> '')", 7, i => () => _loSearch.Text = "", i => _loSearch.Text = "s")));
        await PerfReset();
        res.Add(("kind pill", await PerfCase("kind pill (Perk / All)", 8, i => () => PerfClick(PerfPill(i % 2 == 0 ? "Perk" : "")))));
        await PerfReset();
        res.Add(("runic pick", await PerfCase("runic / relic pick", 12, i => () => slot.SelectedIndex = 1 + i % 3)));
        await PerfReset();
        foreach (var (n, ms) in res)
        {
            int k = _perfSum.FindIndex(s => s.name == n);
            if (k < 0) _perfSum.Add((n, ms, double.NaN)); else _perfSum[k] = (n, _perfSum[k].before, ms);
        }
    }

    /// <summary>A fixed list of clicks; each line shows what the player would see. Compare this block between two builds.</summary>
    async Task PerfTrace()
    {
        _perf.Add("--- behaviour trace");
        await PerfReset();
        var g = _loItems.First(i => i.Name == _loGlyphs[0]);
        var free = _loItems.Where(i => i.Kind == "Perk" && !_loGlyphs.Contains(i.Name) && !_loBurdenBox.Contains(i.Name)).ToList();
        var burden = _loItems.First(i => i.Name == _loBurdenBox[0]);
        var burden2 = _loItems.First(i => i.Name == _loBurdenBox[1]);
        var stat = _loItems.First(i => i.Kind == "Stat");
        await PerfStep("start", () => { });
        PerfView("start");
        await PerfStep("click chosen glyph (out)", () => PerfClick(PerfRow(g.Label)));
        await PerfStep("breaching on", () => _loBreaching.IsChecked = true);
        await PerfStep("click free glyph (cap 14)", () => PerfClick(PerfRow(free[0].Label)));
        await PerfStep("breaching off", () => _loBreaching.IsChecked = false);
        await PerfStep("click free glyph (in)", () => PerfClick(PerfRow(free[0].Label)));
        await PerfStep("breaching on (15 chosen)", () => _loBreaching.IsChecked = true);
        await PerfStep("click free glyph 2 (cap 15)", () => PerfClick(PerfRow(free[1].Label)));
        await PerfStep("click burden glyph (refused)", () => PerfClick(PerfRow(burden.Label)));
        PerfSame("after glyph clicks");
        await PerfStep("burden box 2 -> None", () => _loBurdenCombos[1].SelectedIndex = 0);
        await PerfStep("click free glyph (out)", () => PerfClick(PerfRow(free[0].Label)));
        await PerfStep("click old burden glyph (in)", () => PerfClick(PerfRow(burden2.Label)));
        await PerfStep("burden box 2 -> item 3", () => _loBurdenCombos[1].SelectedIndex = 3);
        await PerfStep("burden box 3 -> None", () => _loBurdenCombos[2].SelectedIndex = 0);
        PerfSame("after burden picks");
        await PerfStep("search 's'", () => _loSearch.Text = "s");
        await PerfStep("search 'sh'", () => _loSearch.Text = "sh");
        await PerfStep("kind Perk", () => PerfClick(PerfPill("Perk")));
        PerfView("sh-perk");
        await PerfStep("click first visible row", () => PerfClick(_loList.Items.OfType<Button>().First(b => b.Visibility == Visibility.Visible)));
        PerfSame("search 'sh' + kind Perk");
        await PerfStep("kind Stat", () => PerfClick(PerfPill("Stat")));
        await PerfStep("search ''", () => _loSearch.Text = "");
        await PerfStep("click a stat glyph (in)", () => PerfClick(PerfRow(stat.Label)));
        PerfSame("stat glyph in");
        await PerfStep("search 'zzz' (no match)", () => _loSearch.Text = "zzz");
        PerfView("nomatch");
        PerfSame("no match");
        await PerfStep("search ''", () => _loSearch.Text = "");
        await PerfStep("kind All", () => PerfClick(PerfPill("")));
        await PerfStep("chip 1 click (out)", () => PerfClick(_loChosen.Children.OfType<Button>().First()));
        PerfSame("chip click");
        await PerfStep("click free glyph 2 (in, before the stat chip)", () => PerfClick(PerfRow(free[1].Label)));
        await PerfStep("click chosen glyph (out, middle)", () => PerfClick(PerfRow(_loItems.First(i => i.Name == _loGlyphs[5]).Label)));
        PerfSame("glyph in + out with a stat chosen");
        await PerfStep("take all glyphs out", () => { foreach (var n in _loGlyphs.Concat(_loStats).ToList()) PerfClick(PerfRow(_loItems.First(i => i.Name == n).Label)); });
        PerfSame("no glyph chosen");
        await PerfStep("click free glyph (first chip)", () => PerfClick(PerfRow(free[0].Label)));
        PerfSame("first chip");
        await PerfStep("search 'e', reload item list", () => { _loSearch.Text = "e"; ReloadItems(); });
        PerfSame("after item list reload");
        await PerfStep("search '', disk reload (dirty: kept)", () => { _loSearch.Text = ""; if (!LoDirty) LoadLoadoutFromDisk(); });
        await PerfStep("discard (LoDropEdits)", () => LoDropEdits());
        PerfSame("after discard");
        await PerfStep("runic slot 1 -> item 2", () => _loCombos.Values.First().SelectedIndex = 2);
        await PerfStep("discard (LoDropEdits)", () => LoDropEdits());
    }

    /// <summary>No game folder set: the file is never read, the rows are built by the first key / pill (no crash, empty-list text).</summary>
    async Task PerfNoGame()
    {
        _perf.Add("--- behaviour trace, no game folder");
        await PerfStep("start", () => { });
        await PerfStep("search 's'", () => _loSearch.Text = "s");
        await PerfStep("kind Perk", () => PerfClick(PerfPill("Perk")));
        PerfSame("no game folder, search 's' + kind Perk", side: false);   // rows only: the chip panel stays empty until a glyph click (as before)
        await PerfStep("search 'zzz' (no match)", () => _loSearch.Text = "zzz");
        PerfSame("no game folder, no match", side: false);
        await PerfStep("search ''", () => _loSearch.Text = "");
        await PerfStep("kind All", () => PerfClick(PerfPill("")));
        await PerfStep("burden box 1 -> item 1", () => _loBurdenCombos[0].SelectedIndex = 1);
        await PerfStep("click first row (burden: refused)", () => PerfClick(_loList.Items.OfType<Button>().First()));
        await PerfStep("click a free row (in)", () => PerfClick(PerfRow(_loItems.First(i => i.Kind == "Perk" && !_loBurdenBox.Contains(i.Name)).Label)));
        PerfSame("no game folder, after clicks");
    }

    /// <summary>The Activated path (MainWindow.xaml.cs: Activated -> RefreshModsAsync -> LoadLoadoutFromDisk + ReleaseMemory) and the click after it.</summary>
    async Task PerfRefresh()
    {
        _perf.Add("--- window re-activated (RefreshModsAsync)");
        var g = _loItems.First(i => i.Name == _loGlyphs[0]);
        // what the Activated -> RefreshModsAsync path does with the card (ModsView.cs: never over unsaved picks)
        double same = await PerfCase("  re-activated, file unchanged", 6, i => () => { if (!LoDirty) LoadLoadoutFromDisk(); });
        _perfSum.Add(("re-activated, file unchanged", double.NaN, same));
        var cfg = Path.Combine(_settings.GamePath!, "mod", "sbg_loadout_cfg.lua");
        if (new FileInfo(Path.Combine(_settings.GamePath!, "GoWR.exe")).Length == 0 && File.Exists(cfg))   // only in a sandbox game folder
        {
            var orig = File.ReadAllText(cfg);
            var other = orig.Replace($"    \"{_loGlyphs[0]}\",\n", "").Replace($"    \"{_loGlyphs[0]}\",\r\n", "");   // another app took one glyph out
            try
            {
                double changed = await PerfCase("  re-activated, file changed by another app", 6,
                                                i => () => { if (!LoDirty) LoadLoadoutFromDisk(); }, i => File.WriteAllText(cfg, i % 2 == 0 ? other : orig));
                _perfSum.Add(("re-activated, file changed", double.NaN, changed));
                PerfSame("after the file changed on disk");
                var b1 = _loBurdenBox[1];
                var free = _loItems.First(i => i.Kind == "Perk" && !_loGlyphs.Contains(i.Name) && !_loBurdenBox.Contains(i.Name)).Name;
                await PerfStep("cfg: another app changed burden 2", () => { File.WriteAllText(cfg, orig.Replace($"\"{b1}\"", $"\"{free}\"")); LoadLoadoutFromDisk(); });
                PerfSame("burden changed on disk");
                await PerfStep("cfg: back to the old burden", () => { File.WriteAllText(cfg, orig); LoadLoadoutFromDisk(); });
                PerfSame("burden back on disk");
                await PerfStep("cfg: unknown burden name", () => { File.WriteAllText(cfg, orig.Replace($"\"{b1}\"", "\"Glyph_NotInTheItemList_0\"")); LoadLoadoutFromDisk(); });
                PerfSame("unknown burden (listed last)");
                await PerfStep("cfg: unknown burden gone", () => { File.WriteAllText(cfg, orig); LoadLoadoutFromDisk(); });
                PerfSame("unknown burden removed");
                _perf.Add($"    file changed: the variant has {(other == orig ? "NO change (glyph line not found)" : "one glyph less")}");
            }
            finally { File.WriteAllText(cfg, orig); LoadLoadoutFromDisk(); await Idle(); }
        }
        else _perf.Add("  re-activated, file changed: skipped (not a sandbox game folder: GoWR.exe is not empty)");
        await PerfCase("  piece LoadLoadoutFromDisk", 5, i => () => LoadLoadoutFromDisk());
        await PerfCase("  piece ReleaseMemory (compacting GC)", 5, i => () => ReleaseMemory());
        for (int k = 0; k < 3; k++)
        {
            var stalls = new List<double>(); bool run = true;
            var probe = Task.Run(async () =>
            {
                while (run)
                {
                    var w = Stopwatch.StartNew();
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                    lock (stalls) stalls.Add(w.Elapsed.TotalMilliseconds);
                    await Task.Delay(5);
                }
            });
            var sw = Stopwatch.StartNew();
            await RefreshModsAsync();
            double tot = sw.Elapsed.TotalMilliseconds;
            await Task.Delay(200); run = false; await probe;
            var click = await PerfOne(() => PerfClick(PerfRow(g.Label)));
            PerfClick(PerfRow(g.Label));
            LoDropEdits();   // out + in moved the glyph to the end of the list (= edited): back to the file, so the next refresh reloads
            await Idle();
            _perf.Add($"  RefreshModsAsync #{k}: total {tot,6:0} ms, longest UI-thread stall {stalls.DefaultIfEmpty().Max(),6:0} ms; first glyph click after it: to-idle {click.idle,6:0.0} ms");
        }
    }

    async Task RunPerfAsync()
    {
        try
        {
            TabValhalla.IsChecked = true;
            await Idle();
            await Task.Delay(500);
            _perf.Add($"items {_loItems.Count}, glyph rows {_loList.Items.OfType<Button>().Count()}, chosen {_loGlyphs.Count}+{_loStats.Count}, burdens [{string.Join(",", _loBurdenBox)}]");
            if (!GameLocator.IsGameDir(_settings.GamePath)) { await PerfNoGame(); throw new OperationCanceledException("no game folder: trace only"); }
            await PerfTrace();
            await PerfTimings("before any dropdown opened");
            var slot = _loCombos.Values.First();
            slot.IsDropDownOpen = true; await Task.Delay(300); slot.IsDropDownOpen = false; await Task.Delay(300);
            Keyboard.ClearFocus();
            await PerfTimings("after a dropdown opened");
            if (Environment.GetEnvironmentVariable("PERF_REFRESH") != "0") await PerfRefresh();
            _perf.Add("--- summary: median to-idle ms (before / after a dropdown opened)");
            foreach (var (n, b, a) in _perfSum) _perf.Add($"  {n,-28} {b,6:0.0} / {a,6:0.0}");
        }
        catch (OperationCanceledException ex) { _perf.Add(ex.Message); }
        catch (Exception ex) { _perf.Add("ERROR " + ex); }
        File.WriteAllLines(PerfOut!, _perf);
        Application.Current.Shutdown(0);
    }
}
