using DevDoctor.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevDoctor.Infrastructure;

public sealed class SqliteHistoryStore : IHistoryStore
{
    public const int SchemaVersion = 2;
    public string DatabasePath { get; }
    public SemaphoreSlim DataGate { get; }
    private readonly IReportRedactor _redactor = new ReportRedactor();
    internal static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 64, PropertyNameCaseInsensitive = true };
    private const int MaxPayload = 8 * 1024 * 1024;
    private readonly Action? _accessGuard;
    public SqliteHistoryStore(string databasePath, SemaphoreSlim? dataGate = null, Action? accessGuard = null) => (DatabasePath, DataGate, _accessGuard) = (databasePath, dataGate ?? new(1, 1), accessGuard);
    public Task EnsureAsync(CancellationToken token = default) => Work(_ => true, token);
    public Task SaveAsync(HistoryRecord record, CancellationToken token = default) => Work(db =>
    {
        ValidateRecord(record); record = Sanitize(record);
        var payload = JsonSerializer.Serialize(record, JsonOptions);
        if (payload.Length > MaxPayload) throw new UserDataException("limit", "历史证据超过单条 8 Mi 字符上限；诊断结果仍保留在当前会话。");
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM records WHERE id=$id"; command.Parameters.AddWithValue("$id", record.Id.ToString()); command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO records(id,started,finished,kind,scope_id,scope_label,completion,aggregate_status,payload,rule_version) VALUES($id,$started,$finished,$kind,$scope,$label,$completion,$status,$payload,$rule)";
        command.Parameters.AddWithValue("$started", record.StartedAt.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$finished", record.FinishedAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$kind", (int)record.Kind); command.Parameters.AddWithValue("$scope", record.ScopeId); command.Parameters.AddWithValue("$label", record.ScopeLabel);
        command.Parameters.AddWithValue("$completion", (int)record.Completion); command.Parameters.AddWithValue("$status", (int)record.Status); command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$rule", record.RuleVersion);
        command.ExecuteNonQuery(); InsertIndex(db, transaction, record); token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    public Task<HistoryRecord?> GetAsync(Guid id, CancellationToken token = default) => Work(db =>
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM records WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        return command.ExecuteScalar() is string payload ? HistoryPrivacy.Redact(Parse(payload), _redactor) : null;
    }, token);
    public Task<HistoryPage> QueryAsync(HistoryQuery query, CancellationToken token = default) => Work(db =>
    {
        var page = Math.Clamp(query.Page, 0, 1000000); var size = Math.Clamp(query.PageSize, 1, 100);
        using var command = db.CreateCommand(); var filters = new List<string>();
        Exception? searchFailure = null;
        if (query.From is { } from) { filters.Add("r.started >= $from"); command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds()); }
        if (query.Until is { } until) { filters.Add("r.started < $until"); command.Parameters.AddWithValue("$until", until.ToUnixTimeMilliseconds()); }
        if (!string.IsNullOrWhiteSpace(query.ObjectName)) { filters.Add("EXISTS(SELECT 1 FROM objects o WHERE o.record_id=r.id AND instr(lower(o.name),lower($object))>0)"); command.Parameters.AddWithValue("$object", query.ObjectName[..Math.Min(128, query.ObjectName.Length)]); }
        if (query.Status is { } status) { filters.Add("EXISTS(SELECT 1 FROM observations f WHERE f.record_id=r.id AND f.status=$status)"); command.Parameters.AddWithValue("$status", (int)status); }
        if (query.ProjectKind is { } project) { filters.Add("EXISTS(SELECT 1 FROM objects o WHERE o.record_id=r.id AND o.project_kind=$project)"); command.Parameters.AddWithValue("$project", (int)project); }
        if (query.Kind is { } kind) { filters.Add("r.kind=$kind"); command.Parameters.AddWithValue("$kind", (int)kind); }
        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var text = query.SearchText.Trim();
            if (text.Length > DiagnosticSearch.MaxQueryLength) throw new UserDataException("limit", "历史搜索最多支持256个字符，请缩小关键词。");
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 8) throw new UserDataException("limit", "历史搜索最多支持8个关键词，请缩小条件。");
            var budget = System.Diagnostics.Stopwatch.StartNew();
            var memo = new Dictionary<string, bool>(StringComparer.Ordinal);
            // A connection-local, read-only predicate decodes and redacts legacy payloads too.
            // Searching raw JSON would both miss escaped Chinese and leak old unredacted text via counts.
            db.CreateFunction<string, string, bool>("dd_history_match", (id, payload) =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.Elapsed > TimeSpan.FromSeconds(15)) throw new UserDataException("limit", "历史搜索达到15秒上限；原数据保留，请结合日期、对象或状态缩小范围。");
                    if (memo.TryGetValue(id, out var cached)) return cached;
                    var record = Parse(payload);
                    var found = new bool[words.Length]; var remaining = words.Length;
                    foreach (var field in SearchFields(record))
                    {
                        token.ThrowIfCancellationRequested();
                        if (budget.Elapsed > TimeSpan.FromSeconds(15)) throw new UserDataException("limit", "历史搜索达到15秒上限；原数据保留，请结合日期、对象或状态缩小范围。");
                        var safe = HistoryPrivacy.Text(field, _redactor);
                        for (var i = 0; i < words.Length; i++)
                        {
                            if (!found[i] && safe.Contains(words[i], StringComparison.OrdinalIgnoreCase)) { found[i] = true; remaining--; }
                        }
                        if (remaining == 0) break;
                    }
                    var matches = remaining == 0;
                    if (memo.Count < 8192) memo[id] = matches;
                    return matches;
                }
                catch (Exception ex) when (ex is OperationCanceledException or UserDataException)
                { searchFailure = ex; throw; }
            });
            filters.Add("dd_history_match(r.id,r.payload)=1");
        }
        var where = filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters);
        try
        {
            command.CommandText = "SELECT COUNT(*) FROM records r" + where; var total = Convert.ToInt32(command.ExecuteScalar());
            command.CommandText = "SELECT r.id,r.started,r.kind,r.scope_label,r.completion,r.aggregate_status,(SELECT COUNT(*) FROM objects o WHERE o.record_id=r.id),(SELECT COUNT(*) FROM observations f WHERE f.record_id=r.id) FROM records r" + where + " ORDER BY r.started DESC,r.id DESC LIMIT $size OFFSET $offset";
            command.Parameters.AddWithValue("$size", size); command.Parameters.AddWithValue("$offset", page * size);
            using var reader = command.ExecuteReader(); var items = new List<HistorySummary>();
            while (reader.Read()) { token.ThrowIfCancellationRequested(); items.Add(new(Guid.Parse(reader.GetString(0)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), (HistoryScanKind)reader.GetInt32(2), HistoryPrivacy.Text(reader.GetString(3), _redactor), (ScanCompletion)reader.GetInt32(4), (DiagnosticStatus)reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7))); }
            return new HistoryPage(items, total, page, size);
        }
        catch (SqliteException) when (searchFailure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(searchFailure).Throw();
            throw;
        }
    }, token);
    private static IEnumerable<string> SearchFields(HistoryRecord record)
    {
        yield return record.ScopeLabel;
        yield return UiText.ScanKindName(record.Kind);
        yield return UiText.CompletionName(record.Completion);
        yield return record.Status.ToDisplayText();
        foreach (var item in record.Objects)
        {
            yield return item.Name; yield return item.Category;
            if (item.ProjectKind is { } kind) yield return UiText.ProjectName(kind);
            foreach (var finding in item.Observations)
            {
                yield return finding.RuleId; yield return finding.Title; yield return UiText.FindingTitle(finding.Title);
                yield return finding.Status.ToDisplayText(); yield return finding.Observed;
                yield return finding.Evidence; yield return finding.Cause;
                foreach (var suggestion in finding.Suggestions) yield return suggestion;
            }
        }
    }
    public Task<HistoryStatistics> StatisticsAsync(CancellationToken token = default) => Work(db =>
    {
        var statuses = Enum.GetValues<DiagnosticStatus>().ToDictionary(s => s, _ => 0); var completed = 0; var cancelled = 0; var failed = 0; var total = 0;
        using var command = db.CreateCommand(); command.CommandText = "SELECT aggregate_status,completion,COUNT(*) FROM records GROUP BY aggregate_status,completion";
        using var reader = command.ExecuteReader(); while (reader.Read())
        {
            var count = reader.GetInt32(2); total += count; var status = (DiagnosticStatus)reader.GetInt32(0); if (statuses.ContainsKey(status)) statuses[status] += count;
            switch ((ScanCompletion)reader.GetInt32(1)) { case ScanCompletion.Completed: completed += count; break; case ScanCompletion.Cancelled: cancelled += count; break; default: failed += count; break; }
        }
        return new HistoryStatistics(total, completed, cancelled, failed, statuses);
    }, token);
    public Task<int> DeleteAsync(Guid id, CancellationToken token = default) => Delete("id=$value", id.ToString(), token);
    public Task<int> DeleteBeforeAsync(DateTimeOffset cutoff, CancellationToken token = default) => Delete("started < $value", cutoff.ToUnixTimeMilliseconds(), token);
    public Task CreateSnapshotAsync(string destination, CancellationToken token = default) => Work(db => { SnapshotCore(db, destination); return true; }, token);

    internal void SnapshotCore(SqliteConnection source, string destination)
    {
        if (!LocalPathAccess.IsSafe(destination) || File.Exists(destination)) throw new UserDataException("path", "快照目标必须为尚不存在的安全本地文件。");
        using var target = new SqliteConnection(BuildConnection(destination)); target.Open(); source.BackupDatabase(target);
    }
    internal SqliteConnection OpenCore()
    {
        _accessGuard?.Invoke();
        if (!LocalPathAccess.IsSafe(DatabasePath)) throw new UserDataException("path", "历史数据目录必须为不含重解析点的本地路径。");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(BuildConnection(DatabasePath));
        try { connection.Open(); Initialize(connection); return connection; }
        catch { connection.Dispose(); throw; }
    }
    internal static string BuildConnection(string path, bool readOnly = false) => new SqliteConnectionStringBuilder
        { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 2, ForeignKeys = true }.ToString();

    internal static int CheckDatabase(string path)
    {
        using var db = new SqliteConnection(BuildConnection(path, true)); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA trusted_schema=OFF;"; command.ExecuteNonQuery(); command.CommandText = "PRAGMA quick_check";
        if (!string.Equals(command.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal)) throw new UserDataException("corrupt", "备份数据库完整性校验失败。");
        command.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(command.ExecuteScalar());
        if (version != SchemaVersion) throw new UserDataException("version", "数据库数据格式不兼容当前版本。");
        command.CommandText = "PRAGMA application_id";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1145327427) throw new UserDataException("format", "备份数据库应用标识不属于 DevDoctor。");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('trigger','view')";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new UserDataException("format", "数据库包含未支持的触发器或视图。");
        CheckSchema(db);
        command.CommandText = "PRAGMA foreign_key_check"; using (var foreignKeys = command.ExecuteReader()) if (foreignKeys.Read()) throw new UserDataException("format", "备份数据库存在无效外键关联。");
        command.CommandText = "SELECT id,started,finished,kind,scope_id,scope_label,completion,aggregate_status,payload,rule_version FROM records"; var count = 0; long objects = 0, observations = 0;
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var record = Parse(reader.GetString(8));
            if (reader.GetString(0) != record.Id.ToString() || reader.GetInt64(1) != record.StartedAt.ToUnixTimeMilliseconds() || reader.GetInt64(2) != record.FinishedAt.ToUnixTimeMilliseconds() || reader.GetInt32(3) != (int)record.Kind || reader.GetString(4) != record.ScopeId || reader.GetString(5) != record.ScopeLabel || reader.GetInt32(6) != (int)record.Completion || reader.GetInt32(7) != (int)record.Status || reader.GetInt32(9) != record.RuleVersion)
                throw new UserDataException("format", "备份数据库索引元数据与记录正文不一致。");
            CheckRecordIndex(db, record); objects += record.Objects.Count; observations += record.Objects.Sum(o => o.Observations.Count);
            if (++count > 1000000) throw new UserDataException("limit", "备份记录数量超过允许范围。");
        }
        command.CommandText = "SELECT COUNT(*) FROM objects"; if (Convert.ToInt64(command.ExecuteScalar()) != objects) throw new UserDataException("format", "备份数据库含有未关联对象。");
        command.CommandText = "SELECT COUNT(*) FROM observations"; if (Convert.ToInt64(command.ExecuteScalar()) != observations) throw new UserDataException("format", "备份数据库含有未关联证据。");
        return count;
    }
    private static void CheckSchema(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var tables = new HashSet<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        if (!tables.SetEquals(["records", "objects", "observations"])) throw new UserDataException("format", "备份数据库缺少必要的数据表，或包含未知表。");
        var definitions = new Dictionary<string, string> {
            ["records"] = "CREATE TABLE records(id TEXT PRIMARY KEY,started INTEGER NOT NULL,finished INTEGER NOT NULL,kind INTEGER NOT NULL,scope_id TEXT NOT NULL,scope_label TEXT NOT NULL,completion INTEGER NOT NULL,aggregate_status INTEGER NOT NULL,payload TEXT NOT NULL,rule_version INTEGER NOT NULL DEFAULT 1)",
            ["objects"] = "CREATE TABLE objects(record_id TEXT NOT NULL REFERENCES records(id) ON DELETE CASCADE,object_id TEXT NOT NULL,name TEXT NOT NULL,category TEXT NOT NULL,project_kind INTEGER,PRIMARY KEY(record_id,object_id))",
            ["observations"] = "CREATE TABLE observations(record_id TEXT NOT NULL REFERENCES records(id) ON DELETE CASCADE,object_id TEXT NOT NULL,observation_id TEXT NOT NULL,status INTEGER NOT NULL,PRIMARY KEY(record_id,object_id,observation_id))" };
        command.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        using (var reader = command.ExecuteReader()) while (reader.Read()) if (Canonical(reader.GetString(1)) != Canonical(definitions[reader.GetString(0)])) throw new UserDataException("format", "备份数据库表约束与当前格式不一致。");
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name NOT LIKE 'sqlite_%'";
        var indexes = new HashSet<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) indexes.Add(reader.GetString(0));
        if (!indexes.SetEquals(["records_started", "observations_status", "objects_project"])) throw new UserDataException("format", "备份数据库包含未知索引约束。");
        Columns("records", ["id|TEXT|0|1", "started|INTEGER|1|0", "finished|INTEGER|1|0", "kind|INTEGER|1|0", "scope_id|TEXT|1|0", "scope_label|TEXT|1|0", "completion|INTEGER|1|0", "aggregate_status|INTEGER|1|0", "payload|TEXT|1|0", "rule_version|INTEGER|1|0"]);
        Columns("objects", ["record_id|TEXT|1|1", "object_id|TEXT|1|2", "name|TEXT|1|0", "category|TEXT|1|0", "project_kind|INTEGER|0|0"]);
        Columns("observations", ["record_id|TEXT|1|1", "object_id|TEXT|1|2", "observation_id|TEXT|1|3", "status|INTEGER|1|0"]);
        foreach (var table in new[] { "objects", "observations" })
        {
            command.CommandText = $"PRAGMA foreign_key_list({table})";
            using var reader = command.ExecuteReader(); if (!reader.Read() || reader.GetString(2) != "records" || reader.GetString(3) != "record_id" || reader.GetString(4) != "id" || reader.GetString(6) != "CASCADE" || reader.Read()) throw new UserDataException("format", "备份数据库外键定义不受支持。");
        }
        Index("records", "records_started", ["started"]); Index("observations", "observations_status", ["status", "record_id"]); Index("objects", "objects_project", ["project_kind", "record_id"]);
        static string Canonical(string sql) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c) && c != ';')).ToUpperInvariant().Replace("CREATETABLEIFNOTEXISTS", "CREATETABLE", StringComparison.Ordinal);
        void Columns(string table, string[] expected)
        {
            command.CommandText = $"PRAGMA table_info({table})"; var actual = new List<string>();
            using var reader = command.ExecuteReader(); while (reader.Read()) actual.Add($"{reader.GetString(1)}|{reader.GetString(2).ToUpperInvariant()}|{reader.GetInt32(3)}|{reader.GetInt32(5)}");
            if (!actual.SequenceEqual(expected)) throw new UserDataException("format", "备份数据库字段和主键定义不受支持。");
        }
        void Index(string table, string name, string[] expected)
        {
            command.CommandText = $"PRAGMA index_list({table})"; var found = false;
            using (var metadata = command.ExecuteReader()) while (metadata.Read()) if (metadata.GetString(1) == name) { if (metadata.GetInt32(2) != 0 || metadata.GetInt32(4) != 0 || metadata.GetString(3) != "c") throw new UserDataException("format", "备份数据库索引包含不支持的唯一或部分约束。"); found = true; }
            if (!found) throw new UserDataException("format", "备份数据库索引不属于预期数据表。");
            command.CommandText = $"PRAGMA index_info({name})"; var columns = new List<string>(); using var reader = command.ExecuteReader(); while (reader.Read()) columns.Add(reader.GetString(2));
            if (!columns.SequenceEqual(expected)) throw new UserDataException("format", "备份数据库缺少必要索引。");
        }
    }
    private static void CheckRecordIndex(SqliteConnection db, HistoryRecord record)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT object_id,name,category,project_kind FROM objects WHERE record_id=$record"; command.Parameters.AddWithValue("$record", record.Id.ToString());
        var expectedObjects = record.Objects.ToDictionary(o => o.Id); var count = 0;
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            if (!expectedObjects.TryGetValue(reader.GetString(0), out var item) || item.Name != reader.GetString(1) || item.Category != reader.GetString(2) || (item.ProjectKind is null ? !reader.IsDBNull(3) : reader.IsDBNull(3) || (int)item.ProjectKind != reader.GetInt32(3))) throw new UserDataException("format", "备份数据库对象索引与证据正文不一致。");
            count++;
        }
        if (count != expectedObjects.Count) throw new UserDataException("format", "备份数据库对象索引不完整。");
        var expected = record.Objects.SelectMany(o => o.Observations.Select(f => (o.Id, f.Id, (int)f.Status))).ToHashSet(); count = 0;
        command.CommandText = "SELECT object_id,observation_id,status FROM observations WHERE record_id=$record";
        using (var reader = command.ExecuteReader()) while (reader.Read()) { if (!expected.Contains((reader.GetString(0), reader.GetString(1), reader.GetInt32(2)))) throw new UserDataException("format", "备份数据库状态索引与证据正文不一致。"); count++; }
        if (count != expected.Count) throw new UserDataException("format", "备份数据库证据索引不完整。");
    }
    private static void Initialize(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(command.ExecuteScalar());
        if (version > SchemaVersion) throw new UserDataException("version", "历史数据库来自较新版本；原文件已保留，请使用兼容版本或恢复已验证备份。");
        command.CommandText = "PRAGMA application_id"; var applicationId = Convert.ToInt32(command.ExecuteScalar());
        if (applicationId is not (0 or 1145327427)) throw new UserDataException("format", "该数据库不属于 DevDoctor，未修改原文件。");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','trigger','view') AND name NOT LIKE 'sqlite_%'";
        if (version == 0 && Convert.ToInt32(command.ExecuteScalar()) > 0) throw new UserDataException("format", "未识别的已有数据库不会被当作空白历史初始化。");
        command.CommandText = "PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;"; command.ExecuteNonQuery();
        if (version >= SchemaVersion) return;
        using var transaction = db.BeginTransaction(); command.Transaction = transaction;
        command.CommandText = "CREATE TABLE IF NOT EXISTS records(id TEXT PRIMARY KEY,started INTEGER NOT NULL,finished INTEGER NOT NULL,kind INTEGER NOT NULL,scope_id TEXT NOT NULL,scope_label TEXT NOT NULL,completion INTEGER NOT NULL,aggregate_status INTEGER NOT NULL,payload TEXT NOT NULL);" +
            "CREATE TABLE IF NOT EXISTS objects(record_id TEXT NOT NULL REFERENCES records(id) ON DELETE CASCADE,object_id TEXT NOT NULL,name TEXT NOT NULL,category TEXT NOT NULL,project_kind INTEGER,PRIMARY KEY(record_id,object_id));" +
            "CREATE TABLE IF NOT EXISTS observations(record_id TEXT NOT NULL REFERENCES records(id) ON DELETE CASCADE,object_id TEXT NOT NULL,observation_id TEXT NOT NULL,status INTEGER NOT NULL,PRIMARY KEY(record_id,object_id,observation_id));" +
            "CREATE INDEX IF NOT EXISTS records_started ON records(started DESC); CREATE INDEX IF NOT EXISTS observations_status ON observations(status,record_id); CREATE INDEX IF NOT EXISTS objects_project ON objects(project_kind,record_id);";
        command.ExecuteNonQuery(); command.CommandText = "ALTER TABLE records ADD COLUMN rule_version INTEGER NOT NULL DEFAULT 1; PRAGMA user_version=2; PRAGMA application_id=1145327427;"; command.ExecuteNonQuery();
        if (version == 1)
        {
            command.CommandText = "SELECT payload FROM records"; var records = new List<HistoryRecord>();
            using (var reader = command.ExecuteReader()) while (reader.Read()) records.Add(Parse(reader.GetString(0)));
            foreach (var record in records)
            {
                command.Parameters.Clear(); command.CommandText = "UPDATE records SET started=$started,finished=$finished,kind=$kind,scope_id=$scope,scope_label=$label,completion=$completion,aggregate_status=$status,rule_version=$rule WHERE id=$id";
                command.Parameters.AddWithValue("$started", record.StartedAt.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$finished", record.FinishedAt.ToUnixTimeMilliseconds()); command.Parameters.AddWithValue("$kind", (int)record.Kind); command.Parameters.AddWithValue("$scope", record.ScopeId); command.Parameters.AddWithValue("$label", record.ScopeLabel); command.Parameters.AddWithValue("$completion", (int)record.Completion); command.Parameters.AddWithValue("$status", (int)record.Status); command.Parameters.AddWithValue("$rule", record.RuleVersion); command.Parameters.AddWithValue("$id", record.Id.ToString()); command.ExecuteNonQuery();
                InsertIndex(db, transaction, record);
            }
        }
        transaction.Commit();
    }
    private static void InsertIndex(SqliteConnection db, SqliteTransaction transaction, HistoryRecord record)
    {
        using var command = db.CreateCommand(); command.Transaction = transaction;
        foreach (var item in record.Objects)
        {
            command.Parameters.Clear(); command.CommandText = "INSERT OR REPLACE INTO objects(record_id,object_id,name,category,project_kind) VALUES($record,$object,$name,$category,$project)";
            command.Parameters.AddWithValue("$record", record.Id.ToString()); command.Parameters.AddWithValue("$object", item.Id); command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$category", item.Category); command.Parameters.AddWithValue("$project", item.ProjectKind is { } kind ? (object)(int)kind : DBNull.Value); command.ExecuteNonQuery();
            foreach (var observation in item.Observations)
            {
                command.Parameters.Clear(); command.CommandText = "INSERT OR REPLACE INTO observations(record_id,object_id,observation_id,status) VALUES($record,$object,$id,$status)";
                command.Parameters.AddWithValue("$record", record.Id.ToString()); command.Parameters.AddWithValue("$object", item.Id); command.Parameters.AddWithValue("$id", observation.Id); command.Parameters.AddWithValue("$status", (int)observation.Status); command.ExecuteNonQuery();
            }
        }
    }
    private Task<int> Delete(string predicate, object value, CancellationToken token) => Work(db =>
    {
        using var transaction = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM records WHERE " + predicate; command.Parameters.AddWithValue("$value", value); var count = command.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); transaction.Commit(); return count;
    }, token);
    private async Task<T> Work<T>(Func<SqliteConnection, T> operation, CancellationToken token)
    {
        await DataGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => { token.ThrowIfCancellationRequested(); using var db = OpenCore(); return operation(db); }, token).ConfigureAwait(false);
        }
        catch (SqliteException ex) { throw Translate(ex); }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { throw new UserDataException("corrupt", "历史字段内容损坏，原数据库已保留。请从已验证的完整备份恢复。", ex); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new UserDataException("io", "历史文件不可读写或当前目录不可用；原数据未被清空。", ex); }
        finally { DataGate.Release(); }
    }
    internal static UserDataException Translate(SqliteException ex) => ex.SqliteErrorCode switch {
        5 or 6 => new("busy", "历史数据库正在被占用，已达到短等待上限。请稍后重试；原历史保留。", ex),
        11 or 26 => new("corrupt", "历史数据库损坏或不是 SQLite 格式。原文件已保留，可从已验证备份恢复；不会自动清空。", ex),
        8 or 10 or 13 or 14 => new("io", "无法安全访问历史数据库，可能只读、磁盘空间不足或权限受限。原文件已保留。", ex),
        _ => new("database", "历史数据库操作未完成；原文件保留，请查看备份恢复说明。", ex) };
    internal static HistoryRecord Parse(string payload)
    {
        if (payload.Length > MaxPayload) throw new UserDataException("limit", "单条历史超过读取上限。");
        try { var record = JsonSerializer.Deserialize<HistoryRecord>(payload, JsonOptions) ?? throw new JsonException(); ValidateRecord(record); return record; }
        catch (JsonException ex) { throw new UserDataException("corrupt", "历史记录内容无法解析，原数据库未清空。", ex); }
    }
    internal static void ValidateRecord(HistoryRecord record)
    {
        if (record.FormatVersion != 1 || record.RuleVersion is < 1 or > 100000 || record.Id == Guid.Empty || !Text(record.SoftwareVersion, 64) || !Key(record.ScopeId) || !Text(record.ScopeLabel) || record.Notes is null || record.Notes.Count > 32 || record.Notes.Any(n => !Text(n)) || !Enum.IsDefined(record.Kind) || !Enum.IsDefined(record.Completion) || record.Objects is null || record.Objects.Count > 512 || record.Objects.Any(o => o is null) || record.Objects.Select(o => o.Id).Distinct().Count() != record.Objects.Count ||
            record.Objects.Any(o => !Key(o.Id) || !Text(o.Name) || !Text(o.Category) || o.Version is not null && !Text(o.Version) || o.ProjectKind is { } kind && !Enum.IsDefined(kind) || o.Observations is null || o.Observations.Count > 128 || !Enum.IsDefined(o.Coverage) || !Enum.IsDefined(o.Presence) || o.Observations.Any(f => f is null) || o.Observations.Select(f => f.Id).Distinct().Count() != o.Observations.Count ||
                o.Observations.Any(f => !Key(f.Id) || !Text(f.RuleId, 512) || !Text(f.Title) || !Text(f.Observed) || !Text(f.Evidence) || !Text(f.Cause) || !Enum.IsDefined(f.Status) || f.Suggestions is null || f.Suggestions.Count > 16 || f.Suggestions.Any(s => !Text(s)))) || record.Objects.Sum(o => o.Observations.Count) > 4096)
            throw new UserDataException("format", "历史记录版本、状态或大小不符合当前数据格式。");
        static bool Text(string? value, int max = 8192) => value is not null && value.Length <= max;
        static bool Key(string? value) => value is { Length: > 0 and <= 256 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or ':');
    }
    private HistoryRecord Sanitize(HistoryRecord record)
    {
        string Safe(string? text) { try { var result = _redactor.Redact(text ?? ""); return result.Length <= 4096 ? result : result[..4096] + "（已限制长度）"; } catch (RegexMatchTimeoutException) { return "<脱敏未完成，原文未保存>"; } }
        return record with { ScopeLabel = Safe(record.ScopeLabel), Notes = record.Notes.Take(32).Select(Safe).ToArray(), Objects = record.Objects.Select(o => o with {
            Name = Safe(o.Name), Category = Safe(o.Category), Version = o.Version is null ? null : Safe(o.Version), Observations = o.Observations.Select(f => f with {
                Title = Safe(f.Title), Observed = Safe(f.Observed), Evidence = Safe(f.Evidence), Cause = Safe(f.Cause), Suggestions = f.Suggestions.Take(16).Select(Safe).ToArray() }).ToArray() }).ToArray() };
    }
}
