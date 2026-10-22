using System.Text.Json.Serialization;

namespace Nexus.Core.Models;

public enum BrainState
{
    Sleeping,
    Idle,
    Listening,
    Understanding,
    Reasoning,
    Acting,
    AwaitingApproval,
    Completed,
    Warning,
    Offline
}

public enum PermissionLevel
{
    Observe,
    Suggest,
    Confirm,
    TrustedRoutines
}

public enum ActionRisk
{
    ReadOnly,
    Low,
    Medium,
    High,
    Critical
}

public enum RecurrenceKind
{
    None,
    Daily,
    Weekdays,
    Weekly,
    Monthly
}

public enum TaskProgressStatus
{
    NotYet,
    Doing,
    Done
}

public enum TaskUrgency
{
    Low,
    Normal,
    High,
    Critical
}

public enum UserTaskType
{
    OneTime,
    Recurring,
    Someday
}

public enum TaskExecutionMode
{
    Manual,
    AiAssisted
}

public sealed record TaskActivity(
    string Id,
    DateTimeOffset Timestamp,
    string Kind,
    string Summary,
    string? ResultPath = null);

public sealed record AssistantSettings(
    string AssistantName = "DAGNUE",
    PermissionLevel PermissionLevel = PermissionLevel.Confirm,
    bool StartWithWindows = true,
    bool ShowAtStartup = true,
    bool NotificationsEnabled = true,
    bool VoiceEnabled = true,
    bool WakeWordEnabled = false,
    bool ReducedMotion = false,
    bool LowPowerMode = false,
    bool ScreenContextEnabled = false,
    bool ClipboardContextEnabled = false,
    string Hotkey = "Ctrl+Shift+Space",
    string CodexModel = "",
    string WorkspacePath = "");

public sealed record ActionRequest(
    string Id,
    string Tool,
    string Summary,
    string? Detail,
    ActionRisk Risk,
    bool IsDestructive,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string>? Parameters = null);

public sealed record ActionDecision(
    bool Allowed,
    bool RequiresConfirmation,
    string Reason);

public sealed record Reminder(
    string Id,
    string Title,
    string? Notes,
    DateTimeOffset DueAt,
    RecurrenceKind Recurrence,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastTriggeredAt = null,
    TaskProgressStatus Status = TaskProgressStatus.NotYet,
    TaskUrgency Urgency = TaskUrgency.Normal,
    bool IsToday = false,
    bool HasSchedule = true,
    DateTimeOffset? Deadline = null,
    string? AiInstruction = null,
    DateTimeOffset? AiLastRunAt = null,
    string? AiLastResult = null,
    UserTaskType TaskType = UserTaskType.OneTime,
    TaskExecutionMode ExecutionMode = TaskExecutionMode.Manual,
    DateTimeOffset? CompletedAt = null,
    string? ResultSummary = null,
    string? ResultPath = null,
    string? AiThreadId = null,
    string? AiSuggestion = null,
    DateTimeOffset? AiSuggestionAt = null,
    IReadOnlyList<TaskActivity>? Activity = null,
    IReadOnlyList<string>? Tags = null);

public sealed record Routine(
    string Id,
    string Name,
    string Trigger,
    string Prompt,
    bool IsEnabled,
    bool IsTrusted,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt = null);

public sealed record TaskWorkspace(
    string Id,
    string Title,
    string Instruction,
    string? ThreadId,
    string FormJson,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int FormContractVersion = 3);

public sealed record TaskCommandHistory(
    string Id,
    string WorkspaceId,
    string Command,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    string Status,
    string? ResultSummary = null,
    string? AttachmentPath = null,
    string? BeforeSnapshotPath = null,
    string? AfterSnapshotPath = null,
    string? BeforeFormJson = null,
    string? AfterFormJson = null,
    string? BeforeSchedulesJson = null,
    string? AfterSchedulesJson = null,
    bool IsUndone = false,
    bool RedoInvalidated = false);

public sealed record TaskSchedule(
    string Id,
    string WorkspaceId,
    string Name,
    string Prompt,
    DateTimeOffset NextRunAt,
    RecurrenceKind Recurrence,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt = null,
    string? LastResult = null);

public static class CodexApprovalPolicies
{
    public const string Untrusted = "untrusted";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "untrusted" or "unlesstrusted" => Untrusted,
        "on-request" or "onrequest" => "on-request",
        "granular" => "granular",
        "never" => "never",
        _ => Untrusted
    };
}

public sealed record CodexTurnOptions(
    string? WorkingDirectory = null,
    bool WorkspaceWrite = false,
    bool NetworkAccess = true,
    string ApprovalPolicy = CodexApprovalPolicies.Untrusted);

public sealed record ConnectorStatus(
    string Id,
    string Name,
    string Kind,
    bool IsAvailable,
    bool IsAuthenticated,
    string Status);

public sealed record AuditEntry(
    string Id,
    DateTimeOffset Timestamp,
    string Category,
    string Summary,
    string Status,
    ActionRisk Risk,
    string? Detail = null);

public sealed record MemoryEntry(
    string Id,
    string Category,
    string Content,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsPinned = false);

public sealed record ConversationMessage(
    string Id,
    string ThreadId,
    string Role,
    string Content,
    DateTimeOffset Timestamp,
    string Status = "complete");

public sealed record SystemSnapshot(
    DateTimeOffset CapturedAt,
    double CpuPercent,
    double MemoryPercent,
    ulong MemoryUsedBytes,
    ulong MemoryTotalBytes,
    double DiskPercent,
    long DiskFreeBytes,
    string PowerStatus,
    int BatteryPercent,
    bool NetworkAvailable,
    int ActiveTasks,
    int PendingApprovals,
    bool CodexConnected);

public sealed record DailyBriefing(
    string Greeting,
    string Summary,
    IReadOnlyList<Reminder> UpcomingReminders,
    SystemSnapshot System,
    IReadOnlyList<string> Notices);

public sealed record CodexStreamEvent(
    string Method,
    string? ThreadId,
    string? TurnId,
    string? Text,
    string RawJson);

public sealed record ApprovalPrompt(
    long RequestId,
    string Method,
    string? ThreadId,
    string? TurnId,
    string Summary,
    string RawJson);

public sealed record CodexTurnResult(
    string ThreadId,
    string? TurnId,
    string FinalText,
    bool WasInterrupted,
    string? Error = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AssistantSettings))]
[JsonSerializable(typeof(Reminder))]
[JsonSerializable(typeof(TaskActivity))]
[JsonSerializable(typeof(Routine))]
[JsonSerializable(typeof(AuditEntry))]
internal partial class NexusJsonContext : JsonSerializerContext;
