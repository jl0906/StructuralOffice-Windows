namespace StructuralOffice.Desktop.Services;

public sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private ReleaseVersion(int major, int minor, int patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public IReadOnlyList<string> Prerelease { get; }
    public bool IsPrerelease => Prerelease.Count > 0;

    public static bool TryParse(string? value, out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }

        var parts = normalized.Split('-', 2);
        var numbers = parts[0].Split('.');
        var patch = 0;
        if (numbers.Length is < 2 or > 3 ||
            !int.TryParse(numbers[0], out var major) ||
            !int.TryParse(numbers[1], out var minor) ||
            (numbers.Length == 3 && !int.TryParse(numbers[2], out patch)))
        {
            return false;
        }

        var prerelease = parts.Length == 2
            ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (parts.Length == 2 && prerelease.Length == 0)
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, numbers.Length == 3 ? patch : 0, prerelease);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var numeric = Major.CompareTo(other.Major);
        if (numeric == 0) numeric = Minor.CompareTo(other.Minor);
        if (numeric == 0) numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0) return numeric;

        if (!IsPrerelease && !other.IsPrerelease) return 0;
        if (!IsPrerelease) return 1;
        if (!other.IsPrerelease) return -1;

        for (var index = 0; index < Math.Max(Prerelease.Count, other.Prerelease.Count); index++)
        {
            if (index >= Prerelease.Count) return -1;
            if (index >= other.Prerelease.Count) return 1;
            var leftNumeric = int.TryParse(Prerelease[index], out var leftNumber);
            var rightNumeric = int.TryParse(other.Prerelease[index], out var rightNumber);
            if (leftNumeric && rightNumeric)
            {
                var comparison = leftNumber.CompareTo(rightNumber);
                if (comparison != 0) return comparison;
            }
            else if (leftNumeric != rightNumeric)
            {
                return leftNumeric ? -1 : 1;
            }
            else
            {
                var comparison = string.Compare(
                    Prerelease[index], other.Prerelease[index], StringComparison.OrdinalIgnoreCase);
                if (comparison != 0) return comparison;
            }
        }

        return 0;
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        return IsPrerelease ? $"{value}-{string.Join('.', Prerelease)}" : value;
    }
}
