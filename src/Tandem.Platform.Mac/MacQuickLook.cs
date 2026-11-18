using Tandem.Core.Platform;

namespace Tandem.Platform.Mac;

/// <summary>Quick Look as a <c>/usr/bin/qlmanage -p</c> process: the preview is open while the process runs.
/// (<c>qlmanage</c> is not available in the App Sandbox; it is replaced in stage 4 behind the same interface.)</summary>
public sealed class MacQuickLook(IProcessRunner runner) : IQuickLook
{
    private const string QlManage = "/usr/bin/qlmanage";

    private readonly IProcessRunner _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    private readonly Lock _lock = new();
    private IRunningProcess? _preview;

    public bool IsOpen
    {
        get { lock (_lock) return _preview is not null; }
    }

    public void Toggle(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_lock)
        {
            if (_preview is not null)
            {
                CloseLocked();
                return;
            }

            if (paths.Count == 0) return;
            foreach (var path in paths) ProcessLaunch.RequireAbsolute(path, nameof(paths));

            var preview = ProcessLaunch.Start(_runner, new ProcessSpec(QlManage, ["-p", .. paths]));
            _preview = preview;
            ProcessLaunch.ReleaseWhenExited(preview, () => Forget(preview));
        }
    }

    public void Close()
    {
        lock (_lock) CloseLocked();
    }

    /// <summary>Kills the preview; the background release disposes it once it has exited.</summary>
    private void CloseLocked()
    {
        var preview = _preview;
        _preview = null;
        preview?.Kill();
    }

    /// <summary>Marks the preview closed if <paramref name="preview"/> is still the current one.</summary>
    private void Forget(IRunningProcess preview)
    {
        lock (_lock)
        {
            if (ReferenceEquals(_preview, preview)) _preview = null;
        }
    }
}
