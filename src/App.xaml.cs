using System.IO;
using System.Windows;

namespace ModSwitcher2;

public partial class App : Application
{
    System.Threading.Mutex? _single; // kept alive for the whole run
    /// <summary>
    /// Headless modes (no window), used by tests/test_switcher2_parity.py:
    ///   --status [--game DIR] [--data DIR]   one line per group: "id&lt;TAB&gt;active preset index (-1 = custom)&lt;TAB&gt;preset count"
    ///   --apply ID N --game DIR [--data DIR] [--recycle DIR]   apply preset N of group ID; prints what changed
    ///   --libraries FILE                    the library paths parsed from a libraryfolders.vdf, one per line
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var a = e.Args;
        if (a.Length > 2 && a[0] == "--perf") { StartPerf(a[1], a[2]); return; }   // hidden timing mode (Perf.cs)
        if (a.Length > 2 && a[0] == "--guide-demo") { StartGuideDemo(a[1], a[2]); return; }   // hidden guide screenshots (GuideDemo.cs)
        if (a.Length > 3 && a[0] == "--pack-demo") { StartPackDemo(a[1], a[2], a[3]); return; }   // hidden demo mode (PacksDemo.cs)
        if (a.Length > 0 && a[0].StartsWith("--"))
        {
            int code;
            try { code = RunHeadless(a, Console.Out); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); code = 1; }
            Console.Out.Flush();
            Shutdown(code);
            return;
        }
        // one window at a time: a second start brings the first one to the front (two apps would write the same files)
        _single = new System.Threading.Mutex(true, "SBGModSwitcher2.single", out bool first);
        if (!first)
        {
            ModSwitcher2.MainWindow.FocusOtherInstance();
            Shutdown(0);
            return;
        }

        // last resort: a bug in a click handler shows a message and is logged instead of closing the app
        DispatcherUnhandledException += (_, ev) =>
        {
            try
            {
                Directory.CreateDirectory(AppSettings.Dir);
                File.AppendAllText(Path.Combine(AppSettings.Dir, "activity.log"),
                                   $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  UNEXPECTED ERROR: {ev.Exception}{Environment.NewLine}");
            }
            catch (Exception) { }
            MessageBox.Show(ev.Exception.Message + "\n\nNothing more was written. Details are in activity.log (Settings → Settings folder).",
                            "Mod Switcher: unexpected error", MessageBoxButton.OK, MessageBoxImage.Warning);
            ev.Handled = true;
        };
        new MainWindow().Show();
    }

    static int RunHeadless(string[] a, TextWriter o)
    {
        string? Opt(string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
        if (Opt("--appdata") is { } appdata) AppSettings.DirOverride = appdata;   // tests: backups / lab copies in a sandbox

        switch (a[0])
        {
            case "--libraries":
                foreach (var p in GameLocator.ParseLibraryPaths(File.ReadAllText(a[1]))) o.WriteLine(p);
                return 0;
            case "--status":
            case "--apply":
                var game = Opt("--game") ?? AppSettings.Load().GamePath ?? GameLocator.Detect()
                           ?? throw new DirectoryNotFoundException("game folder not found");
                var data = Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("mods.json not found");
                var cat = ModCatalog.Load(DataRoot.CatalogPath(data), Edition.Load(data));
                var m = new ModMatcher(game, data);
                if (a[0] == "--status")
                {
                    foreach (var g in cat.Groups) o.WriteLine($"{g.Id}\t{m.Check(g)}\t{g.Presets.Count}");
                    return 0;
                }
                // --apply <group id> <preset index> --game D [--data D] [--recycle D]
                // --game is required: a headless apply never picks the real game folder by itself (installs go
                // through tools/install.py; this mode is for sandbox tests).
                if (Opt("--game") == null) throw new ArgumentException("--apply needs --game <folder>");
                var grp = cat.Groups.FirstOrDefault(g => g.Id == a[1]) ?? throw new ArgumentException("no group " + a[1]);
                var r = new ModApplier(game, data, Opt("--recycle") ?? ModApplier.DefaultRecycle, m).Apply(grp.Presets[int.Parse(a[2])]);
                foreach (var line in r.Log) o.WriteLine(line);
                o.WriteLine(r.NoChange ? "no change" : r.FullRestart ? "restart: full" : "restart: " + grp.Reload);
                return 0;
            case "--loadout-read":
            {
                // --loadout-read --game D : what mod\sbg_loadout_cfg.lua holds, one "key<TAB>value" line each
                var lo = Loadout.Read(Opt("--game") ?? throw new ArgumentException("--loadout-read needs --game <folder>"));
                o.WriteLine($"on\t{(lo.On ? "true" : "false")}");
                o.WriteLine($"rev\t{lo.Rev}");
                o.WriteLine($"glyphs\t{string.Join(",", lo.Glyphs)}");
                o.WriteLine($"stats\t{string.Join(",", lo.Stats)}");
                o.WriteLine($"drop\t{string.Join(",", lo.Drop)}");
                foreach (var kv in lo.Slots.OrderBy(k => k.Key, StringComparer.Ordinal)) o.WriteLine($"slot\t{kv.Key}\t{kv.Value}");
                if (lo.Burdens.Count > 0) o.WriteLine($"burdens\t{string.Join(",", lo.Burdens)}");   // only when used: same output as the old app
                if (lo.Breaching) o.WriteLine("breaching\ttrue");                                    // only when true: same reason
                return 0;
            }
            case "--loadout-code":
            {
                // --loadout-code --game D [--data D] : app<TAB>version, game<TAB>version ("-" = not installed), same<TAB>true|false
                var c = Loadout.Code(Opt("--game") ?? throw new ArgumentException("--loadout-code needs --game <folder>"),
                                     Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("data root not found"));
                o.WriteLine($"app\t{(c.AppExists ? c.App : "-")}");
                o.WriteLine($"game\t{c.Game ?? "-"}");
                o.WriteLine($"same\t{(c.Same ? "true" : "false")}");
                return 0;
            }
            case "--loadout-write":
            {
                // --loadout-write --game D [--data D] --on true|false [--glyphs a,b] [--stats a,b] [--slots K=V,K=V] [--burdens a,b,c] [--breaching]
                // --game is required (sandbox tests only; never picks the real game folder by itself)
                var g = Opt("--game") ?? throw new ArgumentException("--loadout-write needs --game <folder>");
                var d = Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("data root not found");
                List<string> Split(string? v) => (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                var slots = Split(Opt("--slots")).Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
                foreach (var line in Loadout.Write(g, d, Opt("--on") == "true", Split(Opt("--glyphs")), Split(Opt("--stats")), slots, Split(Opt("--burdens")),
                                                   Array.IndexOf(a, "--breaching") >= 0))
                    o.WriteLine(line);
                return 0;
            }
            case "--tweak-read":
            {
                // --tweak-read --game D : fov_on, fov_deg, names, msgs (one "key<TAB>value" line each; "-" = line missing)
                var g = Opt("--game") ?? throw new ArgumentException("--tweak-read needs --game <folder>");
                var f = Tweaks.FovGet(g);
                o.WriteLine($"fov_on\t{(f.On ? "true" : "false")}");
                o.WriteLine($"fov_deg\t{f.Deg}");
                o.WriteLine($"names\t{Tweaks.BarNamesGet(g) ?? "-"}");
                var mg = Tweaks.MsgsGet(g);
                o.WriteLine($"msgs\t{(mg == null ? "-" : mg.Value ? "true" : "false")}");
                return 0;
            }
            case "--tweak-write":
            {
                // --tweak-write --game D [--fov on:N|off:N] [--names TEXT] [--msgs true|false]   (sandbox tests only)
                var g = Opt("--game") ?? throw new ArgumentException("--tweak-write needs --game <folder>");
                if (Opt("--fov") is { } fov)
                {
                    var p = fov.Split(':');
                    o.WriteLine(Tweaks.FovSet(g, p[0] == "on", int.Parse(p[1]), () => GameLocator.IsGameRunningIn(g)) ? "fov changed" : "fov same");
                }
                if (Opt("--names") is { } names) { Tweaks.BarNamesSet(g, names); o.WriteLine("names set"); }
                if (Opt("--msgs") is { } msgs) { Tweaks.MsgsSet(g, msgs == "true"); o.WriteLine("msgs set"); }
                return 0;
            }
            case "--lab-fighters":
            case "--lab-slot":
            case "--lab-slot-orig":
            {
                // --lab-fighters V1 V2 V3|- D2 S2 D3 S3 --game D [--data D] [--recycle D]   (S = spot index 0..5)
                // --lab-slot WadName WadRel MB --game D ...   /   --lab-slot-orig WadName WadRel --game D ...   (sandbox tests only)
                var g = Opt("--game") ?? throw new ArgumentException(a[0] + " needs --game <folder>");
                var d = Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("data root not found");
                var ap = new ModApplier(g, d, Opt("--recycle") ?? ModApplier.DefaultRecycle, new ModMatcher(g, d));
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                var res = a[0] switch
                {
                    "--lab-fighters" => ap.ApplyFighters(new Lab.Fighters(a[1], a[2], a[3] == "-" ? null : a[3],
                        double.Parse(a[4], inv), int.Parse(a[5]), double.Parse(a[6], inv), int.Parse(a[7]))),
                    "--lab-slot" => ap.SetSlot(a[1], a[2], uint.Parse(a[3]) * 1024 * 1024),
                    _ => ap.RestoreSlot(a[1], a[2]),
                };
                foreach (var line in res.Log) o.WriteLine(line);
                o.WriteLine(res.NoChange ? "no change" : res.FullRestart ? "restart: full" : "restart: menu");
                return 0;
            }
            case "--dumpfill":
            {
                // --dumpfill --game D [--out D] [--only WAD] : fill missing MIO dumps from D's own wads (DumpFill.cs; tests).
                // One "dump<TAB>status<TAB>sha|-<TAB>ms<TAB>why" line per target, then "peak<TAB>working set<TAB>private bytes".
                var g = Opt("--game") ?? throw new ArgumentException("--dumpfill needs --game <folder>");
                foreach (var f in DumpFill.FillMissing(g, Opt("--out"), Opt("--only")))
                    o.WriteLine($"{f.Target.DumpRel}\t{f.Status}\t{f.Sha ?? "-"}\t{f.Ms}\t{f.Why}");
                using var me = System.Diagnostics.Process.GetCurrentProcess();
                o.WriteLine($"peak\t{me.PeakWorkingSet64}\t{me.PeakPagedMemorySize64}");
                return 0;
            }
            case "--items-build":
            {
                // --items-build --game D [--lang en] --out FILE : the loadout item list from the player's own files (8b)
                var g = Opt("--game") ?? throw new ArgumentException("--items-build needs --game <folder>");
                var outFile = Opt("--out") ?? throw new ArgumentException("--items-build needs --out <file>");
                File.WriteAllText(outFile, ItemsGen.Build(g, Opt("--lang") ?? "en"), new System.Text.UTF8Encoding(false));
                o.WriteLine("written " + outFile);
                return 0;
            }
            case "--loader":
            {
                // --loader check|install|remove --game D [--data D] [--appdata D] : the loadout loader chain (Loader.cs; tests only)
                var g = Opt("--game") ?? throw new ArgumentException("--loader needs --game <folder>");
                switch (a.Length > 1 ? a[1] : "")
                {
                    case "check":
                        o.WriteLine($"problem\t{Loader.Problem(g) ?? "-"}");
                        o.WriteLine($"present\t{(Loader.Present(g) ? "true" : "false")}");
                        foreach (var rec in Loader.Records().Where(x => x.Root == Loader.RootKey(g))) o.WriteLine($"record\t{rec.Game}\t{rec.Backup ?? "-"}");
                        return 0;
                    case "install":
                        var d = Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("data root not found");
                        foreach (var line in Loader.Install(g, d)) o.WriteLine(line);
                        return 0;
                    case "fix":
                        o.WriteLine(Loader.IniFix(g));
                        return 0;
                    case "restore":
                        o.WriteLine(Loader.IniRestore(g) ?? "no backup");
                        return 0;
                    case "setup":   // --loader setup --game D --data D [--door true|false] [--exe-version X] : the Setup card rows
                        bool? door = Opt("--door") is { } dv ? dv == "true" : null;
                        foreach (var sr in SetupCheck.Run(g, Opt("--data"), door, Opt("--exe-version")))
                            o.WriteLine($"{sr.Key}	{(sr.Ok == true ? "ok" : sr.Ok == false ? "no" : "unknown")}	{sr.Fix}	{sr.Next}");
                        return 0;
                    case "remove":
                        foreach (var line in Loader.Remove(g)) o.WriteLine(line);
                        return 0;
                    default:
                        throw new ArgumentException("--loader check|install|remove");
                }
            }
            case "--edition":
            {
                // --edition [--data D] : the edition file of a data folder (Edition.cs), "key<TAB>value" lines (tests only);
                // "edition<TAB>none" = the normal app
                var d = Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("data root not found");
                var ed = Edition.Load(d);
                if (ed == null) { o.WriteLine("edition\tnone"); return 0; }
                o.WriteLine($"edition\t{ed.Id}");
                o.WriteLine($"version\t{ed.Version}");
                o.WriteLine($"tabs\t{string.Join(",", ed.Tabs)}");
                o.WriteLine($"loadout\t{(ed.Loadout ? "true" : "false")}");
                o.WriteLine($"comingSoon\t{ed.ComingSoon.Count}");
                return 0;
            }
            case "--pack-check":
            case "--pack-install":
            {
                // --pack-check ZIP [--app-version X] : "file<TAB>mod<TAB>game path" lines (the preview), "ok<TAB>id<TAB>version" or the error on stderr, exit 1 (PackManifest.cs; tests only)
                // --pack-install ZIP --appdata D : PackInstaller into D\packs\<id> (no game files touched; tests; env SBG_PACK_CRASH)
                var app = Opt("--app-version") is { } av ? Version.Parse(av) : PackManifest.AppVersion;
                var man = PackManifest.FromZip(a[1], app);
                var info = man.Info;
                foreach (var f in man.GameFiles) o.WriteLine($"file\t{f.Mod}\t{f.Path}");   // the preview list
                if (a[0] == "--pack-install")
                {
                    if (Opt("--appdata") == null) throw new ArgumentException("--pack-install needs --appdata <folder>");
                    // the same installer as the window: other packs' groups are refused; with --game G [--recycle R] the
                    // old version's mods are turned off first, as in the window (PackCli.cs); context-free thread
                    var inst = new PackInstaller { Taken = PackCli.Taken() };
                    // the old version is looked up before the install: Packs.List cleans hidden folders (also the new one's)
                    var old = Packs.List().FirstOrDefault(x => x.Info.Id == info.Id);
                    if (Opt("--game") is { } ig && old != null)
                        inst.TurnOldOff = _ => Task.FromResult(PackCli.TurnOff(old, ig, Opt("--recycle") ?? ModApplier.DefaultRecycle, o));
                    var done = Task.Run(() => inst.InstallAsync(a[1], app)).GetAwaiter().GetResult();
                    o.WriteLine("installed\t" + done!.Root);
                }
                o.WriteLine($"ok\t{info.Id}\t{info.Version}");
                return 0;
            }
            case "--pack-remove":
            {
                // --pack-remove ID --appdata D --game G [--recycle R] [--force] : its mods off, then the pack deleted (as the
                // window's Remove; --force = "Remove anyway"). Exit 1 and the pack kept when a mod could not be turned off.
                if (Opt("--appdata") == null || Opt("--game") is not { } rg) throw new ArgumentException("--pack-remove needs --appdata <folder> --game <folder>");
                if (!GameLocator.IsGameDir(rg)) throw new PackError("Set the game folder first: its mods must be turned off before it can go.");
                if (!PackManifest.IdRx.IsMatch(a[1])) throw new PackError("Bad pack id: " + a[1]);
                var pack = Packs.List().FirstOrDefault(x => x.Info.Id == a[1]) ?? throw new PackError("not installed: " + a[1]);
                if (!PackCli.TurnOff(pack, rg, Opt("--recycle") ?? ModApplier.DefaultRecycle, o) && Array.IndexOf(a, "--force") < 0)
                {
                    Console.Error.WriteLine("kept: a mod could not be turned off");
                    return 1;
                }
                Packs.Delete(a[1]);
                o.WriteLine("removed\t" + a[1]);
                return 0;
            }
            case "--pack-list":
            {
                // --pack-list --appdata D : "pack<TAB>id<TAB>version<TAB>groups" per installed pack, "error<TAB>..." per broken one
                var errs = new List<string>();
                foreach (var p in Packs.List(errs)) o.WriteLine($"pack\t{p.Info.Id}\t{p.Info.Version}\t{string.Join(",", p.Info.Groups)}");
                foreach (var er in errs) o.WriteLine("error\t" + er);
                return 0;
            }
            case "--rules":
            {
                // --rules FILE [--data D] : FILE = a RuleState as JSON; prints "key<TAB>warning" lines (tests only)
                var d = Opt("--data") ?? DataRoot.Find() ?? throw new DirectoryNotFoundException("data root not found");
                var rcat = ModCatalog.Load(DataRoot.CatalogPath(d), Edition.Load(d));
                var st = System.Text.Json.JsonSerializer.Deserialize<RuleState>(File.ReadAllText(a[1]),
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new RuleState();
                foreach (var kv in Rules.Check(rcat.Rules, st).OrderBy(k => k.Key, StringComparer.Ordinal))
                    foreach (var w in kv.Value) o.WriteLine($"{kv.Key}\t{w}");
                return 0;
            }
            default:
                Console.Error.WriteLine("unknown option " + a[0]);
                return 2;
        }
    }
}
