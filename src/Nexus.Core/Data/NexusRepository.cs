using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nexus.Core.Models;

namespace Nexus.Core.Data;

public sealed class NexusRepository : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NexusRepository(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS reminders (
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                due_at TEXT NOT NULL,
                enabled INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_reminders_due ON reminders(enabled, due_at);
            CREATE TABLE IF NOT EXISTS routines (
                id TEXT PRIMARY KEY,
                json TEXT NOT NULL,
                enabled INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit (
                id TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                category TEXT NOT NULL,
                status TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_audit_timestamp ON audit(timestamp DESC);
            CREATE TABLE IF NOT EXISTS memory (
                id TEXT PRIMARY KEY,
                category TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                pinned INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                timestamp TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_thread ON messages(thread_id, timestamp);
            CREATE TABLE IF NOT EXISTS task_workspaces (
                id TEXT PRIMARY KEY,
                updated_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_task_workspaces_updated ON task_workspaces(updated_at DESC);
            CREATE TABLE IF NOT EXISTS task_command_history (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                submitted_at TEXT NOT NULL,
                json TEXT NOT NULL,
                FOREIGN KEY(workspace_id) REFERENCES task_workspaces(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_task_command_history_workspace
                ON task_command_history(workspace_id, submitted_at DESC);
            CREATE TABLE IF NOT EXISTS task_schedules (
                id TEXT PRIMARY KEY,
                workspace_id TEXT NOT NULL,
                next_run_at TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                json TEXT NOT NULL,
                FOREIGN KEY(workspace_id) REFERENCES task_workspaces(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS ix_task_schedules_due
                ON task_schedules(enabled, next_run_at);
            CREATE TABLE IF NOT EXISTS capability_profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                kind TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                remote_enabled INTEGER NOT NULL,
                updated_at TEXT NOT NULL,
                json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_capability_profiles_kind
                ON capability_profiles(kind, enabled, remote_enabled);
            CREATE TABLE IF NOT EXISTS capability_audit (
                id TEXT PRIMARY KEY,
                timestamp TEXT NOT NULL,
                task_id TEXT NOT NULL,
                capability_id TEXT NOT NULL,
                tool TEXT NOT NULL,
                status TEXT NOT NULL,
                risk TEXT NOT NULL,
                detail TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_capability_audit_timestamp
                ON capability_audit(timestamp DESC);
            """;

        await ExecuteAsync(sql, cancellationToken);
    }

    public async Task<AssistantSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM settings WHERE key='assistant'";
            var value = await command.ExecuteScalarAsync(cancellationToken) as string;
            return value is null
                ? new AssistantSettings()
                : JsonSerializer.Deserialize<AssistantSettings>(value) ?? new AssistantSettings();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SaveSettingsAsync(AssistantSettings settings, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO settings(key,json,updated_at) VALUES('assistant',$json,$now) " +
            "ON CONFLICT(key) DO UPDATE SET json=$json,updated_at=$now",
            cancellationToken,
            ("$json", JsonSerializer.Serialize(settings)),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));

    public Task SaveReminderAsync(Reminder reminder, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO reminders(id,json,due_at,enabled) VALUES($id,$json,$due,$enabled) " +
            "ON CONFLICT(id) DO UPDATE SET json=$json,due_at=$due,enabled=$enabled",
            cancellationToken,
            ("$id", reminder.Id),
            ("$json", JsonSerializer.Serialize(reminder)),
            ("$due", reminder.DueAt.ToString("O")),
            ("$enabled", reminder.IsEnabled ? 1 : 0));

    public async Task<IReadOnlyList<Reminder>> GetRemindersAsync(bool enabledOnly = false, CancellationToken cancellationToken = default)
    {
        var items = new List<Reminder>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = enabledOnly
                ? "SELECT json FROM reminders WHERE enabled=1 ORDER BY due_at"
                : "SELECT json FROM reminders ORDER BY due_at";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = JsonSerializer.Deserialize<Reminder>(reader.GetString(0));
                if (item is not null) items.Add(item);
            }
        }
        finally
        {
            _gate.Release();
        }
        return items;
    }

    public Task DeleteReminderAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM reminders WHERE id=$id", cancellationToken, ("$id", id));

    public Task SaveRoutineAsync(Routine routine, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO routines(id,json,enabled) VALUES($id,$json,$enabled) " +
            "ON CONFLICT(id) DO UPDATE SET json=$json,enabled=$enabled",
            cancellationToken,
            ("$id", routine.Id),
            ("$json", JsonSerializer.Serialize(routine)),
            ("$enabled", routine.IsEnabled ? 1 : 0));

    public async Task<IReadOnlyList<Routine>> GetRoutinesAsync(CancellationToken cancellationToken = default)
    {
        return await ReadJsonListAsync<Routine>("SELECT json FROM routines ORDER BY id", cancellationToken);
    }

    public Task AddAuditAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT OR REPLACE INTO audit(id,timestamp,category,status,json) VALUES($id,$timestamp,$category,$status,$json)",
            cancellationToken,
            ("$id", entry.Id),
            ("$timestamp", entry.Timestamp.ToString("O")),
            ("$category", entry.Category),
            ("$status", entry.Status),
            ("$json", JsonSerializer.Serialize(entry)));

    public Task SaveTaskWorkspaceAsync(TaskWorkspace workspace, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO task_workspaces(id,updated_at,json) VALUES($id,$updated,$json) " +
            "ON CONFLICT(id) DO UPDATE SET updated_at=$updated,json=$json",
            cancellationToken, ("$id", workspace.Id), ("$updated", workspace.UpdatedAt.ToString("O")),
            ("$json", JsonSerializer.Serialize(workspace)));

    public async Task<IReadOnlyList<TaskWorkspace>> GetTaskWorkspacesAsync(CancellationToken cancellationToken = default) =>
        await ReadJsonListAsync<TaskWorkspace>(
            "SELECT json FROM task_workspaces ORDER BY updated_at DESC", cancellationToken);

    public async Task<TaskWorkspace?> GetTaskWorkspaceAsync(string id, CancellationToken cancellationToken = default) =>
        (await ReadJsonListAsync<TaskWorkspace>(
            "SELECT json FROM task_workspaces WHERE id=$id LIMIT 1", cancellationToken, ("$id", id))).FirstOrDefault();

    public Task DeleteTaskWorkspaceAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM task_workspaces WHERE id=$id", cancellationToken, ("$id", id));

    public Task SaveTaskCommandAsync(TaskCommandHistory command, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO task_command_history(id,workspace_id,submitted_at,json) VALUES($id,$workspace,$submitted,$json) " +
            "ON CONFLICT(id) DO UPDATE SET submitted_at=$submitted,json=$json",
            cancellationToken, ("$id", command.Id), ("$workspace", command.WorkspaceId),
            ("$submitted", command.SubmittedAt.ToString("O")), ("$json", JsonSerializer.Serialize(command)));

    public async Task<IReadOnlyList<TaskCommandHistory>> GetTaskCommandsAsync(
        string workspaceId, int limit = 100, CancellationToken cancellationToken = default) =>
        await ReadJsonListAsync<TaskCommandHistory>(
            "SELECT json FROM task_command_history WHERE workspace_id=$workspace " +
            "ORDER BY submitted_at DESC LIMIT $limit", cancellationToken,
            ("$workspace", workspaceId), ("$limit", limit));

    public Task DeleteTaskCommandsAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM task_command_history WHERE workspace_id=$workspace", cancellationToken,
            ("$workspace", workspaceId));

    public Task SaveTaskScheduleAsync(TaskSchedule schedule, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO task_schedules(id,workspace_id,next_run_at,enabled,json) VALUES($id,$workspace,$next,$enabled,$json) " +
            "ON CONFLICT(id) DO UPDATE SET workspace_id=$workspace,next_run_at=$next,enabled=$enabled,json=$json",
            cancellationToken, ("$id", schedule.Id), ("$workspace", schedule.WorkspaceId),
            ("$next", schedule.NextRunAt.ToString("O")), ("$enabled", schedule.IsEnabled ? 1 : 0),
            ("$json", JsonSerializer.Serialize(schedule)));

    public async Task<IReadOnlyList<TaskSchedule>> GetTaskSchedulesAsync(
        string? workspaceId = null, bool enabledOnly = false, CancellationToken cancellationToken = default)
    {
        var filters = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        if (!string.IsNullOrWhiteSpace(workspaceId)) { filters.Add("workspace_id=$workspace"); parameters.Add(("$workspace", workspaceId)); }
        if (enabledOnly) filters.Add("enabled=1");
        var where = filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters);
        return await ReadJsonListAsync<TaskSchedule>("SELECT json FROM task_schedules" + where + " ORDER BY next_run_at",
            cancellationToken, parameters.ToArray());
    }

    public Task DeleteTaskSchedulesAsync(string workspaceId, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM task_schedules WHERE workspace_id=$workspace", cancellationToken,
            ("$workspace", workspaceId));

    public Task SaveCapabilityProfileAsync(CapabilityProfile profile, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO capability_profiles(id,name,kind,enabled,remote_enabled,updated_at,json) " +
            "VALUES($id,$name,$kind,$enabled,$remote,$updated,$json) " +
            "ON CONFLICT(id) DO UPDATE SET name=$name,kind=$kind,enabled=$enabled," +
            "remote_enabled=$remote,updated_at=$updated,json=$json",
            cancellationToken,
            ("$id", profile.Id), ("$name", profile.Name), ("$kind", profile.Kind.ToString()),
            ("$enabled", profile.IsEnabled ? 1 : 0), ("$remote", profile.AllowRemoteTasks ? 1 : 0),
            ("$updated", profile.UpdatedAt.ToString("O")), ("$json", JsonSerializer.Serialize(profile)));

    public async Task<IReadOnlyList<CapabilityProfile>> GetCapabilityProfilesAsync(
        bool enabledOnly = false, bool remoteOnly = false, CancellationToken cancellationToken = default)
    {
        var filters = new List<string>();
        if (enabledOnly) filters.Add("enabled=1");
        if (remoteOnly) filters.Add("remote_enabled=1");
        var where = filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters);
        return await ReadJsonListAsync<CapabilityProfile>(
            "SELECT json FROM capability_profiles" + where + " ORDER BY kind,name", cancellationToken);
    }

    public async Task<CapabilityProfile?> GetCapabilityProfileAsync(
        string id, CancellationToken cancellationToken = default) =>
        (await ReadJsonListAsync<CapabilityProfile>(
            "SELECT json FROM capability_profiles WHERE id=$id LIMIT 1", cancellationToken,
            ("$id", id))).FirstOrDefault();

    public Task DeleteCapabilityProfileAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteAsync("DELETE FROM capability_profiles WHERE id=$id", cancellationToken, ("$id", id));

    public Task AddCapabilityAuditAsync(CapabilityAuditEntry entry, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO capability_audit(id,timestamp,task_id,capability_id,tool,status,risk,detail) " +
            "VALUES($id,$timestamp,$task,$capability,$tool,$status,$risk,$detail)",
            cancellationToken,
            ("$id", entry.Id), ("$timestamp", entry.Timestamp.ToString("O")), ("$task", entry.TaskId),
            ("$capability", entry.CapabilityId), ("$tool", entry.Tool), ("$status", entry.Status),
            ("$risk", entry.Risk.ToString()), ("$detail", entry.Detail ?? string.Empty));

    public async Task<IReadOnlyList<CapabilityAuditEntry>> GetRecentCapabilityAuditAsync(
        int limit = 200, CancellationToken cancellationToken = default)
    {
        var items = new List<CapabilityAuditEntry>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,timestamp,task_id,capability_id,tool,status,risk,detail " +
                "FROM capability_audit ORDER BY timestamp DESC LIMIT $limit";
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Enum.TryParse<ActionRisk>(reader.GetString(6), out var risk);
                items.Add(new CapabilityAuditEntry(reader.GetString(0), DateTimeOffset.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), risk,
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }
        finally { _gate.Release(); }
        return items;
    }

    public async Task<IReadOnlyList<AuditEntry>> GetRecentAuditAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        return await ReadJsonListAsync<AuditEntry>(
            "SELECT json FROM audit ORDER BY timestamp DESC LIMIT $limit", cancellationToken, ("$limit", limit));
    }

    public Task SaveMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT OR REPLACE INTO messages(id,thread_id,timestamp,json) VALUES($id,$thread,$timestamp,$json)",
            cancellationToken,
            ("$id", message.Id),
            ("$thread", message.ThreadId),
            ("$timestamp", message.Timestamp.ToString("O")),
            ("$json", JsonSerializer.Serialize(message)));

    public async Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string threadId, int limit = 200, CancellationToken cancellationToken = default)
    {
        return await ReadJsonListAsync<ConversationMessage>(
            "SELECT json FROM messages WHERE thread_id=$thread ORDER BY timestamp DESC LIMIT $limit",
            cancellationToken, ("$thread", threadId), ("$limit", limit));
    }

    public Task SaveMemoryAsync(MemoryEntry memory, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            "INSERT INTO memory(id,category,content,created_at,updated_at,pinned) VALUES($id,$category,$content,$created,$updated,$pinned) " +
            "ON CONFLICT(id) DO UPDATE SET category=$category,content=$content,updated_at=$updated,pinned=$pinned",
            cancellationToken,
            ("$id", memory.Id), ("$category", memory.Category), ("$content", memory.Content),
            ("$created", memory.CreatedAt.ToString("O")), ("$updated", memory.UpdatedAt.ToString("O")),
            ("$pinned", memory.IsPinned ? 1 : 0));

    public async Task<IReadOnlyList<MemoryEntry>> SearchMemoryAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
    {
        var results = new List<MemoryEntry>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,category,content,created_at,updated_at,pinned FROM memory " +
                "WHERE content LIKE $query OR category LIKE $query ORDER BY pinned DESC,updated_at DESC LIMIT $limit";
            command.Parameters.AddWithValue("$query", $"%{query}%");
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new MemoryEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    DateTimeOffset.Parse(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt32(5) == 1));
            }
        }
        finally
        {
            _gate.Release();
        }
        return results;
    }

    private async Task<IReadOnlyList<T>> ReadJsonListAsync<T>(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        var items = new List<T>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var item = JsonSerializer.Deserialize<T>(reader.GetString(0));
                if (item is not null) items.Add(item);
            }
        }
        finally
        {
            _gate.Release();
        }
        return items;
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public ValueTask DisposeAsync()
    {
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
