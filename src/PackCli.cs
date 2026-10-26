using System.IO;

namespace ModSwitcher2;

/// <summary>
/// Headless pack steps for the scenario tests (batch B, 2026-10-27): the mods-off part of the window's update and
/// Remove (PacksView.cs TurnPackOffAsync), without the window. Used only by App.xaml.cs --pack-* modes (sandbox tests).
/// Not done here: the Kratos loader chain of the last loadout pack (window: RemoveLoaderIfLastAsync), and a broken
/// pack folder is an error (the window skips it). Next cut: one UI-free "turn off" core shared with the window.
/// </summary>
public static class PackCli
{
    /// <summary>Group id -> (pack id, title) of every installed pack: what PackInstaller.Taken holds in the window.</summary>
    public static Dictionary<string, (string PackId, string Title)> Taken()
    {
        var d = new Dictionary<string, (string, string)>();
        foreach (var p in Packs.List())
            foreach (var g in PackManifest.FromFolder(p.Root, PackManifest.AppVersion).Catalog.Groups)
                d.TryAdd(g.Id, (p.Info.Id, p.Info.Title));
        return d;
    }

    /// <summary>Every group of the pack back to its vanilla preset; false (and a log line) when one failed.</summary>
    public static bool TurnOff(InstalledPack p, string game, string recycle, TextWriter log)
    {
        if (!GameLocator.IsGameDir(game)) throw new PackError("Set the game folder first: its mods must be turned off before it can go.");
        bool ok = true;
        foreach (var g in PackManifest.FromFolder(p.Root, PackManifest.AppVersion).Catalog.Groups)
        {
            var m = new ModMatcher(game, p.Root);
            if (m.Check(g) == g.Vanilla) continue;
            try
            {
                foreach (var line in new ModApplier(game, p.Root, recycle, m).Apply(g.Presets[g.Vanilla]).Log) log.WriteLine(line);
                log.WriteLine($"off\t{g.Id}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
            {
                log.WriteLine($"failed\t{g.Id}\t{ex.Message}");
                ok = false;
            }
        }
        return ok;
    }
}
