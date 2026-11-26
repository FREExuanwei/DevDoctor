using DevDoctor.Core;

namespace DevDoctor.Infrastructure;

public sealed class HistoryCapture(IHistoryStore store, IHistoryRecordFactory factory) : IHistoryCapture
{
    public event EventHandler? Changed;
    public Guid? LastSavedRecordId { get; private set; }
    public Task<string?> EnvironmentAsync(DiagnosticReport report) => Save(() => factory.Environment(report));
    public Task<string?> ProjectAsync(ProjectDiagnosticReport report) => Save(() => factory.Project(report));
    public Task<string?> FailureAsync(HistoryScanKind kind, string scope, string reason, DateTimeOffset started, ScanCompletion completion = ScanCompletion.Failed) => Save(() => factory.Failure(kind, scope, reason, started, completion));
    private async Task<string?> Save(Func<HistoryRecord> create)
    {
        LastSavedRecordId = null;
        try { var record = await Task.Run(create).ConfigureAwait(false); await store.SaveAsync(record).ConfigureAwait(false); LastSavedRecordId = record.Id; Changed?.Invoke(this, EventArgs.Empty); return null; }
        catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException or ArgumentException)
        { return "历史保存未完成，当前诊断结果仍可查看。" + (ex is UserDataException data ? data.Message : "请检查本地数据目录是否可写。"); }
    }
}
