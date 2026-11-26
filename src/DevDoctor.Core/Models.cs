namespace DevDoctor.Core;

public enum DiagnosticStatus { Normal, Warning, Error, NotInstalled, Unknown, NotApplicable }
public enum ToolKind { Java, Python, Node, Git, PythonLauncher, Maven, Gradle, Dotnet, CMake, VisualStudio }
public enum ProbeKind { JavaVersion, JavacVersion, PythonVersion, PipVersion, PythonLauncherList, NodeVersion, NpmVersion, GitVersion, PowerShellPolicy,
    MavenVersion, GradleVersion, DotnetVersion, DotnetSdks, DotnetRuntimes, CMakeVersion, VsInstances, VsCppInstances, PythonEnvironment }
public enum ProcessOutcome { Completed, Missing, AccessDenied, TimedOut, Cancelled, Rejected, Failed }

public sealed record ModuleDescriptor(string Id, string Name, string Category, string Description, string Monogram);

public sealed record DiagnosticFinding(string Code, string Title, DiagnosticStatus Status, string Observed,
    string Basis, string PossibleCause, IReadOnlyList<string> Suggestions);

public sealed record DiagnosticResult(string ModuleId, string Name, string Category,
    IReadOnlyList<DiagnosticFinding> Findings, DateTimeOffset StartedAt, TimeSpan Duration,
    string? Version = null, string? ExecutablePath = null, IReadOnlyList<string>? Candidates = null)
{
    public IReadOnlyDictionary<string, string[]> Facts { get; init; } = new Dictionary<string, string[]>();
    public DiagnosticStatus Status => DiagnosticStatusRules.Aggregate(Findings.Select(f => f.Status));
    public string Summary => Findings.FirstOrDefault(f => f.Status == Status)?.Observed ?? "没有可用结果";
}

public sealed record DiagnosticReport(Guid Id, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    IReadOnlyList<DiagnosticResult> Results, bool WasCancelled);

public sealed record DiagnosticProgress(string ModuleId, string ModuleName, int Completed, int Total, DiagnosticResult? Result);

public sealed record DiagnosticOptions
{
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ModuleTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

public sealed record DiagnosticContext(DiagnosticOptions Options);
public sealed record ProbeRequest(ProbeKind Kind, string ExecutablePath, TimeSpan Timeout);

// A bounded reconstruction, not a promise of the environment inherited by every shell.
public sealed record PathConfiguration(string? MachinePath, string? UserPath, string MergedPath,
    bool IsComplete, string? Error = null);

public sealed record ProcessResult(ProcessOutcome Outcome, int? ExitCode = null, string StandardOutput = "",
    string StandardError = "", bool OutputTruncated = false, string? Error = null)
{
    public bool Success => Outcome == ProcessOutcome.Completed && ExitCode == 0;
    public string CombinedOutput => (StandardOutput + "\n" + StandardError).Trim();
}

public sealed record AppSettings
{
    public string Theme { get; init; } = "Dark";
    public int AppearanceVersion { get; init; }
    public string ThemePreset { get; init; } = "FluentBlue";
    public string AppearanceMode { get; init; } = "System";
    public string InterfaceStyle { get; init; } = "FluentProfessional";
    public int ScalePercent { get; init; } = 100;
    public int TimeoutSeconds { get; init; } = 10;
    public bool RedactPaths { get; init; } = true;
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning = null);
