using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DevDoctor.App.Services;
using DevDoctor.Core;
using DevDoctor.Infrastructure;

namespace DevDoctor.App.ViewModels;

public sealed class ReportsViewModel : ObservableObject
{
    private readonly HistoryViewModel _history; private readonly IStructuredReportExporter _exporter; private readonly IBackupService _backups; private readonly IUserFileDialogs _dialogs; private readonly AppDataWorkspace _data;
    private Func<Task> _flush = () => Task.CompletedTask; private Func<AppSettings, Task> _apply = _ => Task.CompletedTask;
    private string _message = "先到“历史管理”选择一条记录或生成对比，再导出完整报告。"; private bool _busy; private bool _externalBusy;
    private BackupPreview? _preview; private CancellationTokenSource? _operation;
    public ReportsViewModel(HistoryViewModel history, IStructuredReportExporter exporter, IBackupService backups, IUserFileDialogs dialogs, AppDataWorkspace data)
    {
        (_history, _exporter, _backups, _dialogs, _data) = (history, exporter, backups, dialogs, data);
        ExportJsonCommand = new AsyncRelayCommand(() => ExportAsync(false, false), () => Ready && _history.SelectedRecord is not null);
        ExportHtmlCommand = new AsyncRelayCommand(() => ExportAsync(true, false), () => Ready && _history.SelectedRecord is not null);
        ExportDiffJsonCommand = new AsyncRelayCommand(() => ExportAsync(false, true), () => Ready && _history.Comparison is not null);
        ExportDiffHtmlCommand = new AsyncRelayCommand(() => ExportAsync(true, true), () => Ready && _history.Comparison is not null);
        BackupCommand = new AsyncRelayCommand(BackupAsync, () => Ready);
        ValidateCommand = new AsyncRelayCommand(ValidateAsync, () => Ready);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => Ready && _preview is not null);
        _history.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(HistoryViewModel.SelectedRecord) or nameof(HistoryViewModel.Comparison)) { OnPropertyChanged(nameof(SelectionText)); Notify(); } };
    }
    public void ConfigureAppearance(Func<Task> flush, Func<AppSettings, Task> apply) => (_flush, _apply) = (flush, apply);
    public bool IsBusy { get => _busy; private set { if (SetProperty(ref _busy, value)) { OnPropertyChanged(nameof(IsIdle)); Notify(); } } }
    public bool IsIdle => !IsBusy;
    public bool ExternalBusy { get => _externalBusy; set { if (SetProperty(ref _externalBusy, value)) Notify(); } }
    private bool Ready => !IsBusy && !ExternalBusy;
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string SelectionText => (_history.SelectedRecord is { } record ? $"已选记录：{record.StartedAt:yyyy-MM-dd HH:mm:ss} · {UiText.ScanKindName(record.Kind)} · {record.Id.ToString()[..8]}" : "尚未选择历史记录") + (_history.Comparison is null ? "\n尚未生成历史对比" : $"\n对比：{_history.Comparison.Before.Id.ToString()[..8]} → {_history.Comparison.After.Id.ToString()[..8]}");
    public string PreviewText => _preview is null ? "尚未校验备份。选择文件只做校验，确认后才会恢复。" : $"备份校验通过\n生成时间：{_preview.CreatedAt:yyyy-MM-dd HH:mm:ss zzz}\n软件版本：{_preview.SoftwareVersion} · 格式：{_preview.FormatVersion} · 历史：{_preview.Records} 条\n将覆盖：{string.Join("、", _preview.Replaces)}\nSHA-256：{_preview.Sha256}\n恢复前自动建立本地恢复点；恢复失败将尝试回退。";
    public IAsyncRelayCommand ExportJsonCommand { get; }
    public IAsyncRelayCommand ExportHtmlCommand { get; }
    public IAsyncRelayCommand ExportDiffJsonCommand { get; }
    public IAsyncRelayCommand ExportDiffHtmlCommand { get; }
    public IAsyncRelayCommand BackupCommand { get; }
    public IAsyncRelayCommand ValidateCommand { get; }
    public IAsyncRelayCommand RestoreCommand { get; }
    private async Task ExportAsync(bool html, bool difference)
    {
        var record = _history.SelectedRecord; var comparison = _history.Comparison;
        if (difference ? comparison is null : record is null) return;
        var extension = html ? ".html" : ".json";
        var path = _dialogs.Save(difference ? "导出脱敏历史对比" : "导出脱敏诊断报告", extension, "DevDoctor-" + (difference ? "对比-" : "诊断-") + DateTime.Now.ToString("yyyyMMdd-HHmmss") + extension);
        if (path is null) return;
        await Run(async token =>
        {
            if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) || !WindowsPathPolicy.IsLocalAbsolute(path) || Path.GetFullPath(path).StartsWith(_data.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UserDataException("path", "请选择用户数据目录之外的本地报告文件，以免覆盖应用数据。");
            var text = await Task.Run(() => difference ? html ? _exporter.Html(comparison!) : _exporter.Json(comparison!) : html ? _exporter.Html(record!) : _exporter.Json(record!), token);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temporary, text, token); File.Move(temporary, path, true); } finally { try { File.Delete(temporary); } catch (IOException) { } }
            Message = "报告已导出到所选位置（默认脱敏）。分享前请再次核对内容。";
        });
    }
    private async Task BackupAsync()
    {
        var path = _dialogs.Save("保存完整本地备份", ".zip", "DevDoctor-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"); if (path is null) return;
        await Run(async token => { await _flush(); await _backups.CreateAsync(path, token); Message = "备份已生成，包含 SQLite 一致性快照、设置、外观和范围标识。请妥善保管完整备份。"; });
    }
    private async Task ValidateAsync()
    {
        var path = _dialogs.OpenBackup(); if (path is null) return; _preview = null; OnPropertyChanged(nameof(PreviewText));
        await Run(async token => { _preview = await _backups.ValidateAsync(path, token); OnPropertyChanged(nameof(PreviewText)); Message = "备份已验证。请阅读覆盖范围，再点击“恢复此备份”。"; });
    }
    private async Task RestoreAsync()
    {
        var preview = _preview; if (preview is null || !_dialogs.Confirm("恢复此备份", PreviewText + "\n\n确定用此备份覆盖当前用户数据吗？恢复期间暂时锁定界面，完成后立即应用恢复的外观。")) return;
        await Run(async token =>
        {
            await _flush(); var result = await _backups.RestoreAsync(preview, token);
            await _apply(result.Settings); _history.ClearAfterRestore(); await _history.RefreshAsync(reset: true);
            _preview = null; OnPropertyChanged(nameof(PreviewText));
            Message = $"恢复完成 · {result.Records} 条历史。外观已应用。\n恢复点：{result.RestorePoint}";
        });
    }
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (!Ready) return; IsBusy = true; _operation = new(); Message = "正在处理本地数据，请稍候…";
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Message = "本次操作已取消。"; }
        catch (Exception ex) when (ex is UserDataException or IOException or UnauthorizedAccessException or ArgumentException) { Message = HistoryViewModel.Explain(ex); }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; }
    }
    public void Cancel() => _operation?.Cancel();
    public Task WaitForIdleAsync() => Task.WhenAll(new[] { ExportJsonCommand.ExecutionTask, ExportHtmlCommand.ExecutionTask, ExportDiffJsonCommand.ExecutionTask, ExportDiffHtmlCommand.ExecutionTask, BackupCommand.ExecutionTask, ValidateCommand.ExecutionTask, RestoreCommand.ExecutionTask }.OfType<Task>());
    private void Notify() { foreach (var command in new[] { ExportJsonCommand, ExportHtmlCommand, ExportDiffJsonCommand, ExportDiffHtmlCommand, BackupCommand, ValidateCommand, RestoreCommand }.OfType<IRelayCommand>()) command.NotifyCanExecuteChanged(); }
}
