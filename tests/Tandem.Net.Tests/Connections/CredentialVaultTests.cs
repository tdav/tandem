using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tandem.Core.Tests.Vfs;
using Tandem.Net.Connections;

namespace Tandem.Net.Tests.Connections;

[SupportedOSPlatform("macos")]
public sealed class CredentialVaultTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string MasterPassword = "синтетический пароль";
    private const string OtherPassword = "другой пароль";
    private static readonly ConnectionProfile Profile = new(
        Guid.Parse("8d844605-6de9-45e2-97a8-d572c8f24f54"), "Тест", NetProtocol.Sftp,
        "2001:db8::1", 2200, "пользователь", NetAuth.PrivateKey, "/tmp/ключ", "/данные", false, "windows-1251");

    [Fact]
    public async Task Writes_and_reads_the_portable_format()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        Assert.False(vault.IsConfigured);
        Assert.False(vault.IsUnlocked);
        await vault.CreateAsync(MasterPassword,
        [
            new VaultEntry(Profile, "", [0, 255, 128], "фраза"),
            new VaultEntry(Profile with { Id = Guid.NewGuid(), Protocol = NetProtocol.FtpsExplicit, Auth = NetAuth.Password },
                "секрет", null, null),
            new VaultEntry(Profile with { Id = Guid.NewGuid(), Protocol = NetProtocol.Ssh },
                null, [1, 2, 3], "ssh phrase"),
        ], Ct);

        Assert.True(vault.IsConfigured);
        Assert.True(vault.IsUnlocked);
        var file = await File.ReadAllBytesAsync(directory.Combine(CredentialVault.FileName), Ct);
        Assert.Equal("TANDVLT1"u8.ToArray(), file[..8]);
        var key = Rfc2898DeriveBytes.Pbkdf2(MasterPassword, file.AsSpan(8, 16), 600_000, HashAlgorithmName.SHA256, 32);
        using var cipher = new AesGcm(key, 16);
        var plaintext = new byte[file.Length - 52];
        cipher.Decrypt(file.AsSpan(24, 12), file.AsSpan(52), file.AsSpan(36, 16), plaintext, file.AsSpan(0, 36));
        using var json = JsonDocument.Parse(plaintext);
        var records = json.RootElement.GetProperty("entries");
        var first = records[0];
        Assert.Equal(new[] { "passphrase", "password", "privateKey", "profile" },
            first.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("", first.GetProperty("password").GetString());
        Assert.Equal("AP+A", first.GetProperty("privateKey").GetString());
        Assert.Equal("фраза", first.GetProperty("passphrase").GetString());
        var profile = first.GetProperty("profile");
        Assert.Equal(new[] { "auth", "host", "id", "initialPath", "name", "nameEncoding", "passiveMode",
                "port", "privateKeyPath", "protocol", "user" },
            profile.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal("sftp", profile.GetProperty("protocol").GetString());
        Assert.Equal("privateKey", profile.GetProperty("auth").GetString());
        Assert.Equal("ftpsExplicit", records[1].GetProperty("profile").GetProperty("protocol").GetString());
        Assert.Equal(JsonValueKind.Null, records[1].GetProperty("privateKey").ValueKind);
        Assert.Equal("ssh", records[2].GetProperty("profile").GetProperty("protocol").GetString());

        using var reopened = new CredentialVault(directory.Path);
        await reopened.UnlockAsync(MasterPassword, Ct);
        var entries = await reopened.ReadAsync(Ct);
        Assert.Equal(Profile, entries[0].Profile);
        Assert.Equal("", entries[0].Password);
        Assert.Equal(new byte[] { 0, 255, 128 }, entries[0].PrivateKey);
        Assert.Equal("фраза", entries[0].Passphrase);
        Assert.Equal("секрет", entries[1].Password);
        Assert.Null(entries[1].PrivateKey);
        Assert.Null(entries[1].Passphrase);
        Assert.Equal(NetProtocol.Ssh, entries[2].Profile.Protocol);
        Assert.Equal("ssh phrase", entries[2].Passphrase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(24)]
    [InlineData(36)]
    [InlineData(52)]
    public async Task Rejects_tampering_with_the_same_error_as_a_wrong_password(int offset)
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await vault.CreateAsync(MasterPassword, [], Ct);
        var path = directory.Combine(CredentialVault.FileName);
        using var reopened = new CredentialVault(directory.Path);
        var wrongPassword = await Assert.ThrowsAsync<CredentialVaultException>(() => reopened.UnlockAsync(OtherPassword, Ct));
        var data = await File.ReadAllBytesAsync(path, Ct);
        data[offset] ^= 1;
        await File.WriteAllBytesAsync(path, data, Ct);

        var corrupt = await Assert.ThrowsAsync<CredentialVaultException>(() => reopened.UnlockAsync(MasterPassword, Ct));
        Assert.Equal(wrongPassword.Message, corrupt.Message);
        Assert.Null(corrupt.InnerException);
        Assert.False(reopened.IsUnlocked);
        await Assert.ThrowsAsync<CredentialVaultException>(() => reopened.ReadAsync(Ct));
        Assert.Equal(data, await File.ReadAllBytesAsync(path, Ct));
    }

    [Fact]
    public async Task Saves_only_owner_read_and_write_permissions_on_creation_and_replacement()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        var path = directory.Combine(CredentialVault.FileName);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await vault.CreateAsync(MasterPassword, [], Ct);
        Assert.Equal(mode, File.GetUnixFileMode(path));

        var original = await File.ReadAllBytesAsync(path, Ct);
        File.SetUnixFileMode(path, mode | UnixFileMode.GroupRead);
        await vault.UpsertAsync(new VaultEntry(Profile, null, null, null), Ct);
        Assert.Equal(mode, File.GetUnixFileMode(path));
        var updated = await File.ReadAllBytesAsync(path, Ct);
        Assert.Equal(original[8..24], updated[8..24]);
        Assert.NotEqual(original[24..36], updated[24..36]);
    }

    [Fact]
    public async Task Parallel_writes_on_one_vault_all_persist()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await vault.CreateAsync(MasterPassword, [], Ct);
        var profiles = Enumerable.Range(0, 12).Select(_ => Profile with { Id = Guid.NewGuid() }).ToArray();
        await Task.WhenAll(profiles.Select(profile => Task.Run(
            () => vault.UpsertAsync(new VaultEntry(profile, null, null, null), Ct), Ct)));
        await vault.RemoveAsync(profiles[0].Id, Ct);

        using var reopened = new CredentialVault(directory.Path);
        await reopened.UnlockAsync(MasterPassword, Ct);
        var entries = await reopened.ReadAsync(Ct);
        Assert.Equal(profiles.Skip(1).Select(profile => profile.Id).Order(), entries.Select(entry => entry.Profile.Id).Order());
    }

    [Fact]
    public async Task Parallel_creation_keeps_one_complete_vault()
    {
        using var directory = new TempDir();
        using var first = new CredentialVault(directory.Path);
        using var second = new CredentialVault(directory.Path);
        var attempts = new[] { first, second }.Select(vault => Task.Run(async () =>
            await Record.ExceptionAsync(() => vault.CreateAsync(MasterPassword, [], Ct)), Ct));
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is CredentialVaultException);
        Assert.Single(new[] { first, second }, vault => vault.IsUnlocked);

        using var reopened = new CredentialVault(directory.Path);
        await reopened.UnlockAsync(MasterPassword, Ct);
        Assert.Empty(await reopened.ReadAsync(Ct));
        await Assert.ThrowsAsync<CredentialVaultException>(() => reopened.CreateAsync(OtherPassword, [], Ct));
        Assert.True(reopened.IsUnlocked);
    }

    [Fact]
    public async Task Parallel_writes_from_two_unlocked_instances_preserve_the_winner()
    {
        using var directory = new TempDir();
        using var first = new CredentialVault(directory.Path);
        using var second = new CredentialVault(directory.Path);
        await first.CreateAsync(MasterPassword, [], Ct);
        await second.UnlockAsync(MasterPassword, Ct);

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var attempts = new[] { first, second }.Select((vault, index) => Task.Run(async () =>
            await Record.ExceptionAsync(() => vault.UpsertAsync(
                new VaultEntry(Profile with { Id = ids[index] }, null, null, null), Ct)), Ct));
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, result => result is null);
        Assert.Single(results, result => result is CredentialVaultException);
        using var reopened = new CredentialVault(directory.Path);
        await reopened.UnlockAsync(MasterPassword, Ct);
        Assert.Equal(ids[Array.IndexOf(results, null)], Assert.Single(await reopened.ReadAsync(Ct)).Profile.Id);
    }

    [Fact]
    public async Task Refuses_to_recreate_a_vault_deleted_after_unlock()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await vault.CreateAsync(MasterPassword, [new VaultEntry(Profile, "original", null, null)], Ct);
        var path = directory.Combine(CredentialVault.FileName);
        File.Delete(path);

        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault.UpsertAsync(new VaultEntry(Profile, "change", null, null), Ct));
        Assert.False(File.Exists(path));
        Assert.Equal("original", Assert.Single(await vault.ReadAsync(Ct)).Password);
    }

    [Fact]
    public async Task Refuses_to_overwrite_a_same_length_external_change_with_unchanged_timestamp()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await vault.CreateAsync(MasterPassword, [new VaultEntry(Profile, "original", null, null)], Ct);
        var path = directory.Combine(CredentialVault.FileName);
        var modified = File.GetLastWriteTimeUtc(path);
        var changed = await File.ReadAllBytesAsync(path, Ct);
        changed[^1] ^= 1;
        await File.WriteAllBytesAsync(path, changed, Ct);
        File.SetLastWriteTimeUtc(path, modified);

        await Assert.ThrowsAsync<CredentialVaultException>(() => vault.RemoveAsync(Profile.Id, Ct));
        Assert.Equal(changed, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal("original", Assert.Single(await vault.ReadAsync(Ct)).Password);
    }

    [Fact]
    public async Task Failed_save_leaves_disk_and_memory_untouched()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await vault.CreateAsync(MasterPassword, [new VaultEntry(Profile, "original", null, null)], Ct);
        var path = directory.Combine(CredentialVault.FileName);
        var originalFile = await File.ReadAllBytesAsync(path, Ct);
        var originalMode = File.GetUnixFileMode(directory.Path);
        File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAsync<CredentialVaultException>(() =>
                vault.UpsertAsync(new VaultEntry(Profile, "change", null, null), Ct));
        }
        finally
        {
            File.SetUnixFileMode(directory.Path, originalMode);
        }

        Assert.Equal(originalFile, await File.ReadAllBytesAsync(path, Ct));
        Assert.Equal("original", Assert.Single(await vault.ReadAsync(Ct)).Password);
    }

    [Fact]
    public async Task Copies_key_bytes_from_writers_and_for_each_reader()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        var original = new byte[] { 1, 2, 3 };
        await vault.CreateAsync(MasterPassword, [new VaultEntry(Profile, null, original, null)], Ct);
        original[0] = 99;
        var snapshot = Assert.Single(await vault.ReadAsync(Ct));
        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot.PrivateKey);
        snapshot.PrivateKey![1] = 99;

        Assert.Equal(Profile, Assert.Single(await vault.ReadProfilesAsync(Ct)));
        var found = Assert.IsType<VaultEntry>(await vault.FindAsync(Profile.Id, Ct));
        Assert.Equal(new byte[] { 1, 2, 3 }, found.PrivateKey);
        found.PrivateKey![2] = 99;
        Assert.Null(await vault.FindAsync(Guid.Empty, Ct));
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(await vault.ReadAsync(Ct)).PrivateKey);
    }

    [Fact]
    public async Task Rejects_oversized_files_and_keys()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await Assert.ThrowsAsync<CredentialVaultException>(() =>
            vault.CreateAsync(MasterPassword, [new VaultEntry(Profile, null, new byte[1_048_577], null)], Ct));
        Assert.False(vault.IsConfigured);

        var path = directory.Combine(CredentialVault.FileName);
        using (var file = File.Create(path))
            file.SetLength(32 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<CredentialVaultException>(() => vault.UnlockAsync(MasterPassword, Ct));
        Assert.False(vault.IsUnlocked);
    }

    [Fact]
    public async Task Reads_an_independently_produced_vault_containing_a_key_at_the_limit()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        var privateKey = new byte[1_048_576];
        RandomNumberGenerator.Fill(privateKey);
        await WritePortableFixtureAsync(directory.Combine(CredentialVault.FileName), Convert.ToBase64String(privateKey));

        await vault.UnlockAsync(MasterPassword, Ct);
        var entry = Assert.Single(await vault.ReadAsync(Ct));
        Assert.Equal(Profile, entry.Profile);
        Assert.Equal(privateKey, entry.PrivateKey);
        Assert.Null(entry.Password);
        Assert.Empty(entry.Passphrase!);
    }

    [Fact]
    public async Task Rejects_an_authenticated_escaped_key_whose_decoded_bytes_exceed_the_limit()
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        var oversized = @"\u0041" + Convert.ToBase64String(new byte[1_048_579])[1..];
        await WritePortableFixtureAsync(directory.Combine(CredentialVault.FileName), oversized);

        var error = await Assert.ThrowsAsync<CredentialVaultException>(() => vault.UnlockAsync(MasterPassword, Ct));
        Assert.Null(error.InnerException);
        Assert.False(vault.IsUnlocked);
    }

    [Theory]
    [InlineData("SFTP", false)]
    [InlineData("sftp", true)]
    public async Task Rejects_an_authenticated_record_that_does_not_match_the_v1_schema(string protocol, bool omitNameEncoding)
    {
        using var directory = new TempDir();
        using var vault = new CredentialVault(directory.Path);
        await WritePortableFixtureAsync(directory.Combine(CredentialVault.FileName), "AQID", protocol, omitNameEncoding);

        await Assert.ThrowsAsync<CredentialVaultException>(() => vault.UnlockAsync(MasterPassword, Ct));
        Assert.False(vault.IsUnlocked);
    }

    private static async Task WritePortableFixtureAsync(string path, string key, string protocol = "sftp", bool omitNameEncoding = false)
    {
        var nameEncoding = omitNameEncoding ? "" : """ "nameEncoding":"windows-1251", """;
        var payload = Encoding.UTF8.GetBytes($$$"""
            {"entries":[{"passphrase":"","privateKey":"{{{key}}}","password":null,"profile":{
                "user":"пользователь",{{{nameEncoding}}}"passiveMode":false,"initialPath":"/данные",
                "privateKeyPath":"/tmp/ключ","auth":"privateKey","port":2200,"host":"2001:db8::1","protocol":"{{{protocol}}}",
                "name":"Тест","id":"8d844605-6de9-45e2-97a8-d572c8f24f54"}}]}
            """);
        var file = new byte[52 + payload.Length];
        "TANDVLT1"u8.CopyTo(file);
        RandomNumberGenerator.Fill(file.AsSpan(8, 28));
        var encryptionKey = Rfc2898DeriveBytes.Pbkdf2(MasterPassword, file.AsSpan(8, 16), 600_000,
            HashAlgorithmName.SHA256, 32);
        using var cipher = new AesGcm(encryptionKey, 16);
        cipher.Encrypt(file.AsSpan(24, 12), payload, file.AsSpan(52), file.AsSpan(36, 16), file.AsSpan(0, 36));
        await File.WriteAllBytesAsync(path, file, Ct);
    }

    [Fact]
    public void Diagnostic_string_omits_profile_and_secrets()
    {
        var entry = new VaultEntry(Profile, "synthetic-password", [0, 255, 128], "synthetic-passphrase");
        Assert.Equal(nameof(VaultEntry), entry.ToString());
    }
}
