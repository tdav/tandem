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
        private readonly Task _completion;
        private int _openStreams = 2;
        private int _disposed;

        private RunningProcess(Process process)
        {
            _process = process;
            _completion = Task.WhenAll(_exit.Task, _streamsDrained.Task)
                .ContinueWith(_ => _lines.Writer.TryComplete(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public IAsyncEnumerable<string> OutputLines => _lines.Reader.ReadAllAsync();

        public Task<int> ExitCode => _exit.Task;

        /// <summary>UTF-8 on macOS; on Windows the console's OEM code page (CP866 for Russian), which cmd.exe and
        /// most console programs write when their output goes to a pipe.</summary>
        private static readonly Encoding OutputEncoding = ChooseOutputEncoding();

        private static Encoding ChooseOutputEncoding()
        {
            if (!OperatingSystem.IsWindows()) return Encoding.UTF8;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                return Encoding.UTF8;
            }
        }

        public static RunningProcess Start(ProcessSpec spec)
        {
            var info = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = OutputEncoding,
                StandardErrorEncoding = OutputEncoding,
            };
            if (spec.CommandLine is { } commandLine) info.Arguments = commandLine;
            else foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);
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

        /// <remarks>Never called into <see cref="Process"/> under a lock of ours: <see cref="Process"/> raises
        /// <c>Exited</c> while holding its own monitor, and <c>Dispose</c> and others take that monitor too.</remarks>
        public void Kill()
        {
            if (Volatile.Read(ref _disposed) != 0 || _exit.Task.IsCompleted) return;
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited in the meantime, or was disposed concurrently (ObjectDisposedException is one too).
            }
            catch (Win32Exception)
            {
                // It, or one of its descendants, is already gone.
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

            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _process.Exited -= OnExited;
            _process.Dispose();

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

        /// <remarks>Runs on a thread-pool thread (under the <see cref="Process"/>'s own monitor), where an exception
        /// would end the process; so a <see cref="Process"/> already disposed (after a kill whose output did not drain
        /// in time) reports -1 instead of throwing. It takes no lock.</remarks>
        private void OnExited(object? sender, EventArgs e)
        {
            int code;
            try
            {
                code = _process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // Disposed, or no exit code available (ObjectDisposedException is an InvalidOperationException too).
                code = -1;
            }

            _exit.TrySetResult(code);
        }
    }
}
