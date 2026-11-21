using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tandem.App.Resources;
using Tandem.App.Services;
using Tandem.Core.Tools.Rename;
using Tandem.Core.Vfs;

namespace Tandem.App.ViewModels.Tools;

public sealed record RenamePreviewRow(string OldName, string NewName, RenameStatus Status)
{
    public string StatusText => Loc.Enum(Status);
    public bool HasError => Status is RenameStatus.InvalidName or RenameStatus.Duplicate or RenameStatus.NameTaken;
}

public sealed record RenameCaseOption(CaseChange Value)
{
    public string Label => Loc.Enum(Value);
}

public sealed partial class MultiRenameViewModel : ObservableObject, IDialogModel<RenamePlan>, IDisposable
{
    private const int ImmediatePreviewLimit = 2000;
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(150);

    private readonly IReadOnlyList<FileEntry> _targets;
    private readonly IReadOnlyDictionary<VfsPath, IReadOnlySet<string>> _namesByFolder;
    private readonly TimeZoneInfo _zone;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _preview;
    private RenamePlan? _plan;
    private bool _initializing = true;
    private bool _disposed;

    [ObservableProperty] public partial string NameTemplate { get; set; }
    [ObservableProperty] public partial string ExtensionTemplate { get; set; }
    [ObservableProperty] public partial string Search { get; set; }
    [ObservableProperty] public partial string Replace { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial CaseChange Case { get; set; }
    [ObservableProperty] public partial int CounterStart { get; set; }
    [ObservableProperty] public partial int CounterStep { get; set; }
    [ObservableProperty] public partial int CounterDigits { get; set; }
    [ObservableProperty] public partial IReadOnlyList<RenamePreviewRow> Rows { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; }
    [ObservableProperty] public partial string? RuleError { get; private set; }
    [ObservableProperty] public partial bool HasRuleError { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
    public partial bool CanAccept { get; private set; }

    public IReadOnlyList<RenameCaseOption> CaseOptions { get; } =
        Enum.GetValues<CaseChange>().Select(value => new RenameCaseOption(value)).ToArray();

    public RenameCaseOption SelectedCase
    {
        get => CaseOptions[(int)Case];
        set
        {
            if (value is not null) Case = value.Value;
        }
    }

    public MultiRenameViewModel(IReadOnlyList<FileEntry> targets,
        IReadOnlyDictionary<VfsPath, IReadOnlySet<string>> namesByFolder, TimeZoneInfo zone,
        Func<TimeSpan, CancellationToken, Task>? previewDelay = null)
    {
        _targets = targets;
        _namesByFolder = namesByFolder;
        _zone = zone;
        _delay = previewDelay ?? Task.Delay;
        NameTemplate = "[N]";
        ExtensionTemplate = "[E]";
        Search = "";
        Replace = "";
        CounterStart = CounterStep = CounterDigits = 1;
        Rows = [];
        Summary = "";
        _initializing = false;
        UpdatePreview();
    }

    public event EventHandler<RenamePlan?>? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanAccept))]
    private void Accept()
    {
        if (_plan is { CanRun: true }) CloseRequested?.Invoke(this, _plan);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    partial void OnNameTemplateChanged(string value) => UpdatePreview();
    partial void OnExtensionTemplateChanged(string value) => UpdatePreview();
    partial void OnSearchChanged(string value) => UpdatePreview();
    partial void OnReplaceChanged(string value) => UpdatePreview();
    partial void OnUseRegexChanged(bool value) => UpdatePreview();
    partial void OnMatchCaseChanged(bool value) => UpdatePreview();
    partial void OnCounterStartChanged(int value) => UpdatePreview();
    partial void OnCounterStepChanged(int value) => UpdatePreview();
    partial void OnCounterDigitsChanged(int value) => UpdatePreview();

    partial void OnCaseChanged(CaseChange value)
    {
        OnPropertyChanged(nameof(SelectedCase));
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_disposed || _initializing) return;
        _preview?.Cancel();
        var rule = new RenameRule(NameTemplate ?? "", ExtensionTemplate ?? "", Search ?? "", Replace ?? "",
            UseRegex, MatchCase, Case, CounterStart, CounterStep, CounterDigits);
        if (_targets.Count <= ImmediatePreviewLimit)
        {
            try { SetPreview(Build(rule), null); }
            catch (RenameRuleException e) { SetPreview(null, ErrorText(e)); }
            return;
        }

        _plan = null;
        RuleError = null;
        HasRuleError = false;
        CanAccept = false;
        Summary = Loc.Get("Rename_Computing");
        var request = new CancellationTokenSource();
        _preview = request;
        _ = UpdateLaterAsync(rule, request);
    }

    private async Task UpdateLaterAsync(RenameRule rule, CancellationTokenSource request)
    {
        try
        {
            await _delay(PreviewDelay, request.Token);
            var preview = await Task.Run(() => Build(rule), request.Token);
            if (_preview == request && !_disposed) SetPreview(preview, null);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
        }
        catch (RenameRuleException e)
        {
            if (_preview == request && !_disposed) SetPreview(null, ErrorText(e));
        }
        finally
        {
            if (_preview == request) _preview = null;
            request.Dispose();
        }
    }

    private (RenamePlan Plan, RenamePreviewRow[] Rows) Build(RenameRule rule)
    {
        var plan = RenamePlanner.Build(_targets, rule, _namesByFolder, _zone);
        var rows = plan.Items.Select(item => new RenamePreviewRow(item.Entry.Name, item.NewName, item.Status)).ToArray();
        return (plan, rows);
    }

    private void SetPreview((RenamePlan Plan, RenamePreviewRow[] Rows)? preview, string? error)
    {
        _plan = preview?.Plan;
        Rows = preview?.Rows ?? [];
        RuleError = error;
        HasRuleError = error is not null;
        Summary = _plan is { } plan
            ? Loc.Format("Rename_Summary", plan.ChangedCount, _targets.Count, Rows.Count(row => row.HasError))
            : "";
        CanAccept = _plan?.CanRun == true;
    }

    private static string ErrorText(RenameRuleException error) => error.Position >= 0
        ? Loc.Format("Rename_TemplateError", error.Position + 1)
        : Loc.Get("Rename_RuleError");

    public void Dispose()
    {
        _disposed = true;
        _preview?.Cancel();
    }
}
