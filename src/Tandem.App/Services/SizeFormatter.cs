using System.Globalization;
using Tandem.App.Resources;

namespace Tandem.App.Services;

/// <summary>File sizes for display, in binary units with the current <see cref="Loc"/> language's unit labels and
/// decimal separator: "1023 B", "2.4 MB" / "1023 Б", "2,4 МБ".</summary>
public static class SizeFormatter
{
    private const double Step = 1024;
    private static readonly string[] UnitKeys = ["Unit_B", "Unit_KB", "Unit_MB", "Unit_GB", "Unit_TB"];

    /// <summary>Bytes are shown whole; from KB up, with one decimal. A negative size is shown as zero.</summary>
    public static string Format(long bytes)
    {
        var culture = Loc.Culture;
        if (bytes < Step)
            return $"{Math.Max(bytes, 0).ToString(culture)} {Loc.Get(UnitKeys[0])}";

        double value = bytes;
        var unit = 0;
        // Step up while the value would print as 1024.0 or more, so 1048575 bytes reads "1.0 MB", not "1024.0 KB".
        while (unit < UnitKeys.Length - 1 && Math.Round(value, 1, MidpointRounding.AwayFromZero) >= Step)
        {
            value /= Step;
            unit++;
        }

        var rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        return $"{rounded.ToString("0.0", culture)} {Loc.Get(UnitKeys[unit])}";
    }
}
