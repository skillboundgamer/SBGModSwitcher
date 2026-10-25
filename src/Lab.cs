namespace ModSwitcher2;

/// <summary>
/// The Lab (experimental, hidden behind Settings -> "Show experimental") is not part of the public source.
/// Releases (editions) never build or show it. This stub keeps the names the rest of the app uses, so the app builds.
/// </summary>
public static class Lab
{
    public sealed record SlotInfo(string Name, string Wad, string WadRel);

    public static readonly SlotInfo[] Slots = Array.Empty<SlotInfo>();

    public const uint Step = 2 * 1024 * 1024;

    public const string FightersBase = "";
    public const string KingsDcb = "";
    public const string KingsVS = "";

    public sealed record Fighters(string V1, string V2, string? V3, double D2, int S2, double D3, int S3)
    {
        public string Describe() => "";
    }

    public static void Validate(Fighters f) => throw new NotSupportedException("the Lab is not in this build");

    public static (byte[] Script, string DcbSrc) BuildFighters(byte[] baseScript, Fighters f) =>
        throw new NotSupportedException("the Lab is not in this build");
}
