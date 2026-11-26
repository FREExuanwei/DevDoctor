using DevDoctor.Core;

namespace DevDoctor.Tests;

internal sealed class FakeEnvironment : IEnvironmentReader
{
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string[]> Executables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<ToolKind, string[]> Candidates { get; } = [];
    public string? GetVariable(string name) => Variables.GetValueOrDefault(name);
    public bool DirectoryExists(string path) => Directories.Contains(NormalizePath(path));
    public bool FileExists(string path) => Files.Contains(NormalizePath(path));
    public string NormalizePath(string path) => path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
    public IReadOnlyList<string> FindExecutables(string fileName) => Executables.GetValueOrDefault(fileName) ?? [];
    public IReadOnlyList<string> FindCandidates(ToolKind tool) => Candidates.GetValueOrDefault(tool) ?? [];
    public void AddTool(ToolKind kind, string name, params string[] paths)
    {
        Executables[name] = paths;
        Candidates[kind] = paths;
        foreach (var path in paths) { Files.Add(NormalizePath(path)); Directories.Add(NormalizePath(Path.GetDirectoryName(path)!)); }
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    public Dictionary<ProbeKind, ProcessResult> Results { get; } = [];
    public List<ProbeRequest> Requests { get; } = [];
    public Task<ProcessResult> RunAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Results.GetValueOrDefault(request.Kind) ?? new ProcessResult(ProcessOutcome.Completed, 0));
    }
    public void Success(ProbeKind kind, string text) => Results[kind] = new(ProcessOutcome.Completed, 0, text);
}

internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "DevDoctor.Tests", Guid.NewGuid().ToString("N"), "中文 空格");
    public TempDirectory() => Directory.CreateDirectory(Root);
    public void Dispose() { try { Directory.Delete(Directory.GetParent(Root)!.FullName, true); } catch (IOException) { } }
}
