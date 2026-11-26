using CommunityToolkit.Mvvm.ComponentModel;
using DevDoctor.Core;

namespace DevDoctor.App.ViewModels;

public sealed class ModuleViewModel(ModuleDescriptor descriptor, IReportRedactor redactor) : ObservableObject
{
    private bool _isChecking;
    private bool _isSelected;
    private bool _redactPaths = true;
    public string Id => descriptor.Id;
    public string Name => descriptor.Name;
    public string Description => descriptor.Description;
    public string Monogram => descriptor.Monogram;
    public DiagnosticResult? Result { get; private set; }
    public bool IsChecking { get => _isChecking; set { if (SetProperty(ref _isChecking, value)) NotifyDisplay(); } }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    public bool RedactPaths { get => _redactPaths; set { if (SetProperty(ref _redactPaths, value)) { RenderFindings(); NotifyDisplay(); } } }
    public DiagnosticStatus? Status => IsChecking ? null : Result?.Status;
    public string StatusText => IsChecking ? "检查中" : Result?.Status.ToDisplayText() ?? "待检测";
    public string VersionText => Result?.Version is { } version ? "v" + version : Result is null ? "等待检测" : "版本未确认";
    public string Summary => IsChecking ? "正在读取版本、命令路径与相关环境信息…" : Display(Result?.Summary ?? Description);
    public string PathText => Display(Result?.ExecutablePath ?? "尚无受检路径");
    public bool HasResult => Result is not null;
    public string CheckedAtText => Result is null ? "尚未检测" : $"检测于 {Result.StartedAt:HH:mm:ss} · {Result.Duration.TotalSeconds:0.0} 秒";
    public IReadOnlyList<FindingViewModel> Findings { get; private set; } = [];
    public void Apply(DiagnosticResult result) { Result = result; IsChecking = false; RenderFindings(); NotifyDisplay(); }
    private void RenderFindings() { Findings = Result?.Findings.Select(f => new FindingViewModel(Id, Name, f, redactor, RedactPaths)).ToArray() ?? []; OnPropertyChanged(nameof(Findings)); }
    private string Display(string text) => RedactPaths ? redactor.Redact(text) : redactor.RedactSecrets(text);
    private void NotifyDisplay()
    {
        foreach (var property in new[] { nameof(Result), nameof(Status), nameof(StatusText), nameof(VersionText), nameof(Summary), nameof(PathText), nameof(HasResult), nameof(CheckedAtText) }) OnPropertyChanged(property);
    }
}

public sealed class FindingViewModel : IGuidedFinding
{
    public FindingViewModel(string moduleId, string moduleName, DiagnosticFinding finding, IReportRedactor redactor, bool redact)
    {
        string Display(string text) => redact ? redactor.Redact(text) : redactor.RedactSecrets(text);
        ModuleId = moduleId; ModuleName = moduleName; Code = finding.Code; Title = UiText.FindingTitle(finding.Title); Status = finding.Status;
        Observed = Display(finding.Observed); Basis = Display(finding.Basis);
        PossibleCause = Display(string.IsNullOrWhiteSpace(finding.PossibleCause) ? "没有已确认的问题原因；以所列证据为准。" : finding.PossibleCause);
        Suggestions = string.Join('\n', finding.Suggestions.Select((text, i) => $"{i + 1}. {Display(text)}"));
        if (string.IsNullOrEmpty(Suggestions)) Suggestions = "无需调整；如需核对，可重新检测此模块。";
        var guide = DiagnosticGuidance.Explain(finding);
        Guide = guide with { Impact = Display(guide.Impact), Confirmation = Display(guide.Confirmation),
            Steps = guide.Steps.Select(Display).ToArray(), Risk = Display(guide.Risk), Recovery = Display(guide.Recovery), Retest = Display(guide.Retest) };
    }
    public string ModuleId { get; }
    public string ModuleName { get; }
    public string Code { get; }
    public string Title { get; }
    public DiagnosticStatus Status { get; }
    public string StatusText => Status.ToDisplayText();
    public bool IsProblem => Status is DiagnosticStatus.Warning or DiagnosticStatus.Error or DiagnosticStatus.Unknown or DiagnosticStatus.NotInstalled;
    public string Observed { get; }
    public string Basis { get; }
    public string PossibleCause { get; }
    public string Suggestions { get; }
    public FindingGuide Guide { get; }
}
