using DevDoctor.App.ViewModels;
using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DevDoctor.App.Services;

public static class ApplicationServices
{
    public static ServiceProvider Create(string? dataRoot = null, IUserFileDialogs? fileDialogs = null)
    {
        var services = new ServiceCollection();
        var data = AppDataWorkspace.Resolve(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), dataRoot ?? Environment.GetEnvironmentVariable("DEVDOCTOR_DATA_ROOT"));
        services.AddSingleton(data);
        services.AddSingleton<WorkspaceLease>();
        services.AddSingleton<IEnvironmentReader, WindowsEnvironmentReader>();
        services.AddSingleton<IProcessRunner, SafeProcessRunner>();
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(data.SettingsPath, data.Gate));
        services.AddSingleton(_ => new SqliteHistoryStore(data.DatabasePath, data.Gate, () => { if (File.Exists(data.JournalPath)) throw new UserDataException("recovery", "上次恢复尚未完成，已暂停历史读写。请重启以回退恢复点，原件保留在 recovery 目录。"); }));
        services.AddSingleton<IHistoryStore>(s => s.GetRequiredService<SqliteHistoryStore>());
        services.AddSingleton<IScopeIdentity, LocalScopeIdentity>();
        services.AddSingleton<IHistoryRecordFactory, HistoryRecordFactory>();
        services.AddSingleton<IHistoryCapture, HistoryCapture>();
        services.AddSingleton<IHistoryComparer, HistoryComparer>();
        services.AddSingleton<IStructuredReportExporter, StructuredReportExporter>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<IBackupService>(s => s.GetRequiredService<BackupService>());
        services.AddSingleton<IUserFileDialogs>(fileDialogs ?? new UserFileDialogs());
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<ReportsViewModel>();
        services.AddSingleton<IReportRedactor, ReportRedactor>();
        services.AddSingleton<IReportExporter, MarkdownReportExporter>();
        services.AddSingleton<IDiagnosticModule, JavaDiagnostic>();
        services.AddSingleton<IDiagnosticModule, PythonDiagnostic>();
        services.AddSingleton<IDiagnosticModule, NodeDiagnostic>();
        services.AddSingleton<IDiagnosticModule, GitDiagnostic>();
        services.AddSingleton<IDiagnosticModule, EnvironmentDiagnostic>();
        services.AddSingleton<IToolchainInstallationReader, ToolchainInstallationReader>();
        services.AddSingleton<IDiagnosticModule, MavenDiagnostic>();
        services.AddSingleton<IDiagnosticModule, GradleDiagnostic>();
        services.AddSingleton<IDiagnosticModule, DotnetDiagnostic>();
        services.AddSingleton<IDiagnosticModule, CMakeDiagnostic>();
        services.AddSingleton<IDiagnosticModule, VisualStudioDiagnostic>();
        services.AddSingleton<IDiagnosticEngine, DiagnosticEngine>();
        services.AddSingleton<IProjectScanner, LocalProjectScanner>();
        services.AddSingleton<IProjectAnalyzer, StaticProjectAnalyzer>();
        services.AddSingleton<IProjectMatcher, ProjectMatcher>();
        services.AddSingleton<IProjectDiagnosticService, ProjectDiagnosticService>();
        services.AddSingleton<IProjectFolderPicker, ProjectFolderPicker>();
        services.AddSingleton<ProjectViewModel>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<MainViewModel>(); services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
