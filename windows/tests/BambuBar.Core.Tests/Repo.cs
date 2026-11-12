using System.Reflection;

namespace BambuBar.Core.Tests;

/// <summary>Paths in the repository, from the root the build records in this assembly.</summary>
internal static class Repo
{
    public static string Root { get; } = typeof(Repo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(a => a.Key == "RepoRoot").Value ?? throw new InvalidOperationException("no RepoRoot");

    /// <summary>A path under shared/, the files the Mac app uses too.</summary>
    public static string Shared(params string[] parts) => Path.Combine([Root, "shared", .. parts]);
}
