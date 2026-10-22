using System.Text.Json.Serialization;

namespace Nexus.Core.Models;

public enum CapabilityKind
{
    Database,
    Email,
    DesktopCapture,
    Folder,
    WindowsAdministration,
    WebApi
}

public enum CapabilityPermission
{
    AutomaticRead,
    AutomaticWorkspaceWrite,
    ApprovalRequired,
    StrongApproval,
    LocalOnly
}

public enum DatabaseProvider
{
    Oracle,
    Odbc,
    Sqlite,
    SqlServer,
    MySql
}

public sealed record CapabilityProfile(
    string Id,
    string Name,
    CapabilityKind Kind,
    bool IsEnabled,
    bool AllowRemoteTasks,
    CapabilityPermission Permission,
    string? Endpoint,
    string? Resource,
    string? Username,
    DatabaseProvider? DatabaseProvider,
    string? AllowedScopes,
    string SecretTarget,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastTestedAt = null,
    string? LastTestStatus = null,
    IReadOnlyList<string>? Tags = null,
    string? DictionaryDescription = null,
    IReadOnlyList<string>? Aliases = null);

public sealed record CapabilityAuditEntry(
    string Id,
    DateTimeOffset Timestamp,
    string TaskId,
    string CapabilityId,
    string Tool,
    string Status,
    ActionRisk Risk,
    string? Detail = null);

public sealed record CapabilityToolRequest(
    string SessionId,
    string Tool,
    Dictionary<string, string>? Arguments = null);

public sealed record CapabilityToolResponse(
    bool Success,
    string Message,
    object? Data = null,
    string? ResultPath = null,
    bool RequiresApproval = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityToolRequest))]
[JsonSerializable(typeof(CapabilityToolResponse))]
internal partial class CapabilityJsonContext : JsonSerializerContext;
