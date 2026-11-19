using System.Globalization;
using System.Text;

namespace Tandem.App.Services;

/// <summary>Appends unexpected errors to a log file (<c>~/Library/Logs/Tandem/tandem.log</c> in the app). Writing
/// never throws: a log that cannot be written is skipped. Safe to call from any thread.</summary>
public sealed class ErrorLog(string file)
{
    private readonly Lock _gate = new();

    public string File { get; } = file;

    public void Write(Exception error, string? context = null)
    {
        try
        {
            var text = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .Append(context is null ? "" : " [" + context + "]")
                .Append(' ')
                .AppendLine(error.ToString())
                .ToString();
            lock (_gate)
            {
                if (Path.GetDirectoryName(File) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
                System.IO.File.AppendAllText(File, text);
            }
        }
        catch (Exception)
        {
            // The log is the last resort; there is nowhere left to report its own failure.
        }
    }
}
