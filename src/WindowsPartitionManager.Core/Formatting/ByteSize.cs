using System.Globalization;

namespace WindowsPartitionManager.Core.Formatting;

public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>
    /// Formats a byte count using binary multiples (1024) with the short labels Windows itself
    /// shows, so a "2 TB" drive reads as "1.82 TB" exactly like in Explorer and Disk Management.
    /// </summary>
    public static string Format(ulong bytes)
    {
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        var format = value >= 100 ? "F0" : value >= 10 ? "F1" : "F2";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + Units[unit];
    }

    /// <summary>Parses "500GB", "1.5 TB", "2048M", "123456789" (bytes). Binary multiples, like <see cref="Format"/>.</summary>
    public static bool TryParse(string? text, out ulong bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var digits = 0;
        while (digits < s.Length && (char.IsDigit(s[digits]) || s[digits] == '.' || s[digits] == ','))
        {
            digits++;
        }

        // "1.500GB" or "1,500GB" could mean 1.5 or 1500: refuse instead of guessing.
        var number = s[..digits];
        var separator = number.IndexOfAny(['.', ',']);
        if (separator >= 0 && number.Length - separator - 1 == 3)
        {
            return false;
        }

        if (digits == 0 || !double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        var suffix = s[digits..];
        if (suffix.EndsWith('B'))
        {
            suffix = suffix[..^1];
        }

        var exponent = suffix switch
        {
            "" => 0,
            "K" => 1,
            "M" => 2,
            "G" => 3,
            "T" => 4,
            "P" => 5,
            _ => -1,
        };

        if (exponent < 0)
        {
            return false;
        }

        var result = value * Math.Pow(1024, exponent);
        if (result > ulong.MaxValue)
        {
            return false;
        }

        bytes = (ulong)result;
        return true;
    }
}
