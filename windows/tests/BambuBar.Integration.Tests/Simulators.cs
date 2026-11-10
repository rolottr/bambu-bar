using System.Reflection;

namespace BambuBar.Integration.Tests;

/// <summary>The Docker simulators in shared/scripts/integration/compose.yaml, described to the tests through the
/// environment (the same variables as the Mac's integration tests). Without them every test skips.</summary>
internal static class Simulators
{
    public static bool Enabled => Environment.GetEnvironmentVariable("BAMBUBAR_INTEGRATION") == "1";

    /// <summary>Skips the calling test unless the simulators are running.</summary>
    public static void Require() =>
        Assert.SkipUnless(Enabled, "Set BAMBUBAR_INTEGRATION=1 with the simulators running (windows/tools/integration-test.sh)");

    private static string RepoRoot { get; } = typeof(Simulators).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "RepoRoot").Value!;

    /// <summary>The Bambu Studio settings the fleet wrote (SERIAL_CASE=mixed).</summary>
    public static string FleetSettings =>
        Environment.GetEnvironmentVariable("BAMBUBAR_FLEET_SETTINGS")
        ?? Path.Combine(RepoRoot, "shared", "scripts", "integration", "out", "BambuStudio.conf");

    public const int FleetSize = 17;
    /// <summary>Listed in the settings, never answers: a printer that's switched off.</summary>
    public const string SwitchedOffPrinter = "01PF00000000017";
    /// <summary>Where the fleet sends its announcements (compose.yaml).</summary>
    public const int AnnouncementPort = 2122;
}
