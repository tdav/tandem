using Tandem.Core.Operations;
using Tandem.Core.Tests.Operations;
using Tandem.Core.Tools.Checksums;
using Tandem.Core.Vfs;
using Tandem.Core.Vfs.InMemory;
using Tandem.Core.Viewing;

namespace Tandem.Core.Tests.Tools.Checksums;

public sealed class ChecksumOperationTests
{
    [Fact]
    public async Task Creates_relative_paths_and_verifies_matches_missing_and_mismatches()
    {
        var (fs, registry, path) = Make();
        fs.AddFile("/folder/sub/a.txt", "alpha");
        fs.AddFile("/folder/sub/b.txt", "bravo");
        fs.AddFile("/folder/extra.txt", "charlie");
        var create = new ChecksumCreateOperation(registry, [path.Parent!.Value.Combine("sub"), path.Parent.Value.Combine("extra.txt")],
            path, ChecksumAlgorithm.Sha256, new ScriptedInteraction());
        await create.RunAsync();

        Assert.Equal(["sub/a.txt", "sub/b.txt", "extra.txt"],
            ChecksumFile.Parse(ChecksumAlgorithm.Sha256, fs.ReadAllText(path.Path)).Lines.Select(line => line.RelativePath));

        var ct = TestContext.Current.CancellationToken;
        await fs.DeleteAsync(path.Parent.Value.Combine("extra.txt"), false, ct);
        fs.AddFile("/folder/sub/b.txt", "corrupted");
        var verify = new ChecksumVerifyOperation(registry, path, new ScriptedInteraction());
        await verify.RunAsync();
        Assert.Equal([VerifyStatus.Ok, VerifyStatus.Mismatch, VerifyStatus.Missing], verify.Results.Select(row => row.Status));
    }

    [Fact]
    public async Task Outside_entries_and_link_targets_are_never_read()
    {
        var (fs, registry, path) = Make();
        var ct = TestContext.Current.CancellationToken;
        const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        fs.AddFile("/elsewhere/secret.txt", "abc");
        await fs.CreateSymlinkAsync(new("mem", "/folder/link"), "/elsewhere", ct);
        fs.AddFile(path.Path, $"{hash} *../elsewhere/secret.txt\n{hash} */elsewhere/secret.txt\n{hash} *link/secret.txt\n");
        fs.FailOn((operation, item) => operation == "OpenRead" && item.Name == "secret.txt",
            () => new InvalidOperationException("Outside read"));
        var verify = new ChecksumVerifyOperation(registry, path, new ScriptedInteraction());
        await verify.RunAsync();
        Assert.Equal(3, verify.Results.Count);
        Assert.All(verify.Results, row => Assert.Equal(VerifyStatus.OutsideFolder, row.Status));
    }

    [Fact]
    public async Task Accepts_windows_1251_names()
    {
        var (fs, registry, path) = Make();
        fs.AddFile("/folder/данные", "abc");
        const string hash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        fs.AddFile(path.Path, TextEncodings.Windows1251.Encoding.GetBytes($"{hash} *данные\n"));
        var verify = new ChecksumVerifyOperation(registry, path, new ScriptedInteraction());
        await verify.RunAsync();
        Assert.Equal(VerifyStatus.Ok, Assert.Single(verify.Results).Status);
    }

    [Fact]
    public async Task Existing_file_rejects_and_preserves_contents()
    {
        var (fs, registry, path) = Make();
        fs.AddFile(path.Path, "existing");
        var create = new ChecksumCreateOperation(registry, [], path, ChecksumAlgorithm.Sha256, new ScriptedInteraction());
        var error = await Assert.ThrowsAsync<OperationRejectedException>(() => create.RunAsync());
        Assert.Equal(RejectionReason.DestinationExists, error.Reason);
        Assert.Equal("existing", fs.ReadAllText(path.Path));
    }

    [Fact]
    public async Task Cancelled_create_does_not_leave_a_checksum_file()
    {
        var (fs, registry, path) = Make();
        fs.AddFile("/folder/a.txt", "abc");
        var create = new ChecksumCreateOperation(registry, [new("mem", "/folder/a.txt")], path, ChecksumAlgorithm.Sha256,
            new ScriptedInteraction());
        fs.FailOn((operation, item) =>
        {
            if (operation == "OpenRead" && item.Name == "a.txt") create.Cancel();
            return false;
        }, () => new IOException());
        var result = await create.RunAsync();
        Assert.Equal(OperationState.Cancelled, result.State);
        Assert.Equal(["/folder", "/folder/a.txt"], fs.AllPaths);
    }

    private static (InMemoryFileSystem Fs, ProviderRegistry Registry, VfsPath Path) Make()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/folder");
        var registry = new ProviderRegistry();
        registry.Register(fs);
        return (fs, registry, new("mem", "/folder/check.sha256"));
    }
}
