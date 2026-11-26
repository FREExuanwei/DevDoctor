namespace DevDoctor.Core;

public static class AppInfo { public const string Version = "1.2.0"; public const int DataFormat = 1; public const int RuleVersion = 2; }
public enum HistoryScanKind { Environment, Project }
public enum ScanCompletion { Completed, Cancelled, Failed }
public enum CoverageState { Complete, Partial, NotChecked }
public enum ToolPresence { Unknown, Present, Missing }
public enum DifferenceKind { NewProblem, Resolved, Persistent, StatusChanged, NotComparable, ToolAdded, ToolRemoved }

public sealed record HistoryObservation(string Id, string RuleId, string Title, DiagnosticStatus Status,
    string Observed, string Evidence, string Cause, IReadOnlyList<string> Suggestions)
{
    public bool IsProblem => Status is DiagnosticStatus.Error or DiagnosticStatus.Warning or DiagnosticStatus.NotInstalled or DiagnosticStatus.Unknown;
}
public sealed record HistoryObject(string Id, string Name, string Category, ProjectKind? ProjectKind, CoverageState Coverage,
    ToolPresence Presence, string? Version, IReadOnlyList<HistoryObservation> Observations);
public sealed record HistoryRecord(Guid Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, HistoryScanKind Kind,
    string ScopeId, string ScopeLabel, ScanCompletion Completion, IReadOnlyList<HistoryObject> Objects)
{
    public int FormatVersion { get; init; } = AppInfo.DataFormat;
    // Missing metadata in legacy data must never claim the new rule coverage.
    // Production factories stamp current versions explicitly.
    public int RuleVersion { get; init; } = 1;
    public string SoftwareVersion { get; init; } = "1.0.0";
    public IReadOnlyList<string> Notes { get; init; } = [];
    public DiagnosticStatus Status => DiagnosticStatusRules.Aggregate(Objects.SelectMany(o => o.Observations).Select(o => o.Status));
}
public sealed record HistoryQuery(DateTimeOffset? From = null, DateTimeOffset? Until = null, string? ObjectName = null,
    DiagnosticStatus? Status = null, ProjectKind? ProjectKind = null, HistoryScanKind? Kind = null, int Page = 0, int PageSize = 25)
{
    public string? SearchText { get; init; }
}
public sealed record HistorySummary(Guid Id, DateTimeOffset StartedAt, HistoryScanKind Kind, string ScopeLabel,
    ScanCompletion Completion, DiagnosticStatus Status, int ObjectCount, int ObservationCount);
public sealed record HistoryPage(IReadOnlyList<HistorySummary> Items, int Total, int Page, int PageSize);
public sealed record HistoryStatistics(int Total, int Completed, int Cancelled, int Failed, IReadOnlyDictionary<DiagnosticStatus, int> StatusCounts);
public sealed record HistoryDifference(DifferenceKind Kind, string ObjectName, HistoryObservation? Before,
    HistoryObservation? After, string Explanation);
public sealed record HistoryComparison(HistoryRecord Before, HistoryRecord After, IReadOnlyList<HistoryDifference> Differences,
    IReadOnlyList<string> Notes);
public sealed class UserDataException(string code, string message, Exception? inner = null) : Exception(message, inner)
{ public string Code { get; } = code; }

public interface IScopeIdentity { string GetId(string kind, string value); }
public interface IHistoryStore
{
    Task EnsureAsync(CancellationToken token = default);
    Task SaveAsync(HistoryRecord record, CancellationToken token = default);
    Task<HistoryRecord?> GetAsync(Guid id, CancellationToken token = default);
    Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken token = default);
    Task<HistoryStatistics> StatisticsAsync(CancellationToken token = default);
    Task<int> DeleteAsync(Guid id, CancellationToken token = default);
    Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken token = default);
}
public interface IHistoryRecordFactory
{
    HistoryRecord Environment(DiagnosticReport report);
    HistoryRecord Project(ProjectDiagnosticReport report);
    HistoryRecord Failure(HistoryScanKind kind, string scope, string reason, DateTimeOffset startedAt, ScanCompletion completion = ScanCompletion.Failed);
}
public interface IHistoryComparer { HistoryComparison Compare(HistoryRecord before, HistoryRecord after); }
public interface IHistoryCapture
{
    Guid? LastSavedRecordId => null;
    event EventHandler? Changed;
    Task<string?> EnvironmentAsync(DiagnosticReport report);
    Task<string?> ProjectAsync(ProjectDiagnosticReport report);
    Task<string?> FailureAsync(HistoryScanKind kind, string scope, string reason, DateTimeOffset started, ScanCompletion completion = ScanCompletion.Failed);
}
public interface IStructuredReportExporter
{
    string Json(HistoryRecord record);
    string Html(HistoryRecord record);
    string Json(HistoryComparison comparison);
    string Html(HistoryComparison comparison);
}

public sealed record BackupPreview(string FilePath, int FormatVersion, DateTimeOffset CreatedAt, string SoftwareVersion,
    int Records, string Sha256, IReadOnlyList<string> Replaces)
{ public string? PendingJournalSha256 { get; init; } }
public sealed record RestoreResult(string RestorePoint, int Records, AppSettings Settings);
public interface IBackupService
{
    Task CreateAsync(string destination, CancellationToken token = default);
    Task<BackupPreview> ValidateAsync(string path, CancellationToken token = default);
    Task<RestoreResult> RestoreAsync(BackupPreview confirmedPreview, CancellationToken token = default);
}
