namespace Tandem.Net.Remote;

/// <summary>Logging in with credentials that may be wrong: asks again, up to <see cref="MaxAttempts"/> times.</summary>
public static class CredentialLoop
{
    public const int MaxAttempts = 3;

    /// <summary>Takes credentials from <paramref name="source"/> and calls <paramref name="attempt"/> with them; a
    /// <see cref="NetAuthenticationException"/> from <paramref name="attempt"/> asks again, with
    /// previousWasWrong. Success → <see cref="ICredentialSource.Accepted"/>. Any other error of
    /// <paramref name="attempt"/> ends the loop as it is.</summary>
    /// <exception cref="NetAuthenticationException">The user declined (<see cref="NetAuthenticationException.Declined"/>);
    /// or the server refused <see cref="MaxAttempts"/> times (the last refusal).</exception>
    public static async Task RunAsync(ConnectionProfile profile, ICredentialSource source,
        Func<NetCredentials, Task> attempt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(attempt);
        var previousWasWrong = false;
        for (var round = 1; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            var credentials = await source.GetAsync(profile, previousWasWrong, ct).ConfigureAwait(false)
                              ?? throw new NetAuthenticationException(
                                  $"No credentials were given for {profile.Address}.", declined: true);
            try
            {
                await attempt(credentials).ConfigureAwait(false);
            }
            catch (NetAuthenticationException e) when (!e.Declined && round < MaxAttempts)
            {
                previousWasWrong = true;
                continue;
            }
            source.Accepted(profile, credentials);
            return;
        }
    }
}
