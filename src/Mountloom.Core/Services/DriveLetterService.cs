namespace Mountloom.Core.Services;

public sealed class DriveLetterService
{
    public IReadOnlyList<string> GetAvailableDriveLetters(IEnumerable<string>? reservedLetters = null)
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => Normalize(drive.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (reservedLetters is not null)
        {
            used.UnionWith(reservedLetters.Select(Normalize));
        }

        return Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(letter => $"{(char)letter}:")
            .Where(letter => !used.Contains(letter))
            .ToList();
    }

    public bool IsDriveLetterAvailable(string driveLetter)
    {
        var normalized = Normalize(driveLetter);
        return IsValid(normalized) && !DriveInfo.GetDrives()
            .Any(drive => string.Equals(drive.Name.TrimEnd('\\'), normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsValid(string? driveLetter)
    {
        var value = Normalize(driveLetter);
        return value.Length == 2 && value[0] is >= 'A' and <= 'Z' && value[1] == ':';
    }

    public static string Validate(string? driveLetter)
    {
        var value = Normalize(driveLetter);
        if (!IsValid(value))
        {
            throw new ArgumentException("Choose a Windows drive letter such as X:.", nameof(driveLetter));
        }
        return value;
    }

    public static string Normalize(string? driveLetter)
    {
        var value = (driveLetter ?? string.Empty).Trim().TrimEnd('\\');
        if (value.Length == 1 && char.IsAsciiLetter(value[0]))
        {
            value += ":";
        }

        return value.ToUpperInvariant();
    }
}
