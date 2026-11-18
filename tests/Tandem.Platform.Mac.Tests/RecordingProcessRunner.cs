using Tandem.Core.Platform;

namespace Tandem.Platform.Mac.Tests;

/// <summary>An <see cref="IProcessRunner"/> that starts nothing: it records each spec and hands back a process the
/// test ends itself, with <see cref="FakeProcess.Exit"/>, or through <see cref="IRunningProcess.Kill"/>.</summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly Lock _lock = new();
    private readonly List<FakeProcess> _started = [];

    /// <summary>When set, <see cref="Start"/> throws it instead of starting anything.</summary>
    public Exception? StartFailure { get; set; }

    public IReadOnlyList<FakeProcess> Started
    {
        get { lock (_lock) return [.. _started]; }
    }

    public IReadOnlyList<ProcessSpec> Specs => [.. Started.Select(p => p.Spec)];

    public IRunningProcess Start(ProcessSpec spec)
    {
        if (StartFailure is { } failure) throw failure;
        var process = new FakeProcess(spec);
        lock (_lock) _started.Add(process);
        return process;
    }

    public sealed class FakeProcess(ProcessSpec spec) : IRunningProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _kills;
        private int _disposals;

        public ProcessSpec Spec { get; } = spec;

        public int Kills => Volatile.Read(ref _kills);

        public int Disposals => Volatile.Read(ref _disposals);

        public bool HasExited => _exit.Task.IsCompleted;

        public IAsyncEnumerable<string> OutputLines => Lines();

        public Task<int> ExitCode => _exit.Task;

        /// <summary>Ends the process as if it exited by itself.</summary>
        public void Exit(int code = 0) => _exit.TrySetResult(code);

        public void Kill()
        {
            Interlocked.Increment(ref _kills);
            _exit.TrySetResult(137);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            _exit.TrySetResult(137);
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<string> Lines()
        {
            await _exit.Task;
            yield break;
        }
    }
}
