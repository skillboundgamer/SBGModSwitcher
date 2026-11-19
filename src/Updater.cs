using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModSwitcher2;

// GitHub build only: the csproj leaves this file out of the Nexus build (SbgNexus=true), which holds no network code.

/// <summary>A newer release as the feed names it: version, notes, the zip to download and its sha256.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, string ZipName, string ZipUrl, string Sha256, long Size);

/// <summary>One error with a plain message for the player (no stack, no exception names).</summary>
public sealed class UpdateError(string message) : Exception(message);

/// <summary>
/// The update check: one plain GET of the public "latest release" of the app's GitHub repo, once per start, off the UI
/// thread. No personal data is sent (only the app name and version in the User-Agent, which GitHub asks for).
/// The feed can be pointed somewhere else for tests: env SBG_UPDATE_FEED = the URL of a latest-release JSON. Only with
/// that override may the feed and the zip be on a local test server (http://127.0.0.1 / localhost); without it, only
/// https links on GitHub's own hosts are used.
/// </summary>
public static class Updater
{
    public const string DefaultFeed = "https://api.github.com/repos/skillboundgamer/SBGModSwitcher/releases/latest";
    public const string FeedEnv = "SBG_UPDATE_FEED";
    /// <summary>The zip GitHub players get: SBGModSwitcher-&lt;version&gt;.zip (the -nexus zip is never picked).</summary>
    static readonly Regex ZipRx = new(@"^SBGModSwitcher-\d+(\.\d+){1,3}\.zip$", RegexOptions.IgnoreCase);
    static readonly string[] GitHubHosts = { "api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
    public const long MaxZip = 300L * 1024 * 1024;

    static string? FeedOverride => Environment.GetEnvironmentVariable(FeedEnv) is { Length: > 0 } f ? f.Trim() : null;
    public static string Feed => FeedOverride ?? DefaultFeed;

    /// <summary>This app's version, always 4 parts (2.0.4.1; a missing part is 0).</summary>
    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version ?? new Version(2, 0));

    public static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>"v2.0.4.1", "2.0.5" -> a 4-part version; null when the tag is not a version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        var t = (tag ?? "").Trim().TrimStart('v', 'V');
        return Version.TryParse(t, out var v) && v.Major >= 0 ? Norm(v) : null;
    }

    public static bool IsNewer(Version remote, Version local) => Norm(remote) > Norm(local);

    /// <summary>True for a link the updater may open: https on GitHub's hosts, or a loopback test server when the feed is overridden.</summary>
    public static bool AllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !string.IsNullOrEmpty(u.UserInfo)) return false;
        if (u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && GitHubHosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase)) return true;
        return FeedOverride != null && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && u.IsLoopback;
    }

    /// <summary>Read a GitHub latest-release JSON. Drafts and pre-releases give null. A missing zip or checksum throws.</summary>
    public static ReleaseInfo? ParseFeed(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (Bool(r, "draft") || Bool(r, "prerelease")) return null;
        var tag = Str(r, "tag_name");
        var ver = ParseVersion(tag) ?? throw new UpdateError($"The newest release has no version number ({tag}).");
        var notes = Str(r, "body");
        if (!r.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new UpdateError("The newest release has no download.");
        foreach (var a in assets.EnumerateArray())
        {
            var name = Str(a, "name");
            if (!ZipRx.IsMatch(name)) continue;
            var url = Str(a, "browser_download_url");
            if (!AllowedUrl(url)) throw new UpdateError("The download link of the newest release is not on GitHub.");
            var sha = ShaOf(Str(a, "digest"), notes, name)
                      ?? throw new UpdateError("The newest release names no checksum (sha256) for its zip, so it is not installed.");
            long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
            return new ReleaseInfo(ver, tag, notes, name, url, sha, size);
        }
        throw new UpdateError("The newest release has no SBGModSwitcher zip.");
    }

    /// <summary>The asset digest "sha256:HEX" GitHub gives; else the "`NAME` sha256: `HEX`" line of the release notes.</summary>
    static string? ShaOf(string digest, string notes, string zipName)
    {
        var m = Regex.Match(digest, @"^sha256:([0-9a-fA-F]{64})$");
        if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        m = Regex.Match(notes, Regex.Escape(zipName) + @"`?\s*sha256:\s*`?([0-9a-fA-F]{64})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    static HttpClient? _http;
    /// <summary>One client for the app's life. Redirects are followed (GitHub sends the zip from another host); never https to http.</summary>
    internal static HttpClient Http => _http ??= MakeClient();

    static HttpClient MakeClient()
    {
        var h = new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, ConnectTimeout = TimeSpan.FromSeconds(10), UseCookies = false };
        var c = new HttpClient(h) { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"SBGModSwitcher/{Current}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>The newest release when it is newer than <paramref name="current"/>, else null. Failures throw UpdateError.</summary>
    public static async Task<ReleaseInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        var feed = Feed;
        if (!AllowedUrl(feed)) throw new UpdateError("The update address is not allowed.");
        string json;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var resp = await Http.GetAsync(feed, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) throw new UpdateError($"The update check got no answer from GitHub (HTTP {(int)resp.StatusCode}).");
                json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch (UpdateError) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new UpdateError("No connection to GitHub, so no update check this time.");
            }
        }
        ReleaseInfo? r;
        try { r = ParseFeed(json); }
        catch (JsonException) { throw new UpdateError("The update check got an answer it cannot read."); }
        return r != null && IsNewer(r.Version, current) ? r : null;
    }

    /// <summary>The release notes for the confirm box: plain text, at most <paramref name="max"/> characters.</summary>
    public static string ShortNotes(string notes, int max = 1200)
    {
        var t = (notes ?? "").Replace("\r", "").Replace("**", "").Replace("`", "").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + "\n...";
    }
}
