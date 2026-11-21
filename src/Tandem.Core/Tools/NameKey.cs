using System.Text;

namespace Tandem.Core.Tools;

/// <summary>The one place that answers "is this the same name". The Unicode form never matters (macOS reports
/// names decomposed, servers and archives usually composed); whether letter case matters depends on the method.
/// </summary>
public static class NameKey
{
    /// <summary>The name in composed form (<see cref="NormalizationForm.FormC"/>). Keys are compared ordinally, so
    /// letter case matters (comparing folders, synchronizing).</summary>
    public static string Of(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Normalize(NormalizationForm.FormC);
    }

    /// <summary>Whether two names are one on a macOS disk: the same in composed form, ignoring letter case (is the
    /// name taken, when renaming).</summary>
    public static bool SameOnMacDisk(string a, string b) =>
        string.Equals(Of(a), Of(b), StringComparison.OrdinalIgnoreCase);
}
