using DevDoctor.Core;
using DevDoctor.Infrastructure;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace DevDoctor.Tests;

public sealed class BackupSecurityTests
{
    [Theory]
    [InlineData("PRAGMA application_id=1234567")]
    [InlineData("DROP TABLE observations; DROP TABLE objects")]
    [InlineData("ALTER TABLE records RENAME COLUMN started TO unsupported_started")]
    [InlineData("CREATE UNIQUE INDEX unexpected_scope ON records(scope_id)")]
    [InlineData("DROP INDEX objects_project; CREATE UNIQUE INDEX objects_project ON objects(project_kind,record_id)")]
    public async Task Valid_hashes_do_not_allow_foreign_or_incomplete_database_schemas(string mutation)
    {
        using var temp = new TempDirectory();
        var fixture = await CreateBackup(temp.Root);
        RewriteDatabase(fixture.Zip, Path.Combine(temp.Root, "rewritten.sqlite3"), db => Execute(db, mutation));

        await Assert.ThrowsAsync<UserDataException>(() => fixture.Backup.ValidateAsync(fixture.Zip));
        await AssertOriginalIsUsable(fixture);
    }

    [Theory]
    [InlineData("UPDATE records SET id='00000000-0000-0000-0000-000000000001'")]
    [InlineData("UPDATE records SET scope_id='different-scope'")]
    [InlineData("UPDATE records SET scope_label='token=SHOULD-NOT-ENTER-HISTORY'")]
    [InlineData("UPDATE records SET aggregate_status=2")]
    [InlineData("UPDATE observations SET status=0")]
    [InlineData("DELETE FROM observations; DELETE FROM objects")]
    public async Task Valid_hashes_do_not_allow_indices_and_record_metadata_to_disagree_with_payload(string mutation)
    {
        using var temp = new TempDirectory();
        var fixture = await CreateBackup(temp.Root);
        RewriteDatabase(fixture.Zip, Path.Combine(temp.Root, "rewritten.sqlite3"), db => Execute(db, mutation));

        await Assert.ThrowsAsync<UserDataException>(() => fixture.Backup.ValidateAsync(fixture.Zip));
        await AssertOriginalIsUsable(fixture);
    }

    [Theory]
    [InlineData("null-notes")]
    [InlineData("null-object")]
    [InlineData("null-observations")]
    [InlineData("null-observation")]
    [InlineData("null-suggestions")]
    [InlineData("duplicate-object")]
    [InlineData("duplicate-observation")]
    public async Task Malformed_payload_graphs_are_rejected_as_data_errors_before_restore(string mutation)
    {
        using var temp = new TempDirectory();
        var fixture = await CreateBackup(temp.Root);
        RewriteDatabase(fixture.Zip, Path.Combine(temp.Root, "rewritten.sqlite3"), db =>
        {
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM records LIMIT 1";
            var payload = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            var objects = payload["Objects"]!.AsArray();
            var observations = objects[0]!["Observations"]!.AsArray();
            switch (mutation)
            {
                case "null-notes": payload["Notes"] = null; break;
                case "null-object": objects[0] = null; break;
                case "null-observations": objects[0]!["Observations"] = null; break;
                case "null-observation": observations[0] = null; break;
                case "null-suggestions": observations[0]!["Suggestions"] = null; break;
                case "duplicate-object": objects.Add(objects[0]!.DeepClone()); break;
                case "duplicate-observation": observations.Add(observations[0]!.DeepClone()); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            command.CommandText = "UPDATE records SET payload=$payload";
            command.Parameters.AddWithValue("$payload", payload.ToJsonString());
            command.ExecuteNonQuery();
        });

        await Assert.ThrowsAsync<UserDataException>(() => fixture.Backup.ValidateAsync(fixture.Zip));
        await AssertOriginalIsUsable(fixture);
    }

    [Fact]
    public async Task A_valid_backup_can_repair_a_physically_corrupt_live_database_without_losing_the_original()
    {
        using var temp = new TempDirectory();
        var source = await CreateBackup(Path.Combine(temp.Root, "source"));
        var target = await CreateBackup(Path.Combine(temp.Root, "target"));
        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("This is a physically corrupt SQLite database fixture.");
        File.WriteAllBytes(target.Data.DatabasePath, corruptBytes);

        var result = await target.Backup.RestoreAsync(await target.Backup.ValidateAsync(source.Zip));

        Assert.NotNull(await target.Store.GetAsync(source.Original.Id));
        using var restorePoint = ZipFile.OpenRead(result.RestorePoint);
        Assert.NotNull(restorePoint.GetEntry("recovery-manifest.json"));
        using var input = restorePoint.GetEntry("history.sqlite3")!.Open(); using var saved = new MemoryStream();
        input.CopyTo(saved); Assert.Equal(corruptBytes, saved.ToArray());
        Assert.False(File.Exists(target.Data.JournalPath));
    }

    [Fact]
    public async Task Failed_restore_from_logically_corrupt_live_database_returns_the_original_files()
    {
        using var temp = new TempDirectory();
        var source = await CreateBackup(Path.Combine(temp.Root, "source"));
        var target = await CreateBackup(Path.Combine(temp.Root, "target"));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target.Data.DatabasePath, Pooling = false }.ToString()))
        {
            db.Open(); Execute(db, "UPDATE records SET payload='{'");
        }
        var originalDatabase = File.ReadAllBytes(target.Data.DatabasePath);
        var originalSettings = File.ReadAllBytes(target.Data.SettingsPath);
        var originalKey = File.ReadAllBytes(target.Data.ScopeKeyPath);
        var restore = new BackupService(target.Data, target.Store, new FailSecondReplacement());
        var preview = await restore.ValidateAsync(source.Zip);

        var error = await Assert.ThrowsAsync<UserDataException>(() => restore.RestoreAsync(preview));

        Assert.Equal("rollback", error.Code);
        Assert.Equal(originalDatabase, File.ReadAllBytes(target.Data.DatabasePath));
        Assert.Equal(originalSettings, File.ReadAllBytes(target.Data.SettingsPath));
        Assert.Equal(originalKey, File.ReadAllBytes(target.Data.ScopeKeyPath));
        Assert.False(File.Exists(target.Data.JournalPath));
    }

    [Fact]
    public async Task A_valid_backup_can_repair_malformed_settings_and_preserves_the_original_settings_bytes()
    {
        using var temp = new TempDirectory();
        var source = await CreateBackup(Path.Combine(temp.Root, "source"));
        var target = await CreateBackup(Path.Combine(temp.Root, "target"));
        var corruptSettings = System.Text.Encoding.UTF8.GetBytes("{");
        File.WriteAllBytes(target.Data.SettingsPath, corruptSettings);

        var result = await target.Backup.RestoreAsync(await target.Backup.ValidateAsync(source.Zip));

        Assert.NotNull(await target.Store.GetAsync(source.Original.Id));
        Assert.Null((await new JsonSettingsStore(target.Data.SettingsPath).LoadAsync()).Warning);
        using var point = ZipFile.OpenRead(result.RestorePoint);
        using var input = point.GetEntry("settings.json")!.Open(); using var output = new MemoryStream();
        input.CopyTo(output); Assert.Equal(corruptSettings, output.ToArray());
    }

    [Theory]
    [InlineData("scope.key", 33)]
    [InlineData("settings.json", 65537)]
    public async Task Raw_restore_points_can_roundtrip_invalid_original_files_above_normal_import_limits(string file, int size)
    {
        using var temp = new TempDirectory();
        var source = await CreateBackup(Path.Combine(temp.Root, "source"));
        var target = await CreateBackup(Path.Combine(temp.Root, "target"));
        File.WriteAllBytes(Path.Combine(target.Data.Root, file), Enumerable.Repeat((byte)'x', size).ToArray());
        var originals = new[] { "history.sqlite3", "settings.json", "scope.key" }.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(target.Data.Root, n)));
        var restore = new BackupService(target.Data, target.Store, new FailSecondReplacement());
        var preview = await restore.ValidateAsync(source.Zip);

        var error = await Assert.ThrowsAsync<UserDataException>(() => restore.RestoreAsync(preview));

        Assert.Equal("rollback", error.Code);
        foreach (var pair in originals) Assert.Equal(pair.Value, File.ReadAllBytes(Path.Combine(target.Data.Root, pair.Key)));
        Assert.False(File.Exists(target.Data.JournalPath));
    }

    private sealed class FailSecondReplacement : IAtomicFileOperations
    {
        private int _calls;
        public void Replace(string source, string destination)
        {
            if (++_calls == 2) throw new IOException("controlled second-file failure");
            File.Move(source, destination, true);
        }
    }

    private sealed record Fixture(AppDataWorkspace Data, SqliteHistoryStore Store, BackupService Backup, HistoryRecord Original, string Zip);

    private static async Task<Fixture> CreateBackup(string root)
    {
        var workspace = new AppDataWorkspace(Path.Combine(root, "live"));
        await workspace.InitializeAsync();
        var store = new SqliteHistoryStore(workspace.DatabasePath, workspace.Gate);
        var original = HistoryComparisonTests.Record(HistoryComparisonTests.Object("java", DiagnosticStatus.Warning));
        await store.SaveAsync(original);
        await new JsonSettingsStore(workspace.SettingsPath, workspace.Gate).SaveAsync(AppearanceSettings.Defaults);
        var backup = new BackupService(workspace, store);
        var zip = Path.Combine(root, "backup.zip");
        await backup.CreateAsync(zip);
        return new(workspace, store, backup, original, zip);
    }

    private static async Task AssertOriginalIsUsable(Fixture fixture)
    {
        Assert.NotNull(await fixture.Store.GetAsync(fixture.Original.Id));
        Assert.Equal(1, (await fixture.Store.QueryAsync(new())).Total);
        Assert.False(File.Exists(fixture.Data.JournalPath));
    }

    private static void RewriteDatabase(string zip, string scratchDatabase, Action<SqliteConnection> mutation)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        using (var archive = ZipFile.OpenRead(zip))
            foreach (var entry in archive.Entries)
            {
                using var input = entry.Open(); using var output = new MemoryStream();
                input.CopyTo(output); entries.Add(entry.FullName, output.ToArray());
            }
        File.WriteAllBytes(scratchDatabase, entries["history.sqlite3"]);
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = scratchDatabase, Mode = SqliteOpenMode.ReadWrite, Pooling = false, ForeignKeys = false
        }.ToString()))
        {
            database.Open(); Execute(database, "PRAGMA journal_mode=DELETE"); mutation(database);
        }
        entries["history.sqlite3"] = File.ReadAllBytes(scratchDatabase);
        var manifest = JsonNode.Parse(entries["manifest.json"])!.AsObject();
        manifest["Files"]!["history.sqlite3"] = Convert.ToHexString(SHA256.HashData(entries["history.sqlite3"]));
        entries["manifest.json"] = System.Text.Encoding.UTF8.GetBytes(manifest.ToJsonString());
        using var stream = new FileStream(zip, FileMode.Create, FileAccess.Write, FileShare.None);
        using var rewritten = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var pair in entries) { using var output = rewritten.CreateEntry(pair.Key).Open(); output.Write(pair.Value); }
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
