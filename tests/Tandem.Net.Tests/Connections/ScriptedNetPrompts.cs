using Tandem.Net.Connections;

namespace Tandem.Net.Tests.Connections;

internal sealed class ScriptedNetPrompts : INetPrompts
{
    public Queue<SecretAnswer?> Secrets { get; } = new();
    public List<(SecretKind Kind, bool PreviousWasWrong)> SecretQuestions { get; } = [];
    public List<HostKeyInfo?> PreviousHostKeys { get; } = [];
    public List<CertificateInfo> Certificates { get; } = [];
    public bool ConfirmKeys { get; set; }
    public bool ConfirmCertificates { get; set; }

    public Task<SecretAnswer?> AskSecretAsync(ConnectionProfile profile, SecretKind kind, bool previousWasWrong,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SecretQuestions.Add((kind, previousWasWrong));
        return Task.FromResult(Secrets.Count == 0 ? null : Secrets.Dequeue());
    }

    public Task<bool> ConfirmHostKeyAsync(ConnectionProfile profile, HostKeyInfo key, HostKeyInfo? previous,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        PreviousHostKeys.Add(previous);
        return Task.FromResult(ConfirmKeys);
    }

    public Task<bool> ConfirmCertificateAsync(ConnectionProfile profile, CertificateInfo certificate, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Certificates.Add(certificate);
        return Task.FromResult(ConfirmCertificates);
    }
}
