using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Tandem.Core.Platform;

namespace Tandem.Platform.Mac;

/// <summary>Starts processes directly (never through a shell), with standard output and standard error merged into
/// one stream of lines and standard input closed.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <exception cref="Win32Exception">The executable could not be started (for example, it does not exist).</exception>
    public IRunningProcess Start(ProcessSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(spec.FileName);
        return RunningProcess.Start(spec);
    }

    private sealed class RunningProcess : IRunningProcess
    {
        /// <summary>How long disposal waits for a killed process's output to drain before letting go of it.</summary>
        private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

        private readonly Process _process;
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
        });
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _streamsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Lock _lock = new();
        private readonly Task _completion;
        private int _openStreams = 2;
        private bool _disposed;

        private RunningProcess(Process process)
        {
            _process = process;
            _completion = Task.WhenAll(_exit.Task, _streamsDrained.Task)
                .ContinueWith(_ => _lines.Writer.TryComplete(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public IAsyncEnumerable<string> OutputLines => _lines.Reader.ReadAllAsync();

        public Task<int> ExitCode => _exit.Task;

        public static RunningProcess Start(ProcessSpec spec)
        {
            var info = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
            if (spec.WorkingDirectory is not null) info.WorkingDirectory = spec.WorkingDirectory;

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var running = new RunningProcess(process);
            process.OutputDataReceived += running.OnLine;
            process.ErrorDataReceived += running.OnLine;
            process.Exited += running.OnExited;
            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                throw;
            }

            process.StandardInput.Close();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return running;
        }

        public void Kill()
        {
            lock (_lock)
            {
                if (_disposed || _exit.Task.IsCompleted) return;
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited in the meantime.
                }
                catch (Win32Exception)
                {
                    // It, or one of its descendants, is already gone.
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                await _completion.WaitAsync(DrainTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // An escaped descendant still holds the output open; stop waiting for it.
            }

            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _process.Dispose();
            }

            _exit.TrySetResult(-1);
            _lines.Writer.TryComplete();
        }

        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
                _lines.Writer.TryWrite(e.Data);
            else if (Interlocked.Decrement(ref _openStreams) == 0)
                _streamsDrained.TrySetResult();
        }

        private void OnExited(object? sender, EventArgs e) => _exit.TrySetResult(_process.ExitCode);
    }
}
