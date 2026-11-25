using System.Globalization;
using System.Text.Json;
using Tandem.Core.Settings;

namespace Tandem.App.Services;

/// <summary>
/// Keeps where the floating output window was (position and size) in <c>output-window.json</c> beside
/// <c>state.json</c>. Kept apart from the app state and the settings on purpose. Neither <see cref="Load"/> nor
/// <see cref="Save"/> throws: a missing or damaged file is "no placement", and a failed save is dropped (the
/// placement is a convenience).
/// </summary>
public sealed class OutputWindowPlacementStore(string directory)
{
    private const string FileName = "output-window.json";

    /// <summary>The saved bounds (the split ratio is unused); null when none are saved or the file is damaged.</summary>
    public WindowState? Load()
    {
        try
        {
            var file = Path.Combine(directory, FileName);
            if (!File.Exists(file)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            return new WindowState(root.GetProperty("x").GetDouble(), root.GetProperty("y").GetDouble(),
                root.GetProperty("width").GetDouble(), root.GetProperty("height").GetDouble(), false, 0.5);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }

    public void Save(WindowState bounds)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var c = CultureInfo.InvariantCulture;
            File.WriteAllText(Path.Combine(directory, FileName),
                $"{{\"x\":{bounds.X.ToString("R", c)},\"y\":{bounds.Y.ToString("R", c)}," +
                $"\"width\":{bounds.Width.ToString("R", c)},\"height\":{bounds.Height.ToString("R", c)}}}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A lost placement only means the window opens at its default place next time.
        }
    }
}
