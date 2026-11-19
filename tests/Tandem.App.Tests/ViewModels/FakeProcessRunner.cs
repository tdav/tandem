using System.Threading.Channels;
using Tandem.Core.Platform;

namespace Tandem.App.Tests.ViewModels;

/// <summary>Starts no real process: records each spec and hands out a <see cref="FakeProcess"/> the test scripts.
/// With <see cref="StartFailure"/> set, <see cref="Start"/> throws it, as the real runner does for a missing
/// executable.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Lock _lock = new();
    private readonly List<ProcessSpec> _started = [];
    private readonly List<FakeProcess> _processes = [];

    public Exception? StartFailure { get; set; }

    public IReadOnlyList<ProcessSpec> Started
    {
        get { lock (_lock) return _started.ToArray(); }
    }

    public IReadOnlyList<FakeProcess> Processes
    {
        get { lock (_lock) return _processes.ToArray(); }
    }

    /// <summary>The process started last.</summary>
    public FakeProcess Last => Processes[^1];

    public IRunningProcess Start(ProcessSpec spec)
    {
        if (StartFailure is { } failure) throw failure;
        var process = new FakeProcess();
        lock (_lock)
        {
            _started.Add(spec);
            _processes.Add(process);
        }
        return process;
    }
}

/// <summary>A scripted process: the test writes its output lines and ends it with <see cref="Exit"/>. A kill ends it
/// with code 137, as SIGKILL would.</summary>
internal sealed class FakeProcess : IRunningProcess
{
    public const int KilledExitCode = 137;

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _kills;
    private int _disposals;

    public IAsyncEnumerable<string> OutputLines => _lines.Reader.ReadAllAsync();

    public Task<int> ExitCode => _exit.Task;

    public int Kills => Volatile.Read(ref _kills);

    public bool IsDisposed => Volatile.Read(ref _disposals) > 0;

    public bool HasExited => _exit.Task.IsCompleted;

    public void Write(params string[] lines)
    {
        foreach (var line in lines) _lines.Writer.TryWrite(line);
    }

    public void Exit(int code)
    {
        _lines.Writer.TryComplete();
        _exit.TrySetResult(code);
    }

    public void Kill()
    {
        Interlocked.Increment(ref _kills);
        Exit(KilledExitCode);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        Exit(KilledExitCode);
        return ValueTask.CompletedTask;
    }
}
