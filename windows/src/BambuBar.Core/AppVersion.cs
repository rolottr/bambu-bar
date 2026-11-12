using System.Reflection;

namespace BambuBar.Core;

/// <summary>The app's version, shared with the Mac (MARKETING_VERSION in project.yml, read at build time).</summary>
public static class AppVersion
{
    /// <summary>X.Y.Z, as the Mac shows it and the appcast compares it.</summary>
    public static string Marketing { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        // The SDK appends "+<commit>" when it builds from a Git checkout.
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational[..plus];
    }
}
