using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevDoctor.Core;
using DevDoctor.Diagnostics;
using DevDoctor.Infrastructure;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private static async Task<int> Main(string[] args)
    {
        if (args.Length is not (3 or 5)) { Console.Error.WriteLine("Usage: UpgradeSmoke DATA_ROOT OLD_MANIFEST OUTPUT_JSON [OLD_VERSION CURRENT_VERSION]"); return 2; }
        var output = Path.GetFullPath(args[2]); var checks = new List<string>();
        try
        {
            var oldVersion = args.Length == 5 ? args[3] : "1.0.0";
            var currentVersion = args.Length == 5 ? args[4] : "1.1.0";
            if ((oldVersion, currentVersion) is not (("1.0.0", "1.1.0") or ("1.1.0", "1.2.0"))) throw new InvalidDataException("Unsupported genuine upgrade pair.");
            var oldRuleVersion = oldVersion == "1.0.0" ? 1 : 2;
            var root = Path.GetFullPath(args[0]); var manifestPath = Path.GetFullPath(args[1]);
            var seedRoot = Path.GetDirectoryName(manifestPath)!;
            var expected = JsonSerializer.Deserialize<Seed>(await File.ReadAllTextAsync(manifestPath), Json) ?? throw new InvalidDataException("Missing seed manifest.");
            Check(expected.Format == 1 && expected.RecordCount >= 2 && expected.Records.Length == expected.RecordCount, "Authentic seed manifest structure");
            Check(expected.Records.All(r => r.SoftwareVersion == oldVersion && r.RuleVersion == oldRuleVersion && r.FormatVersion == 1), "Manifest records match expected historical version/rules/format");
            Check(expected.UiEvidence.Length >= 4 && expected.LoadedAssemblies.Length >= 3, "Old production UI and assembly provenance recorded");
            foreach (var assembly in expected.LoadedAssemblies)
                Check(assembly.AssemblyVersion.StartsWith(oldVersion + ".", StringComparison.Ordinal) && Hash(assembly.Path) == assembly.Sha256, "Archived loaded assembly unchanged: " + Path.GetFileName(assembly.Path));
            Check(Hash(expected.Executable.Path) == expected.Executable.Sha256 && expected.Executable.FileVersion == oldVersion + ".0", "Original old executable version/hash");
            DirectoryInfo? workspace = new DirectoryInfo(AppContext.BaseDirectory);
            while (workspace is not null && !Directory.Exists(Path.Combine(workspace.FullName, "artifacts", "baselines", "v" + oldVersion))) workspace = workspace.Parent;
            Check(workspace is not null, "Preserved baseline archive located");
            var portable = Path.Combine(workspace!.FullName, "artifacts", "baselines", "v" + oldVersion, $"DevDoctor-v{oldVersion}-win-x64-portable.zip");
            Check(Hash(portable) == (oldVersion == "1.0.0" ? "1BC07F5414609C11F479A1977525AC8208FAC418938F7DF6D68C1124AB18CC41" : "3BFDDA212A5D1F54C366A220BFB7B9055325793E5251376626D341F0F527FFC1"), "Official preserved portable archive SHA256");
            using (var archive = ZipFile.OpenRead(portable))
                foreach (var file in expected.LoadedAssemblies.Select(a => new FileHash(a.Path, a.Sha256)).Append(new(expected.Executable.Path, expected.Executable.Sha256)))
                {
                    var entry = archive.Entries.Single(e => Path.GetFileName(e.FullName) == Path.GetFileName(file.Path)); using var stream = entry.Open();
                    Check(Convert.ToHexString(SHA256.HashData(stream)) == file.Sha256, "Actually loaded old binary matches official archived entry: " + Path.GetFileName(file.Path));
                }
            foreach (var file in expected.Files)
                Check(Hash(Child(seedRoot, file.Path)) == file.Sha256, "Original seed/report/backup unchanged: " + file.Path);
            Check(AppInfo.Version == currentVersion && AppInfo.RuleVersion == 2 && AppInfo.DataFormat == 1 && SqliteHistoryStore.SchemaVersion == 2, "Current production version and unchanged schema/data format");
            // WorkspaceLease rejects any concurrently open production app using this data root.
            var data = new AppDataWorkspace(root); using var lease = new WorkspaceLease(data);
            await VerifyData(data, expected, checks);

            var exported = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(seedRoot, "old-report.json")));
            var reportRecord = exported.RootElement.GetProperty("Record").Deserialize<HistoryRecord>(Json)!;
            Check(reportRecord.Id == expected.SelectedReportId && reportRecord.SoftwareVersion == oldVersion && reportRecord.RuleVersion == oldRuleVersion, "Original JSON report opens with historical metadata and selected ID");
            var html = await File.ReadAllTextAsync(Path.Combine(seedRoot, "old-report.html"));
            Check(html.Contains(expected.SelectedReportId.ToString(), StringComparison.OrdinalIgnoreCase) && html.Contains(oldVersion, StringComparison.Ordinal) && !html.Contains("<script", StringComparison.OrdinalIgnoreCase) && !html.Contains("http://", StringComparison.OrdinalIgnoreCase) && !html.Contains("https://", StringComparison.OrdinalIgnoreCase), "Original HTML report contains historical ID/version and is self-contained offline");
            var comparer = new HistoryComparer(); var old = expected.Records[0];
            var same = comparer.Compare(old, old with { Id = Guid.NewGuid() });
            Check(same.Notes.All(n => !n.Contains("规则版本不同", StringComparison.Ordinal)), "Same-version old records retain comparable rule metadata");
            // This is only a controlled comparison input, never persisted or claimed as a genuine new scan.
            var currentInput = old with { Id = Guid.NewGuid(), SoftwareVersion = AppInfo.Version, RuleVersion = AppInfo.RuleVersion };
            var boundary = comparer.Compare(old, currentInput);
            if (oldRuleVersion != AppInfo.RuleVersion)
                Check(boundary.Differences.Count > 0 && boundary.Differences.All(d => d.Kind == DifferenceKind.NotComparable) && boundary.Notes.Count > 0, "Different old/new rule versions explicitly compare as NotComparable");
            else
                Check(boundary.Notes.All(n => !n.Contains("规则版本不同", StringComparison.Ordinal)) && boundary.Differences.All(d => d.Kind != DifferenceKind.NotComparable), "Same rule version across software upgrade retains comparability");
            var currentSame = comparer.Compare(currentInput, currentInput with { Id = Guid.NewGuid() });
            Check(currentSame.Notes.All(n => !n.Contains("规则版本不同", StringComparison.Ordinal)), "Same-version current comparison input remains comparable");

            var restoreRoot = Path.Combine(Path.GetDirectoryName(output)!, "old-backup-restored-" + Guid.NewGuid().ToString("N"));
            var restoreData = new AppDataWorkspace(restoreRoot); using var restoreLease = new WorkspaceLease(restoreData);
            await restoreData.InitializeAsync(); var restoreStore = new SqliteHistoryStore(restoreData.DatabasePath, restoreData.Gate); await restoreStore.EnsureAsync();
            // Deliberately distinct target settings/key prove that restoring the ORIGINAL archive replaces them.
            var temporaryKeyHash = Hash(restoreData.ScopeKeyPath);
            var backup = new BackupService(restoreData, restoreStore);
            var preview = await backup.ValidateAsync(expected.BackupPath);
            Check(preview.SoftwareVersion == oldVersion && preview.Records == expected.RecordCount && preview.FormatVersion == 1, "New production BackupService validates original old archive");
            var restored = await backup.RestoreAsync(preview);
            Check(restored.Records == expected.RecordCount && restored.Settings == expected.Settings && File.Exists(restored.RestorePoint), "New production restore returns expected settings/count and creates recovery point");
            Check(temporaryKeyHash != expected.ScopeKeySha256, "Separate restore root originally had a distinct scope key");
            await VerifyData(restoreData, expected, checks);
            Check(Hash(expected.BackupPath) == expected.Files.Single(f => f.Path == "old-full-backup.zip").Sha256, "Original old backup preserved byte for byte after new restore");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = true, softwareVersion = AppInfo.Version, dataRoot = root, expectedManifest = manifestPath, records = expected.Records.Select(r => r.Id), recordCount = expected.RecordCount, restoredRoot = restoreRoot, originalBackup = expected.BackupPath, checks, checkedAt = DateTimeOffset.Now }, Json));
            Console.WriteLine("PASS old data read + original old backup restore: " + output); return 0;

            void Check(bool condition, string message) { if (!condition) throw new InvalidDataException(message); checks.Add("PASS " + message); }
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed = false, error = ex.ToString(), checks, checkedAt = DateTimeOffset.Now }, Json));
            Console.Error.WriteLine(ex); return 1;
        }
    }

    private static async Task VerifyData(AppDataWorkspace data, Seed expected, List<string> checks)
    {
        // Check original key BEFORE initialization: missing original identity must not be regenerated.
        Require(File.Exists(data.DatabasePath) && File.Exists(data.SettingsPath) && File.Exists(data.ScopeKeyPath), "All three original data files exist");
        Require(Hash(data.ScopeKeyPath) == expected.ScopeKeySha256, "Exact original scope.key preserved");
        await data.InitializeAsync(); var store = new SqliteHistoryStore(data.DatabasePath, data.Gate); await store.EnsureAsync();
        var stats = await store.StatisticsAsync(); var page = await store.QueryAsync(new(PageSize: 100));
        Require(stats.Total == expected.RecordCount && page.Total == expected.RecordCount && page.Items.Select(i => i.Id).Order().SequenceEqual(expected.Records.Select(i => i.Id).Order()), "Exact historical count and IDs via current IHistoryStore");
        foreach (var old in expected.Records)
        {
            var actual = await store.GetAsync(old.Id);
            Require(actual is not null && JsonSerializer.Serialize(actual, Json) == JsonSerializer.Serialize(old, Json), "Complete historical record metadata/objects/rules/evidence unchanged: " + old.Id);
        }
        var settings = await new JsonSettingsStore(data.SettingsPath, data.Gate).LoadAsync();
        Require(settings.Warning is null && settings.Settings == expected.Settings && settings.Settings.ThemePreset == "AmberGold" && settings.Settings.AppearanceMode == "Light" && settings.Settings.InterfaceStyle == "CompactPro" && settings.Settings.ScalePercent == 110 && settings.Settings.TimeoutSeconds == 7, "Nondefault old appearance and diagnostic settings load through production settings API");
        Require(Hash(data.ScopeKeyPath) == expected.ScopeKeySha256, "Reading/initialization did not rotate original key");
        var identity = new LocalScopeIdentity(data);
        Require(expected.Records.Where(r => r.Kind == HistoryScanKind.Environment).All(r => r.ScopeId == identity.GetId("environment", Environment.MachineName)), "Original key reproduces historical production machine scope IDs");
        void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(data.Root + ": " + message); checks.Add("PASS " + message); }
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    private static string Child(string root, string name)
    {
        var path = Path.GetFullPath(Path.Combine(root, name)); if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Manifest path escapes seed root."); return path;
    }
    private sealed record FileHash(string Path, string Sha256);
    private sealed record AssemblyEvidence(string Path, string AssemblyVersion, string Sha256);
    private sealed record ExecutableEvidence(string Path, string FileVersion, string Sha256);
    private sealed record Seed(int Format, int RecordCount, HistoryRecord[] Records, Guid SelectedReportId, AppSettings Settings, string ScopeKeySha256, string BackupPath, FileHash[] Files, string[] UiEvidence, AssemblyEvidence[] LoadedAssemblies, ExecutableEvidence Executable);
}
