using System.IO;
using System.Text.Json;

namespace ModSwitcher2;

/// <summary>What the VOTG launcher's receipt says (mod\_votg_receipt.json). READ ONLY: this app never writes it.</summary>
public sealed record VotgInfo(string ModVersion, string Payload, string LoaderSha, bool OwnsColor)
{
    /// <summary>The one version tested with our hook: mod 2.0.0, payload 70e89488cadcba7a, loader 0b3b7acf... (VOTG 0.15.546).</summary>
    public const string TestedPayload = "70e89488cadcba7a";
    public const string TestedLoader = "0b3b7acf";
    public const string TestedName = "VOTG 0.15.546";

    public bool Tested => Payload.StartsWith(TestedPayload, StringComparison.OrdinalIgnoreCase)
                          && LoaderSha.StartsWith(TestedLoader, StringComparison.OrdinalIgnoreCase);

    /// <summary>The Overview row text. Plain words; no VOTG text is copied, only its version number.</summary>
    public string Text(bool hookInPlace)
    {
        if (OwnsColor) return "This VOTG version uses the file our Kratos mods need. They stay off while it is installed.";
        if (Tested) return TestedName + " found. Our mods run beside it. The Switcher never changes VOTG files." + (hookInPlace ? "" : " Press Apply to turn our Kratos mods on.");
        return $"VOTG {(ModVersion == "" ? "?" : ModVersion)} found: not tested with this Switcher. Our mods use their own file only. "
             + "If you see a problem, press Turn off our Kratos hook (VOTG stays as it is).";
    }
}

public static class VotgReceipt
{
    const string Color = "libraries/core/color.lua";

    /// <summary>Reads the receipt of the game folder; null = no VOTG receipt (or one this app cannot read: then VOTG is treated as unknown, never as absent when the file exists, see <see cref="Exists"/>).</summary>
    public static VotgInfo? Read(string gameRoot)
    {
        var p = Path.Combine(gameRoot, Loader.VotgReceiptRel);
        if (!File.Exists(p)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            var r = doc.RootElement;
            string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string loader = "";
            if (r.TryGetProperty("loader", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("files", out var lf) && lf.ValueKind == JsonValueKind.Object)
                foreach (var f in lf.EnumerateObject()) if (f.Name.Equals("winmm.dll", StringComparison.OrdinalIgnoreCase) && f.Value.ValueKind == JsonValueKind.String) loader = f.Value.GetString() ?? "";
            bool owns = false;
            if (r.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
                foreach (var f in files.EnumerateObject()) if (f.Name.Replace('\\', '/').EndsWith(Color, StringComparison.OrdinalIgnoreCase)) owns = true;
            return new(Str(r, "mod_version"), Str(r, "payload"), loader, owns);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new("", "", "", false);   // unreadable receipt: VOTG is there, version unknown (untested text)
        }
    }

    /// <summary>The game paths the receipt lists (its files and backups); null = no receipt.</summary>
    public static List<string>? Files(string gameRoot)
    {
        var p = Path.Combine(gameRoot, Loader.VotgReceiptRel);
        if (!File.Exists(p)) return null;
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(p));
            foreach (var key in new[] { "files", "backups" })
                if (doc.RootElement.TryGetProperty(key, out var o) && o.ValueKind == JsonValueKind.Object)
                    foreach (var f in o.EnumerateObject()) list.Add(f.Name);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        return list;
    }

    public static bool Exists(string gameRoot) => File.Exists(Path.Combine(gameRoot, Loader.VotgReceiptRel));

    // ---- votg.json: what this app told the player about a VOTG version (loader\votg.json, ours) ----
    sealed class State { public List<string> Asked { get; set; } = new(); }
    static string StatePath => Path.Combine(AppSettings.Dir, "loader", "votg.json");
    static State Load() { try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new(); } catch (JsonException) { return new(); } }

    /// <summary>The first install on an untested VOTG version asks once: the first call returns false (and remembers), the next true.</summary>
    public static bool AskedAlready(string payloadKey)
    {
        var s = Load();
        if (s.Asked.Contains(payloadKey)) return true;
        s.Asked.Add(payloadKey);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s));
        File.Move(tmp, StatePath, overwrite: true);
        return false;
    }
}
