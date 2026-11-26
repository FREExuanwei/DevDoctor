namespace DevDoctor.Core;

public enum ProjectKind { Maven, Gradle, Node, Python, CMake, Dotnet }
public enum RequirementKind { Tool, VersionRange, JavaTarget, JavaToolchain, DotnetSdk, DotnetTarget, Wrapper, Information, Unknown, Compiler }
public enum MatchState { Meets, Unmet, Risk, Unknown, NotApplicable }
public sealed record ProjectScanOptions
{
    public int MaxDepth { get; init; } = 6;
    public int MaxEntries { get; init; } = 10000;
    public int MaxFiles { get; init; } = 256;
    public int MaxFileBytes { get; init; } = 262144;
    public int MaxTotalBytes { get; init; } = 4194304;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
}
public sealed record ProjectFile(string FullPath, string RelativePath, string? Content, string? Error = null);
public sealed record ProjectSnapshot(string Root, IReadOnlyList<ProjectFile> Files, IReadOnlyList<string> Notes,
    int VisitedEntries, bool IsLimited, bool WasCancelled, DateTimeOffset StartedAt, TimeSpan Duration);
public sealed record ProjectProgress(string Stage, string CurrentTask, int VisitedEntries = 0, int ConfigFiles = 0);
public sealed record ProjectEvidence(string File, string Key, string Raw, int? Line = null)
{
    public string Display => $"{File}" + (Line is null ? "" : $":{Line}") + $"\n{Key} = {Raw}";
}
public sealed record ProjectRequirement(string Id, string Directory, ProjectKind ProjectKind, string Tool,
    RequirementKind Kind, string Value, ProjectEvidence Evidence, string? Policy = null)
{
    public bool? AllowPrerelease { get; init; }
    public string? Rule { get; init; }
    public string? Source { get; init; }
}
public sealed record ProjectManifest(string Directory, ProjectKind Kind, IReadOnlyList<string> ConfigFiles,
    IReadOnlyList<ProjectRequirement> Requirements);
public sealed record ProjectMatch(ProjectRequirement Requirement, MatchState State, string Observed, string Reason, IReadOnlyList<string> Suggestions)
{
    public DiagnosticStatus Status => State switch { MatchState.Meets => DiagnosticStatus.Normal, MatchState.Unmet => DiagnosticStatus.Error,
        MatchState.Risk => DiagnosticStatus.Warning, MatchState.NotApplicable => DiagnosticStatus.NotApplicable, _ => DiagnosticStatus.Unknown };
    public string StateText => State switch { MatchState.Meets => "已确认满足", MatchState.Unmet => "已确认不满足", MatchState.Risk => "潜在风险", MatchState.NotApplicable => "不适用", _ => "无法判断" };
}
public sealed record ProjectDiagnosticReport(ProjectSnapshot Snapshot, IReadOnlyList<ProjectManifest> Projects,
    IReadOnlyList<ProjectMatch> Matches, DiagnosticReport? Environment);
public interface IProjectScanner
{
    Task<ProjectSnapshot> ScanAsync(string root, ProjectScanOptions options, IProgress<ProjectProgress>? progress, CancellationToken cancellationToken);
}
public interface IProjectAnalyzer { IReadOnlyList<ProjectManifest> Analyze(ProjectSnapshot snapshot); }
public interface IProjectMatcher { IReadOnlyList<ProjectMatch> Match(IReadOnlyList<ProjectManifest> projects, DiagnosticReport? environment); }
public interface IProjectDiagnosticService
{
    Task<ProjectDiagnosticReport> RunAsync(string root, ProjectScanOptions scanOptions, DiagnosticOptions diagnosticOptions,
        IProgress<ProjectProgress>? progress, CancellationToken cancellationToken);
}
