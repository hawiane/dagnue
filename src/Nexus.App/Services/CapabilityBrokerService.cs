using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using MimeKit;
using Nexus.Core.Data;
using Nexus.Core.Models;

namespace Nexus.App.Services;

public sealed record CapabilityBridge(string Instructions, string ScriptPath);

public sealed class CapabilityBrokerService : IAsyncDisposable
{
    public const string PipeName = "DagnueCapabilityBrokerV1";
    private readonly AccessVaultService _vault;
    private readonly NexusRepository _repository;
    private readonly DesktopCaptureService _capture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, TaskSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private Task? _listener;
    private TcpListener? _tcpListener;
    private Task? _tcpLoop;
    private int _loopbackPort;

    public CapabilityBrokerService(AccessVaultService vault, NexusRepository repository,
        DesktopCaptureService capture)
    {
        _vault = vault;
        _repository = repository;
        _capture = capture;
    }

    public void Start()
    {
        _listener ??= ListenAsync(_lifetime.Token);
        if (_tcpListener is null)
        {
            _tcpListener = new TcpListener(IPAddress.Loopback, 0);
            _tcpListener.Start();
            _loopbackPort = ((IPEndPoint)_tcpListener.LocalEndpoint).Port;
            _tcpLoop = ListenTcpAsync(_lifetime.Token);
        }
    }

    public async Task EnsureDefaultProfilesAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _vault.GetProfilesAsync(cancellationToken: cancellationToken);
        if (existing.All(profile => profile.Id != "builtin-desktop-capture"))
        {
            var now = DateTimeOffset.UtcNow;
            await _vault.SaveAsync(new CapabilityProfile("builtin-desktop-capture", "Interactive desktop capture",
                CapabilityKind.DesktopCapture, true, true, CapabilityPermission.ApprovalRequired,
                null, null, null, null, "capture current virtual desktop", "DAGNUE/builtin-desktop-capture",
                now, now), null, cancellationToken);
        }
        if (existing.All(profile => profile.Id != "builtin-windows-admin"))
        {
            var now = DateTimeOffset.UtcNow;
            await _vault.SaveAsync(new CapabilityProfile("builtin-windows-admin", "Windows administrator actions",
                CapabilityKind.WindowsAdministration, false, false, CapabilityPermission.StrongApproval,
                null, null, null, null, "local approval required", "DAGNUE/builtin-windows-admin",
                now, now), null, cancellationToken);
        }
    }

    public async Task<CapabilityBridge> CreateTaskBridgeAsync(string taskId, string workspaceDirectory,
        string resultsDirectory, bool taskApproved, IReadOnlyList<string>? selectedTags = null,
        CancellationToken cancellationToken = default)
    {
        Start();
        var profiles = await _vault.GetProfilesAsync(enabledOnly: true, remoteOnly: true, cancellationToken);
        _sessions[taskId] = new TaskSession(taskId, resultsDirectory, taskApproved,
            profiles.ToDictionary(profile => profile.Id, StringComparer.OrdinalIgnoreCase),
            DateTimeOffset.UtcNow.AddHours(6));

        // Older builds put a bearer token inside the Codex workspace. Windows sandboxing can
        // deliberately deny reads of credential-like files, so the bridge now identifies its
        // already-approved, short-lived local session by task ID over the local broker channel.
        var legacyTokenPath = Path.Combine(workspaceDirectory, ".dagnue-capability-token");
        if (File.Exists(legacyTokenPath))
        {
            File.SetAttributes(legacyTokenPath, FileAttributes.Normal);
            File.Delete(legacyTokenPath);
        }
        var scriptPath = Path.Combine(workspaceDirectory, "dagnue-capability.ps1");
        await File.WriteAllTextAsync(scriptPath, BuildPowerShellBridge(_loopbackPort, taskId),
            new UTF8Encoding(false), cancellationToken);

        var tags = DagnueCapabilityDictionary.NormalizeTags(selectedTags);
        var orderedProfiles = profiles.OrderByDescending(profile =>
            DagnueCapabilityDictionary.GetProfileTags(profile).Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()).ToList();
        var available = orderedProfiles.Select(profile =>
            $"- {profile.Id}: {profile.Name} [{profile.Kind}; {profile.Permission}; tags={string.Join(',', DagnueCapabilityDictionary.GetProfileTags(profile))}] — {DagnueCapabilityDictionary.DescribeProfile(profile)}" ).ToList();
        var instructions = "DAGNUE MASTER CAPABILITY BROKER is available through ./dagnue-capability.ps1. " +
            "Use it when the task needs an authorized resource. The bridge uses an active local task session; no GPT or OpenAI key is stored in this workspace. " +
            "First run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./dagnue-capability.ps1 -Tool capabilities.list. " +
            "For a read-only database query run: powershell.exe -NoProfile -ExecutionPolicy Bypass -File " +
            "./dagnue-capability.ps1 -Tool database.query -ProfileId <id> -Query <SELECT query>. " +
            "For an approved capability whose scopes include write or crud, one guarded INSERT, UPDATE, DELETE, or MERGE " +
            "can run with -Tool database.execute. UPDATE and DELETE must include WHERE; schema/security SQL is always blocked. " +
            "For a requested desktop screenshot run the same command with -Tool desktop.capture " +
            "-ProfileId builtin-desktop-capture. Files produced by the broker are already placed under ./results. " +
            "For email search use -Tool email.search -ProfileId <id> -Query <terms>. " +
            "To create a reviewable email draft file use -Tool email.draft -ProfileId <id> -To <address> " +
            "-Subject <subject> -Body <text>; this never sends the message. " +
            "For an enabled Windows administrator profile, use -Tool windows.service.status or " +
            "windows.service.restart with -ServiceName <exact service name>. Restart always requires approved access. " +
            "For a registered document folder use -Tool folder.search -ProfileId <id> -Query <filename words>; " +
            "then use -Tool folder.copy -ProfileId <id> -Path <relative path> to place the selected file under ./results. " +
            "For the biometric service use -Tool api.get -ProfileId <id> -Path </api/path>. " +
            "Approved changes use api.execute with -Method POST|PUT|DELETE, -Path </api/path>, and optional -Body <json>. " +
            "Use SELECTED TASK TAGS as a routing contract: inspect matching profiles first and use other profiles only as fallback. " +
            "If the broker says approval is required, ask the user instead of bypassing it.\n" +
            $"SELECTED TASK TAGS: {string.Join(", ", tags)}\nAVAILABLE PROFILES (matching route first):\n" +
            (available.Count == 0 ? "- No remote capability profiles are enabled." : string.Join("\n", available));
        return new CapabilityBridge(instructions, scriptPath);
    }

    public async Task<bool> RequiresTaskApprovalAsync(string instructions,
        CancellationToken cancellationToken = default)
    {
        var profiles = await _vault.GetProfilesAsync(enabledOnly: true, remoteOnly: true, cancellationToken);
        foreach (var profile in profiles.Where(profile =>
                     profile.Permission is CapabilityPermission.ApprovalRequired or CapabilityPermission.StrongApproval))
        {
            var mentioned = profile.Kind switch
            {
                CapabilityKind.Database => ContainsAny(instructions, "database", "hris", "oracle", "sql", "employee records"),
                CapabilityKind.Email => ContainsAny(instructions, "email", "mailbox", "inbox", "outlook", "gmail"),
                CapabilityKind.DesktopCapture => ContainsAny(instructions, "screenshot", "screen capture", "capture desktop"),
                CapabilityKind.WindowsAdministration => ContainsAny(instructions, "administrator", "registry", "firewall",
                    "install", "uninstall", "service", "windows setting"),
                CapabilityKind.Folder => ContainsAny(instructions, "shared folder", "network folder", "saln", "document", "file"),
                CapabilityKind.WebApi => ContainsAny(instructions, "biometric", "attendance", "timelog", "bio"),
                _ => false
            };
            if (mentioned) return true;
        }
        return false;
    }

    public void RevokeTask(string taskId)
    {
        _sessions.TryRemove(taskId, out _);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                _ = HandleClientAsync(pipe, cancellationToken);
                pipe = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { pipe?.Dispose(); await Task.Delay(250, cancellationToken); }
        }
    }

    private async Task ListenTcpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _tcpListener!.AcceptTcpClientAsync(cancellationToken);
                _ = HandleTcpClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { await Task.Delay(250, cancellationToken); }
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var ownedClient = client;
        await HandleClientAsync(client.GetStream(), cancellationToken);
    }

    private async Task HandleClientAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var ownedStream = stream;
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            { AutoFlush = true };
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line)) return;
            var request = JsonSerializer.Deserialize<CapabilityToolRequest>(line,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var response = request is null
                ? new CapabilityToolResponse(false, "Invalid capability request.")
                : await ExecuteAsync(request, cancellationToken);
            await writer.WriteLineAsync(JsonSerializer.Serialize(response));
        }
        catch (Exception ex)
        {
            try
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
                { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(
                    new CapabilityToolResponse(false, ex.Message)));
            }
            catch { }
        }
    }

    private async Task<CapabilityToolResponse> ExecuteAsync(CapabilityToolRequest request,
        CancellationToken cancellationToken)
    {
        CleanupExpiredSessions();
        if (string.IsNullOrWhiteSpace(request.SessionId) ||
            !_sessions.TryGetValue(request.SessionId, out var session) ||
            session.ExpiresAt <= DateTimeOffset.UtcNow)
            return new CapabilityToolResponse(false, "Capability session is invalid or expired.");
        var arguments = request.Arguments ?? new Dictionary<string, string>();
        var profileId = arguments.GetValueOrDefault("profileId") ?? string.Empty;
        if (request.Tool.Equals("capabilities.list", StringComparison.OrdinalIgnoreCase))
        {
            var data = session.Profiles.Values.Select(profile => new
            {
                profile.Id, profile.Name, kind = profile.Kind.ToString(), permission = profile.Permission.ToString(),
                scopes = profile.AllowedScopes,
                tags = DagnueCapabilityDictionary.GetProfileTags(profile),
                purpose = DagnueCapabilityDictionary.DescribeProfile(profile)
            }).ToArray();
            return new CapabilityToolResponse(true, $"{data.Length} remote capabilities available.", data);
        }

        if (!session.Profiles.TryGetValue(profileId, out var profile))
            return new CapabilityToolResponse(false, "The requested capability profile is not enabled for remote tasks.");
        var permission = CheckPermission(profile, session.TaskApproved);
        if (permission is not null) return permission;

        try
        {
            CapabilityToolResponse response;
            if (request.Tool.Equals("desktop.capture", StringComparison.OrdinalIgnoreCase) &&
                profile.Kind == CapabilityKind.DesktopCapture)
            {
                var path = await _capture.CaptureAsync(session.ResultsDirectory, cancellationToken);
                response = new CapabilityToolResponse(true, "Interactive desktop captured.",
                    new { filename = Path.GetFileName(path) }, path);
            }
            else if (request.Tool.Equals("database.query", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.Database)
            {
                var query = arguments.GetValueOrDefault("query") ?? string.Empty;
                var data = await _vault.QueryReadOnlyAsync(profile, query, cancellationToken: cancellationToken);
                response = new CapabilityToolResponse(true,
                    $"Read-only query returned {data.Rows.Count} row(s)" + (data.Truncated ? " (truncated)." : "."), data);
            }
            else if (request.Tool.Equals("database.execute", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.Database)
            {
                if (profile.Permission != CapabilityPermission.ApprovalRequired || !session.TaskApproved)
                    return new CapabilityToolResponse(false,
                        "Database writes require an approved task and an ApprovalRequired database profile.",
                        RequiresApproval: true);
                var query = arguments.GetValueOrDefault("query") ?? string.Empty;
                var data = await _vault.ExecuteControlledWriteAsync(profile, query, cancellationToken);
                response = new CapabilityToolResponse(true,
                    $"Controlled database command committed successfully; {data.AffectedRows} row(s) affected.", data);
            }
            else if (request.Tool.Equals("email.search", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.Email)
            {
                var query = arguments.GetValueOrDefault("query") ?? string.Empty;
                var data = await _vault.SearchEmailAsync(profile, query, cancellationToken: cancellationToken);
                response = new CapabilityToolResponse(true, $"Email search returned {data.Count} message(s).", data);
            }
            else if (request.Tool.Equals("email.draft", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.Email)
            {
                var path = await CreateEmailDraftAsync(session.ResultsDirectory, arguments, cancellationToken);
                response = new CapabilityToolResponse(true, "Email draft created for review; it was not sent.",
                    new { filename = Path.GetFileName(path) }, path);
            }
            else if (request.Tool.Equals("folder.search", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.Folder)
            {
                var query = arguments.GetValueOrDefault("query") ?? string.Empty;
                var data = SearchFolder(profile, query);
                response = new CapabilityToolResponse(true, $"Folder search returned {data.Length} match(es).", data);
            }
            else if (request.Tool.Equals("folder.copy", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.Folder)
            {
                var relativePath = arguments.GetValueOrDefault("path") ?? string.Empty;
                var path = await CopyFolderFileAsync(profile, relativePath, session.ResultsDirectory, cancellationToken);
                response = new CapabilityToolResponse(true, "Authorized file copied to task results.",
                    new { filename = Path.GetFileName(path) }, path);
            }
            else if (request.Tool.Equals("api.get", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.WebApi)
            {
                var path = arguments.GetValueOrDefault("path") ?? string.Empty;
                var data = await _vault.RequestApiAsync(profile, HttpMethod.Get, path, cancellationToken: cancellationToken);
                response = new CapabilityToolResponse(true, "Biometric API query completed.", data);
            }
            else if (request.Tool.Equals("api.execute", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.WebApi)
            {
                if (profile.Permission != CapabilityPermission.ApprovalRequired || !session.TaskApproved)
                    return new CapabilityToolResponse(false, "Biometric API changes require an approved task and approved-change profile.", RequiresApproval: true);
                var methodName = arguments.GetValueOrDefault("method") ?? string.Empty;
                if (methodName is not ("POST" or "PUT" or "DELETE")) throw new InvalidOperationException("API changes allow only POST, PUT, or DELETE.");
                var path = arguments.GetValueOrDefault("path") ?? string.Empty;
                var body = arguments.GetValueOrDefault("body");
                var data = await _vault.RequestApiAsync(profile, new HttpMethod(methodName), path, body, cancellationToken: cancellationToken);
                response = new CapabilityToolResponse(true, "Approved biometric API change completed.", data);
            }
            else if (request.Tool.Equals("windows.service.status", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.WindowsAdministration)
            {
                var serviceName = RequireServiceName(arguments);
                using var service = new ServiceController(serviceName);
                response = new CapabilityToolResponse(true, $"{service.ServiceName} is {service.Status}.",
                    new { service = service.ServiceName, displayName = service.DisplayName, status = service.Status.ToString() });
            }
            else if (request.Tool.Equals("windows.service.restart", StringComparison.OrdinalIgnoreCase) &&
                     profile.Kind == CapabilityKind.WindowsAdministration)
            {
                if (profile.Permission != CapabilityPermission.ApprovalRequired || !session.TaskApproved)
                    return new CapabilityToolResponse(false,
                        "Restarting a Windows service requires an approved task and an ApprovalRequired administrator profile.",
                        RequiresApproval: true);
                var serviceName = RequireServiceName(arguments);
                using var service = new ServiceController(serviceName);
                if (service.Status != ServiceControllerStatus.Stopped && service.Status != ServiceControllerStatus.StopPending)
                {
                    service.Stop();
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)), cancellationToken);
                }
                service.Start();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)), cancellationToken);
                response = new CapabilityToolResponse(true, $"{service.ServiceName} restarted successfully.",
                    new { service = service.ServiceName, status = service.Status.ToString() });
            }
            else
            {
                response = new CapabilityToolResponse(false, "This tool is not available for the selected profile.");
            }
            await AuditAsync(session, profile, request.Tool, response.Success ? "completed" : "denied",
                response.Message, cancellationToken);
            return response;
        }
        catch (Exception ex)
        {
            var safeError = SanitizeError(ex.Message);
            await AuditAsync(session, profile, request.Tool, "failed", safeError, cancellationToken);
            return new CapabilityToolResponse(false, safeError);
        }
    }

    private static CapabilityToolResponse? CheckPermission(CapabilityProfile profile, bool taskApproved) =>
        profile.Permission switch
        {
            CapabilityPermission.LocalOnly => new(false, "This capability can only be used from the local desktop."),
            CapabilityPermission.StrongApproval => new(false,
                "This capability requires physical confirmation in DAGNUE and cannot run unattended.", RequiresApproval: true),
            CapabilityPermission.ApprovalRequired when !taskApproved => new(false,
                "This capability requires approval in the task conversation before it can run.", RequiresApproval: true),
            _ => null
        };

    private Task AuditAsync(TaskSession session, CapabilityProfile profile, string tool, string status,
        string detail, CancellationToken cancellationToken) =>
        _repository.AddCapabilityAuditAsync(new CapabilityAuditEntry(Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow, session.TaskId, profile.Id, tool, status,
            profile.Permission is CapabilityPermission.StrongApproval ? ActionRisk.Critical :
            profile.Permission is CapabilityPermission.ApprovalRequired ? ActionRisk.High : ActionRisk.ReadOnly,
            detail.Length > 500 ? detail[..500] : detail), cancellationToken);

    private void CleanupExpiredSessions()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _sessions.Where(pair => pair.Value.ExpiresAt <= now))
            _sessions.TryRemove(pair.Key, out _);
    }

    private static string BuildPowerShellBridge(int port, string taskId)
    {
        var encodedTaskId = Convert.ToBase64String(Encoding.UTF8.GetBytes(taskId));
        return """
        param(
          [Parameter(Mandatory=$true)][string]$Tool,
          [string]$ProfileId = "",
          [string]$Query = "",
          [string]$To = "",
          [string]$Subject = "",
          [string]$Body = "",
          [string]$ServiceName = "",
          [string]$Path = "",
          [string]$Method = ""
        )
        $ErrorActionPreference = 'Stop'
        $sessionId = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('__DAGNUE_SESSION__'))
        $arguments = @{}
        if ($ProfileId) { $arguments.profileId = $ProfileId }
        if ($Query) { $arguments.query = $Query }
        if ($To) { $arguments.to = $To }
        if ($Subject) { $arguments.subject = $Subject }
        if ($Body) { $arguments.body = $Body }
        if ($ServiceName) { $arguments.serviceName = $ServiceName }
        if ($Path) { $arguments.path = $Path }
        if ($Method) { $arguments.method = $Method.ToUpperInvariant() }
        $request = @{ sessionId = $sessionId; tool = $Tool; arguments = $arguments } | ConvertTo-Json -Compress
        $connection = $null
        try {
          try {
            $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'DagnueCapabilityBrokerV1', [IO.Pipes.PipeDirection]::InOut)
            $pipe.Connect(3000)
            $connection = $pipe
          } catch {
            if ($pipe) { $pipe.Dispose() }
            $tcp = [Net.Sockets.TcpClient]::new()
            $tcp.Connect('127.0.0.1', __DAGNUE_PORT__)
            $connection = $tcp.GetStream()
          }
          $writer = [IO.StreamWriter]::new($connection, [Text.UTF8Encoding]::new($false), 4096, $true)
          $writer.AutoFlush = $true
          $reader = [IO.StreamReader]::new($connection, [Text.UTF8Encoding]::new($false), $false, 4096, $true)
          $writer.WriteLine($request)
          $response = $reader.ReadLine()
          if (-not $response) { throw 'DAGNUE capability broker returned no response.' }
          $response
        } finally {
          if ($connection) { $connection.Dispose() }
          if ($tcp) { $tcp.Dispose() }
        }
        """
        .Replace("__DAGNUE_PORT__", port.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("__DAGNUE_SESSION__", encodedTaskId);
    }

    private static async Task<string> CreateEmailDraftAsync(string resultsDirectory,
        IReadOnlyDictionary<string, string> arguments, CancellationToken cancellationToken)
    {
        var to = arguments.GetValueOrDefault("to") ?? string.Empty;
        var subject = arguments.GetValueOrDefault("subject") ?? string.Empty;
        var body = arguments.GetValueOrDefault("body") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(to) || !MailboxAddress.TryParse(to, out var recipient))
            throw new InvalidOperationException("A valid recipient address is required for an email draft.");
        if (string.IsNullOrWhiteSpace(subject)) throw new InvalidOperationException("Email draft subject is required.");
        Directory.CreateDirectory(resultsDirectory);
        var message = new MimeMessage { Subject = subject.Trim(), Body = new TextPart("plain") { Text = body } };
        message.To.Add(recipient);
        message.Headers.Add("X-DAGNUE-Status", "Draft only - not sent");
        var safe = string.Concat(subject.Where(character => !Path.GetInvalidFileNameChars().Contains(character))).Trim();
        if (safe.Length == 0) safe = "email-draft";
        if (safe.Length > 80) safe = safe[..80];
        var path = Path.Combine(resultsDirectory, safe + ".eml");
        await message.WriteToAsync(path, cancellationToken);
        return path;
    }

    private static string RequireServiceName(IReadOnlyDictionary<string, string> arguments)
    {
        var value = arguments.GetValueOrDefault("serviceName")?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 256 || value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or '.')))
            throw new InvalidOperationException("An exact Windows service name is required.");
        return value;
    }

    private static object[] SearchFolder(CapabilityProfile profile, string query)
    {
        var root = GetFolderRoot(profile);
        var fragments = query.Split([' ', '\t', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fragments.Length == 0) throw new InvalidOperationException("Enter filename words to search for.");
        var results = new List<object>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (!fragments.All(fragment => relative.Contains(fragment, StringComparison.OrdinalIgnoreCase))) continue;
            var info = new FileInfo(file);
            results.Add(new { path = relative, name = info.Name, size = info.Length, modified = info.LastWriteTimeUtc });
            if (results.Count >= 100) break;
        }
        return results.ToArray();
    }

    private static async Task<string> CopyFolderFileAsync(CapabilityProfile profile, string relativePath,
        string resultsDirectory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw new InvalidOperationException("A relative file path is required.");
        var root = GetFolderRoot(profile);
        var source = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(source))
            throw new InvalidOperationException("The requested file is outside the authorized folder or does not exist.");
        Directory.CreateDirectory(resultsDirectory);
        var destination = Path.Combine(resultsDirectory, Path.GetFileName(source));
        if (File.Exists(destination))
            destination = Path.Combine(resultsDirectory, $"{Path.GetFileNameWithoutExtension(source)}-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(source)}");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, true);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await input.CopyToAsync(output, cancellationToken);
        return destination;
    }

    private static string GetFolderRoot(CapabilityProfile profile)
    {
        var value = profile.Resource ?? profile.Endpoint;
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("The folder capability has no root path.");
        var root = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("The authorized folder is unavailable.");
        return root;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string SanitizeError(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value,
            @"(?i)(password|pwd|access[_ -]?token|refresh[_ -]?token)\s*=\s*[^;\s]+", "$1=[REDACTED]");

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _tcpListener?.Stop();
        if (_listener is not null)
        {
            try { await _listener; } catch (OperationCanceledException) { }
        }
        if (_tcpLoop is not null)
        {
            try { await _tcpLoop; } catch (OperationCanceledException) { }
        }
        _sessions.Clear();
        _lifetime.Dispose();
    }

    private sealed record TaskSession(string TaskId, string ResultsDirectory, bool TaskApproved,
        IReadOnlyDictionary<string, CapabilityProfile> Profiles, DateTimeOffset ExpiresAt);
}
