using System.Text.RegularExpressions;

namespace BambuBar.Core.Tests;

public class VersionTests
{
    /// <summary>The Windows build takes its version from the Mac's project.yml, so the two can't drift.</summary>
    [Fact]
    public void VersionIsTheMacMarketingVersion()
    {
        var spec = File.ReadAllText(Path.Combine(Repo.Root, "project.yml"));
        var match = Regex.Match(spec, @"^\s*MARKETING_VERSION:\s*""?(\d+\.\d+\.\d+)", RegexOptions.Multiline);
        Assert.True(match.Success, "no MARKETING_VERSION in project.yml");
        Assert.Equal(match.Groups[1].Value, AppVersion.Marketing);
    }
}
