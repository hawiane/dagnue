using System.Text.Json;
using System.Text.Json.Nodes;
using Nexus.Core.Codex;
using Nexus.Core.Data;
using Nexus.Core.Models;
using Nexus.Core.SystemServices;

namespace Nexus.App.Services;

public sealed record MiniAppRunResult(TaskWorkspace Workspace, TaskCommandHistory Command);

public sealed class MiniAppAgentService
{
    private const string Model = "gpt-5.6-sol";
    private readonly NexusRepository _repository;
    private readonly CodexAppServerClient _codex;
    private readonly MiniAppSnapshotService _snapshots = new();

    public MiniAppAgentService(NexusRepository repository, CodexAppServerClient codex)
    {
        _repository = repository;
        _codex = codex;
    }

    public string GetDirectory(string workspaceId) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "task-workspaces", workspaceId);

    public async Task EnsureInitializedAsync(TaskWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var directory = GetDirectory(workspace.Id);
        Directory.CreateDirectory(directory);
        var appMarkdown = Path.Combine(directory, "APP.md");
        if (!File.Exists(appMarkdown))
        {
            var text = $"# {workspace.Title}\n\n## Purpose\n\n{workspace.Instruction}\n\n## Current status\n\nNew mini system; no commands have completed yet.\n\n## Operating notes\n\n- This workspace is independent from the DAGNUE mother application.\n- Keep this file current after every command.\n";
            await File.WriteAllTextAsync(appMarkdown, text, cancellationToken);
        }
    }

    public async Task<MiniAppRunResult> ExecuteAsync(TaskWorkspace workspace, string request,
        IReadOnlyList<string>? images = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(workspace, cancellationToken);
        var directory = GetDirectory(workspace.Id);
        var schedules = await _repository.GetTaskSchedulesAsync(workspace.Id, cancellationToken: cancellationToken);
        var schedulesJson = JsonSerializer.Serialize(schedules);
        var command = new TaskCommandHistory(Guid.NewGuid().ToString("N"), workspace.Id, request,
            DateTimeOffset.Now, null, "running", null, images?.FirstOrDefault(),
            BeforeFormJson: workspace.FormJson, BeforeSchedulesJson: schedulesJson);

        foreach (var old in (await _repository.GetTaskCommandsAsync(workspace.Id, cancellationToken: cancellationToken))
                     .Where(x => x.IsUndone && !x.RedoInvalidated))
            await _repository.SaveTaskCommandAsync(old with { RedoInvalidated = true }, cancellationToken);

        try
        {
            command = command with { BeforeSnapshotPath = await _snapshots.CaptureAsync(directory, command.Id, "before", cancellationToken) };
            await _repository.SaveTaskCommandAsync(command, cancellationToken);
            if (!_codex.IsConnected) await _codex.StartAsync(cancellationToken: cancellationToken);
            var threadId = workspace.ThreadId;
            if (string.IsNullOrWhiteSpace(threadId))
                threadId = await _codex.StartThreadAsync(Model, directory, workspaceWrite: true, cancellationToken);
            else
            {
                try { threadId = await _codex.ResumeThreadAsync(threadId, directory, workspaceWrite: true, cancellationToken); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("thread", StringComparison.OrdinalIgnoreCase))
                {
                    threadId = await _codex.StartThreadAsync(Model, directory, workspaceWrite: true, cancellationToken);
                }
            }

            var appMarkdown = await File.ReadAllTextAsync(Path.Combine(directory, "APP.md"), cancellationToken);
            var prompt = BuildPrompt(workspace, request, appMarkdown, schedulesJson);
            var result = await _codex.RunTurnAsync(prompt, threadId, images,
                new CodexTurnOptions(directory, WorkspaceWrite: true, NetworkAccess: true), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Error)) throw new InvalidOperationException(result.Error);

            var schema = NormalizeFormSchema(ExtractJson(result.FinalText), workspace.FormJson);
            var root = JsonNode.Parse(schema) as JsonObject ?? throw new InvalidOperationException("Mini-system AI returned invalid JSON.");
            if (root["controls"] is not JsonArray controls || controls.Count > 30)
                throw new InvalidOperationException("Mini-system AI returned an invalid form schema.");

            if (root["appMarkdown"]?.GetValue<string>() is { Length: > 0 } updatedMarkdown)
                await File.WriteAllTextAsync(Path.Combine(directory, "APP.md"), updatedMarkdown, cancellationToken);

            if (root["schedules"] is JsonArray scheduleNodes)
            {
                schedules = ParseSchedules(scheduleNodes, workspace.Id);
                await _repository.DeleteTaskSchedulesAsync(workspace.Id, cancellationToken);
                foreach (var schedule in schedules) await _repository.SaveTaskScheduleAsync(schedule, cancellationToken);
            }

            workspace = workspace with
            {
                ThreadId = result.ThreadId, FormJson = schema, Status = "ready",
                UpdatedAt = DateTimeOffset.Now, FormContractVersion = 3
            };
            await _repository.SaveTaskWorkspaceAsync(workspace, cancellationToken);
            var afterSchedules = JsonSerializer.Serialize(await _repository.GetTaskSchedulesAsync(workspace.Id, cancellationToken: cancellationToken));
            command = command with
            {
                Status = "completed", CompletedAt = DateTimeOffset.Now,
                ResultSummary = GetMessage(schema), AfterFormJson = schema, AfterSchedulesJson = afterSchedules,
                AfterSnapshotPath = await _snapshots.CaptureAsync(directory, command.Id, "after", cancellationToken)
            };
            await _repository.SaveTaskCommandAsync(command, cancellationToken);
            return new MiniAppRunResult(workspace, command);
        }
        catch (Exception ex)
        {
            command = command with { Status = "error", CompletedAt = DateTimeOffset.Now, ResultSummary = Truncate(ex.Message) };
            await _repository.SaveTaskCommandAsync(command, cancellationToken);
            throw;
        }
    }

    public async Task<TaskWorkspace?> UndoAsync(TaskWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var command = (await _repository.GetTaskCommandsAsync(workspace.Id, cancellationToken: cancellationToken))
            .FirstOrDefault(x => x.Status == "completed" && !x.IsUndone && !string.IsNullOrWhiteSpace(x.BeforeSnapshotPath));
        if (command is null) return null;
        await _snapshots.RestoreAsync(GetDirectory(workspace.Id), command.BeforeSnapshotPath!, cancellationToken);
        await RestoreSchedulesAsync(workspace.Id, command.BeforeSchedulesJson, cancellationToken);
        workspace = await ResetThreadAsync(workspace with
        {
            FormJson = command.BeforeFormJson ?? workspace.FormJson, UpdatedAt = DateTimeOffset.Now, Status = "ready"
        }, cancellationToken);
        await _repository.SaveTaskCommandAsync(command with { IsUndone = true }, cancellationToken);
        return workspace;
    }

    public async Task<TaskWorkspace?> RedoAsync(TaskWorkspace workspace, CancellationToken cancellationToken = default)
    {
        var command = (await _repository.GetTaskCommandsAsync(workspace.Id, cancellationToken: cancellationToken))
            .Where(x => x.Status == "completed" && x.IsUndone && !x.RedoInvalidated && !string.IsNullOrWhiteSpace(x.AfterSnapshotPath))
            .LastOrDefault();
        if (command is null) return null;
        await _snapshots.RestoreAsync(GetDirectory(workspace.Id), command.AfterSnapshotPath!, cancellationToken);
        await RestoreSchedulesAsync(workspace.Id, command.AfterSchedulesJson, cancellationToken);
        workspace = await ResetThreadAsync(workspace with
        {
            FormJson = command.AfterFormJson ?? workspace.FormJson, UpdatedAt = DateTimeOffset.Now, Status = "ready"
        }, cancellationToken);
        await _repository.SaveTaskCommandAsync(command with { IsUndone = false }, cancellationToken);
        return workspace;
    }

    public async Task<TaskWorkspace> ResetConversationAsync(TaskWorkspace workspace, bool clearCommandHistory,
        CancellationToken cancellationToken = default)
    {
        workspace = await ResetThreadAsync(workspace, cancellationToken);
        if (clearCommandHistory)
        {
            await _repository.DeleteTaskCommandsAsync(workspace.Id, cancellationToken);
            var history = Path.Combine(GetDirectory(workspace.Id), ".dagnue", "history");
            if (Directory.Exists(history)) Directory.Delete(history, recursive: true);
        }
        return workspace;
    }

    private async Task<TaskWorkspace> ResetThreadAsync(TaskWorkspace workspace, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(workspace.ThreadId))
        {
            try
            {
                if (!_codex.IsConnected) await _codex.StartAsync(cancellationToken: cancellationToken);
                await _codex.DeleteThreadAsync(workspace.ThreadId, cancellationToken);
            }
            catch (InvalidOperationException) { }
        }
        workspace = workspace with { ThreadId = null, UpdatedAt = DateTimeOffset.Now };
        await _repository.SaveTaskWorkspaceAsync(workspace, cancellationToken);
        return workspace;
    }

    private async Task RestoreSchedulesAsync(string workspaceId, string? json, CancellationToken cancellationToken)
    {
        await _repository.DeleteTaskSchedulesAsync(workspaceId, cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return;
        foreach (var schedule in JsonSerializer.Deserialize<List<TaskSchedule>>(json) ?? [])
            await _repository.SaveTaskScheduleAsync(schedule, cancellationToken);
    }

    private static string BuildPrompt(TaskWorkspace workspace, string request, string appMarkdown, string schedulesJson) =>
        "You are the autonomous agent inside ONE DAGNUE mini system. You may inspect, create, edit, and run files " +
        "inside the current mini-system working directory. Never modify DAGNUE's mother source or write outside this workspace. " +
        "Use tools when the request requires real work; do not merely claim an action succeeded. APP.md is the durable source of truth. " +
        "Read its current contents below before acting and return its complete updated contents in appMarkdown. " +
        "Return ONLY one JSON object with: message, appMarkdown, controls, and schedules. Controls require stable id/type/label; " +
        "allowed types are text,status,input,checkbox,button; maximum 30. Preserve current form state unless the user asks to change it. " +
        "Schedules is the complete schedule list. Each schedule requires id,name,prompt,nextRunAt ISO-8601,recurrence " +
        "(none,daily,weekdays,weekly,monthly),enabled. Scheduled prompts run in this same workspace.\n\n" +
        $"MINI SYSTEM PURPOSE:\n{workspace.Instruction}\n\nCURRENT APP.md:\n{appMarkdown}\n\n" +
        $"CURRENT FORM JSON:\n{workspace.FormJson}\n\nCURRENT SCHEDULES:\n{schedulesJson}\n\nUSER COMMAND:\n{request}";

    private static List<TaskSchedule> ParseSchedules(JsonArray nodes, string workspaceId)
    {
        var result = new List<TaskSchedule>();
        foreach (var node in nodes.OfType<JsonObject>())
        {
            var name = node["name"]?.GetValue<string>();
            var prompt = node["prompt"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prompt) ||
                !DateTimeOffset.TryParse(node["nextRunAt"]?.GetValue<string>(), out var next)) continue;
            Enum.TryParse<RecurrenceKind>(node["recurrence"]?.GetValue<string>() ?? "none", true, out var recurrence);
            result.Add(new TaskSchedule(node["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), workspaceId,
                name, prompt, next, recurrence, node["enabled"]?.GetValue<bool>() ?? true, DateTimeOffset.Now));
        }
        return result;
    }

    private static string NormalizeFormSchema(string schema, string currentSchema)
    {
        var previous = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(currentSchema) is JsonObject oldRoot && oldRoot["controls"] is JsonArray oldControls)
                for (var i = 0; i < oldControls.Count; i++) if (oldControls[i] is JsonObject old)
                    previous[old["id"]?.GetValue<string>() ?? $"control-{i}"] = old;
        }
        catch (JsonException) { }
        var root = JsonNode.Parse(schema) as JsonObject ?? throw new InvalidOperationException("Task AI returned invalid JSON.");
        var controls = root["controls"] as JsonArray ?? throw new InvalidOperationException("Task AI returned no controls.");
        for (var i = 0; i < controls.Count; i++)
        {
            if (controls[i] is not JsonObject control) continue;
            var id = control["id"]?.GetValue<string>() ?? $"control-{i}"; control["id"] = id;
            previous.TryGetValue(id, out var prior);
            var type = control["type"]?.GetValue<string>()?.ToLowerInvariant();
            if (type == "checkbox" && control["checked"] is null) control["checked"] = prior?["checked"]?.DeepClone() ?? false;
            if (type == "input" && control["value"] is null) control["value"] = prior?["value"]?.DeepClone() ?? string.Empty;
        }
        return root.ToJsonString();
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{'); var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Mini-system AI did not return its state JSON.");
        return text[start..(end + 1)];
    }

    private static string GetMessage(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.TryGetProperty("message", out var value) ? value.GetString() ?? "Command completed." : "Command completed.";
    }

    private static string Truncate(string value) => value.Length > 240 ? value[..240] + "..." : value;
}
