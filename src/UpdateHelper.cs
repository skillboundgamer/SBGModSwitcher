using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace ModSwitcher2;

// GitHub build only (left out of the Nexus build by the csproj).

/// <summary>
/// The update itself, in two halves:
/// 1. <see cref="PrepareAsync"/> (in the app, off the UI thread): download the release zip into a temp folder, check its
///    sha256 against the one the feed gives, unpack it. Nothing in the app folder is touched; any failure stops here.
/// 2. <see cref="Apply"/> (in a helper: a copy of this exe started from the temp folder with --update-apply, after the
///    app has closed): each file of the new version replaces the old one, the old one kept beside it as NAME.old. Any
///    error puts every old file back (the .old copies), so the old version stays as it was.
/// Settings and packs live in AppData and are never touched. Hidden test switch: env SBG_UPDATE_FAIL_AFTER = N makes
/// Apply fail after N files (the "copy error halfway" mock).
/// </summary>
public static class UpdateHelper
{
    public const string AppExe = "SBGModSwitcher.exe";
    public const string ListName = "update-old.txt";   // in the app folder: the files the last update replaced
    public static string TempRoot => Path.Combine(Path.GetTempPath(), "SBGModSwitcher-update");

    /// <summary>Download, check and unpack <paramref name="r"/> into <paramref name="staging"/>\new. -> that folder.</summary>
    public static async Task<string> PrepareAsync(ReleaseInfo r, string staging, IProgress<int>? percent = null, CancellationToken ct = default)
    {
        if (r.Size > Updater.MaxZip) throw new UpdateError("The download is too big to be the app; it is not installed.");
        if (!Updater.AllowedUrl(r.ZipUrl)) throw new UpdateError("The download link is not on GitHub.");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var zip = Path.Combine(staging, "download.zip");
        try { await DownloadAsync(r, zip, percent, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            TryDelete(zip);
            throw new UpdateError("The download did not finish (no connection?). Nothing was changed.");
        }
        var got = Sha256(zip);
        if (!string.Equals(got, r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(zip);
            throw new UpdateError("The download does not match its checksum (sha256), so it is not installed. Nothing was changed.");
        }
        var dir = Path.Combine(staging, "new");
        try { Unpack(zip, dir); }
        catch (InvalidDataException) { throw new UpdateError("The downloaded zip is broken. Nothing was changed."); }
        if (!File.Exists(Path.Combine(dir, AppExe))) throw new UpdateError($"The download holds no {AppExe}. Nothing was changed.");
        return dir;
    }

    static async Task DownloadAsync(ReleaseInfo r, string path, IProgress<int>? percent, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(10));
        using var resp = await Updater.Http.GetAsync(r.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new UpdateError($"The download failed (HTTP {(int)resp.StatusCode}). Nothing was changed.");
        long total = resp.Content.Headers.ContentLength ?? r.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
        var buf = new byte[1 << 16];
        long done = 0;
        int n, last = -1;
        while ((n = await src.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
        {
            done += n;
            if (done > Updater.MaxZip) throw new UpdateError("The download is too big to be the app; it is not installed.");
            await dst.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
            if (total > 0 && percent != null && (int)(done * 100 / total) is var p && p != last) { last = p; percent.Report(p); }
        }
    }

    public static string Sha256(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }

    /// <summary>Unpack into <paramref name="dir"/>; one top folder in the zip (SBGModSwitcher-x.y/) is dropped. No entry may leave the folder.</summary>
    public static void Unpack(string zip, string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        using var z = ZipFile.OpenRead(zip);
        var files = z.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        string top = "";
        var first = files.Select(e => e.FullName.Replace('\\', '/')).FirstOrDefault() ?? "";
        if (first.Contains('/') && first[..(first.IndexOf('/') + 1)] is var t && files.All(e => e.FullName.Replace('\\', '/').StartsWith(t))) top = t;
        foreach (var e in files)
        {
            var rel = e.FullName.Replace('\\', '/')[top.Length..];
            var target = Path.GetFullPath(Path.Combine(dir, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateError("The downloaded zip has a bad file name. Nothing was changed.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, false);
        }
    }

    /// <summary>The files of <paramref name="from"/> (relative paths), in a fixed order.</summary>
    static List<string> RelFiles(string from) =>
        Directory.GetFiles(from, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(from, f))
                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Replace the app's files in <paramref name="to"/> with those of <paramref name="from"/>; every replaced file stays as
    /// NAME.old. On any error every file is put back and UpdateError says so. -> the number of files copied.
    /// </summary>
    public static int Apply(string from, string to, Action<string>? log = null)
    {
        int failAfter = int.TryParse(Environment.GetEnvironmentVariable("SBG_UPDATE_FAIL_AFTER"), out var fa) ? fa : -1;
        var files = RelFiles(from);
        if (!files.Contains(AppExe, StringComparer.OrdinalIgnoreCase)) throw new UpdateError($"The new version holds no {AppExe}. Nothing was changed.");
        var done = new List<(string Target, bool HadOld)>();
        int copied = 0;
        try
        {
            foreach (var rel in files)
            {
                var target = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool had = File.Exists(target);
                if (had) File.Move(target, target + ".old", true);
                done.Add((target, had));
                if (copied == failAfter) throw new IOException("copy error (test switch SBG_UPDATE_FAIL_AFTER)");
                File.Copy(Path.Combine(from, rel), target, false);
                copied++;
                log?.Invoke("copied\t" + rel);
            }
            File.WriteAllLines(Path.Combine(to, ListName), done.Where(d => d.HadOld).Select(d => Path.GetRelativePath(to, d.Target)));
            return copied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var left = Undo(done);
            throw new UpdateError(left.Count == 0
                ? $"The update could not be installed ({ex.Message}). The old version was put back, nothing changed."
                : $"The update could not be installed ({ex.Message}) and {left.Count} file(s) could not be put back: " +
                  string.Join(", ", left.Select(Path.GetFileName)) + ". Their old copies are the .old files beside them.");
        }
    }

    /// <summary>Put each old file back (newest first). -> the files that could not be put back.</summary>
    static List<string> Undo(List<(string Target, bool HadOld)> done)
    {
        var left = new List<string>();
        for (int i = done.Count - 1; i >= 0; i--)
        {
            var (t, had) = done[i];
            try
            {
                if (File.Exists(t)) File.Delete(t);
                if (had) File.Move(t + ".old", t);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { left.Add(t); }
        }
        return left;
    }

    /// <summary>Undo the last update by hand: every file it replaced goes back from its .old copy. -> files put back.</summary>
    public static int Rollback(string to)
    {
        var list = Path.Combine(to, ListName);
        if (!File.Exists(list)) throw new UpdateError("No earlier update to undo in this folder.");
        int n = 0;
        foreach (var rel in File.ReadAllLines(list).Where(l => l.Trim().Length > 0))
        {
            var t = Path.Combine(to, rel);
            if (!File.Exists(t + ".old")) continue;
            File.Move(t + ".old", t, true);
            n++;
        }
        File.Delete(list);
        return n;
    }

    /// <summary>True when the app may write its own folder (a protected folder needs an update by hand).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            var p = Path.Combine(dir, $".sbg-write-test-{Environment.ProcessId}");
            File.WriteAllText(p, "");
            File.Delete(p);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Start the helper: a copy of this exe (and clrgc.dll beside it) in <paramref name="staging"/>\helper, run with
    /// --update-apply. It waits until this process has exited, installs <paramref name="newDir"/> and starts the app again.
    /// </summary>
    public static void StartHelper(string staging, string newDir)
    {
        var exe = Environment.ProcessPath ?? throw new UpdateError("The app cannot find its own file.");
        var appDir = Path.GetDirectoryName(exe)!;
        var helperDir = Path.Combine(staging, "helper");
        Directory.CreateDirectory(helperDir);
        var helper = Path.Combine(helperDir, Path.GetFileName(exe));
        File.Copy(exe, helper, true);
        var gc = Path.Combine(appDir, "clrgc.dll");
        if (File.Exists(gc)) File.Copy(gc, Path.Combine(helperDir, "clrgc.dll"), true);
        var psi = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = helperDir };
        foreach (var a in new[] { "--update-apply", "--from", newDir, "--to", appDir, "--pid", Environment.ProcessId.ToString(),
                                  "--restart", Path.GetFileName(exe), "--gui" })
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>Old temp folders of earlier updates (the helper cannot delete the folder it runs from). Best effort.</summary>
    public static void CleanTemp()
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (var d in Directory.GetDirectories(TempRoot))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddMinutes(-10))
                    try { Directory.Delete(d, true); } catch (Exception) { /* still in use: next start */ }
        }
        catch (Exception) { }
    }

    static void TryDelete(string p) { try { File.Delete(p); } catch (Exception) { } }
}
