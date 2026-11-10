using System.Globalization;

namespace BambuBar.Core.Updates;

/// <summary>A release's X.Y.Z version (the Mac's MARKETING_VERSION, shared by both platforms), compared numerically:
/// 1.10.0 is newer than 1.9.0.</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>Reads exactly three dot-separated runs of ASCII digits that fit an int; anything else is false.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        var parts = text?.Split('.');
        if (parts is not { Length: 3 } || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return false;
        var values = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return false;
        }
        version = new(values[0], values[1], values[2]);
        return true;
    }

    public int CompareTo(ReleaseVersion other) =>
        Major != other.Major ? Major.CompareTo(other.Major)
        : Minor != other.Minor ? Minor.CompareTo(other.Minor)
        : Patch.CompareTo(other.Patch);

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
