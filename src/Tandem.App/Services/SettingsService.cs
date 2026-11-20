using Tandem.Core.Settings;

namespace Tandem.App.Services;

/// <summary>
/// The user's settings for the session: <see cref="Current"/> is what the app shows and uses, kept in
/// <c>settings.json</c> through the store. <see cref="Load"/> reads the file once at start (off the UI thread, before
/// anything shows the settings); after that <see cref="Update"/> is called on the UI thread.
/// </summary>
/// <param name="saveFailed">Told when writing the file fails (the new settings stay in use for the session).</param>
public sealed class SettingsService(JsonSettingsStore store, Action<Exception>? saveFailed = null)
{
    private readonly JsonSettingsStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The settings in use; <see cref="AppSettings.Default"/> until <see cref="Load"/>.</summary>
    public AppSettings Current { get; private set; } = AppSettings.Default;

    /// <summary>Raised by <see cref="Update"/>, on the thread that called it (the UI thread), with
    /// <see cref="Current"/> already the new settings. <see cref="Load"/> does not raise it.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised by <see cref="Update"/> when writing the file fails, after <c>saveFailed</c> was told; the new
    /// settings stay current. For telling the user.</summary>
    public event EventHandler<Exception>? SaveFailed;

    /// <summary>Reads the file; never throws (see <see cref="JsonSettingsStore.Load"/>).</summary>
    public void Load() => Current = _store.Load();

    /// <summary>Makes <paramref name="settings"/>, normalized, the current settings, raises <see cref="Changed"/> and
    /// saves them, even when a <see cref="Changed"/> handler throws (its exception then propagates after the save). A
    /// failed save is passed to <c>saveFailed</c> and <see cref="SaveFailed"/>, not thrown.</summary>
    public void Update(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalized();
        Current = normalized;
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Save(normalized);
        }
    }

    private void Save(AppSettings settings)
    {
        try
        {
            _store.Save(settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            saveFailed?.Invoke(e);
            SaveFailed?.Invoke(this, e);
        }
    }
}
