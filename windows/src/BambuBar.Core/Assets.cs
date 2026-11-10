namespace BambuBar.Core;

/// <summary>
/// The files Core reads that the Mac app bundles too (shared/ in the repository): HMS texts, certificates, filament
/// names, printer art. BambuBar.Core.csproj copies them to <c>Assets\</c> next to every program that uses Core.
/// </summary>
public static class Assets
{
    /// <summary>The folder holding HMS\, Certificates\, Printers\ and filaments.json.</summary>
    public static string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets");

    public static string PathOf(params string[] parts) => Path.Combine([Root, .. parts]);
}
