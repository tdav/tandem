namespace Tandem.Platform.Windows.Tests;

/// <summary>A fresh directory under the system temp path, deleted (after clearing read-only attributes) on
/// dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tandem-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Removed below or not at all; a test leftover in the temp directory is harmless.
            }
        }
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // As above.
        }
    }
}
