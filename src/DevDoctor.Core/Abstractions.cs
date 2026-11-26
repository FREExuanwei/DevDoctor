namespace DevDoctor.Core;

public interface IDiagnosticModule
{
    ModuleDescriptor Descriptor { get; }
    Task<DiagnosticResult> DiagnoseAsync(DiagnosticContext context, CancellationToken cancellationToken);
}

public interface IDiagnosticEngine
{
    IReadOnlyList<ModuleDescriptor> Modules { get; }
    Task<DiagnosticReport> RunAsync(DiagnosticOptions options, IReadOnlyCollection<string>? moduleIds,
        IProgress<DiagnosticProgress>? progress, CancellationToken cancellationToken);
}

public interface IEnvironmentReader
{
    string? GetVariable(string name);
    PathConfiguration? ReadPathConfiguration() => null;
    bool DirectoryExists(string path);
    bool FileExists(string path);
    string NormalizePath(string path);
    IReadOnlyList<string> FindExecutables(string fileName);
    IReadOnlyList<string> FindCandidates(ToolKind tool);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProbeRequest request, CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IReportRedactor
{
    string Redact(string text);
    string RedactSecrets(string text) => Redact(text);
}
public interface IReportExporter { string Export(DiagnosticReport report); }

// Reserved for a later history phase; v0.2 keeps reports in session memory.
public interface IDiagnosticHistoryStore
{
    Task SaveAsync(DiagnosticReport report, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiagnosticReport>> GetRecentAsync(int count, CancellationToken cancellationToken);
}
