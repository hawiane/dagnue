using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Nexus.Core.Codex;
using Nexus.Core.Models;
using Nexus.Core.SystemServices;

namespace Nexus.App.Services;

public sealed class RemoteTaskAgentService : IAsyncDisposable
{
    private readonly string _dataDirectory;
    private readonly CodexAppServerClient _codex;
    private readonly NotificationService _notifications;
    private readonly IndexedFileSearchService _fileSearch;
    private readonly CapabilityBrokerService _capabilityBroker;
    private readonly Func<AssistantSettings> _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private HttpClient? _http;
    private Task? _loop;
    private RemoteRunnerConfig? _config;

    public RemoteTaskAgentService(string dataDirectory, CodexAppServerClient codex,
        NotificationService notifications, IndexedFileSearchService fileSearch,
        CapabilityBrokerService capabilityBroker, Func<AssistantSettings> settings)
    {
        _dataDirectory = dataDirectory;
        _codex = codex;
        _notifications = notifications;
        _fileSearch = fileSearch;
        _capabilityBroker = capabilityBroker;
        _settings = settings;
    }

    public bool IsConfigured => _config is { Enabled: true };

    public async Task<IReadOnlyList<RemoteTaskDashboardItem>> GetDashboardTasksAsync(
        CancellationToken cancellationToken = default)
    {
        if (_http is null || _config is not { Enabled: true }) return [];
        var path = $"api/runner/tasks?deviceId={Uri.EscapeDataString(_config.DeviceId)}";
        var response = await _http.GetFromJsonAsync<RemoteTaskDashboardResponse>(path, cancellationToken);
        return response?.Tasks ?? [];
    }

    public async Task SendDashboardActionAsync(string taskId, string action, string? message = null,
        CancellationToken cancellationToken = default)
    {
        if (_http is null || _config is not { Enabled: true })
            throw new InvalidOperationException("The remote task runner is not configured.");
        await SendPatchAsync($"api/runner/tasks/{Uri.EscapeDataString(taskId)}",
            new { action, message, deviceId = _config.DeviceId }, cancellationToken);
    }

    public void Start()
    {
        _config = LoadConfig();
        if (_config is not { Enabled: true } || string.IsNullOrWhiteSpace(_config.BaseUrl) ||
            string.IsNullOrWhiteSpace(_config.Token)) return;
        _http = new HttpClient { BaseAddress = new Uri(_config.BaseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        if (!string.IsNullOrWhiteSpace(_config.SitesBypassToken))
            _http.DefaultRequestHeaders.TryAddWithoutValidation(
                "OAI-Sites-Authorization", $"Bearer {_config.SitesBypassToken}");
        _loop = RunAsync(_lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(25))
                {
                    await PostJsonAsync("api/runner/heartbeat", new
                    {
                        deviceId = _config!.DeviceId, name = _config.DeviceName,
                        platform = $"Windows {Environment.OSVersion.Version}", version = "1.0.0",
                        currentTaskId = (string?)null
                    }, cancellationToken);
                    lastHeartbeat = DateTimeOffset.UtcNow;
                }

                var response = await PostJsonAsync("api/runner/tasks/claim", new { deviceId = _config!.DeviceId }, cancellationToken);
                var claim = await response.Content.ReadFromJsonAsync<ClaimResponse>(cancellationToken: cancellationToken);
                if (claim?.Task is not null) await ExecuteAsync(claim.Task, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ExecuteAsync(RemoteTask task, CancellationToken cancellationToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var control = new RemoteExecutionControl();
        var monitor = MonitorTaskControlAsync(task.Id, control, executionCancellation, monitorCancellation.Token);
        try
        {
            await ExecuteCoreAsync(task, executionCancellation.Token);
        }
        catch (OperationCanceledException) when (control.WasRequested)
        {
            // The web desk already owns and displays the requested paused/stopped/ended state.
        }
        finally
        {
            monitorCancellation.Cancel();
            try { await monitor; } catch (OperationCanceledException) { }
        }
    }

    private async Task ExecuteCoreAsync(RemoteTask task, CancellationToken cancellationToken)
    {
        var estimatedSeconds = EstimateDurationSeconds(task);
        await UpdateProgressAsync(task.Id, "Preparing task",
            "Creating a safe workspace and reading your instructions.", 8, estimatedSeconds, cancellationToken);
        if (task.ApprovedAt is null && (RequiresApproval(task.Instructions) ||
            await _capabilityBroker.RequiresTaskApprovalAsync(task.Instructions, cancellationToken)))
        {
            await UpdateAsync(task.Id, "approval_required",
                "This request can affect files, software, credentials, security, or external systems. Approve it in the web task desk to continue.", null, null, cancellationToken);
            _notifications.Show("Remote task needs approval", task.Title);
            return;
        }

        var directory = Path.Combine(_dataDirectory, "remote-tasks", task.Id);
        Directory.CreateDirectory(directory);
        var inputs = Path.Combine(directory, "inputs");
        var results = Path.Combine(directory, "results");
        var indexedCandidates = Path.Combine(directory, "indexed-candidates");
        Directory.CreateDirectory(inputs);
        Directory.CreateDirectory(results);
        Directory.CreateDirectory(indexedCandidates);
        ClearPreviousResults(results);
        ClearPreviousResults(indexedCandidates);
        if ((task.Files?.Count ?? 0) > 0)
            await UpdateProgressAsync(task.Id, "Downloading attachments",
                "Copying your attached files into the task workspace.", 16, estimatedSeconds, cancellationToken);
        foreach (var file in task.Files ?? []) await DownloadAsync(task.Id, file, inputs, cancellationToken);

        try
        {
            var capabilityBridge = await _capabilityBroker.CreateTaskBridgeAsync(task.Id, directory, results,
                task.ApprovedAt is not null, task.Tags, cancellationToken);
            await UpdateProgressAsync(task.Id, "Starting DAGNUE AI",
                "Opening the task conversation and preparing the execution tools.", 25, estimatedSeconds, cancellationToken);
            if (!_codex.IsConnected) await _codex.StartAsync(cancellationToken: cancellationToken);
            var threadId = string.IsNullOrWhiteSpace(task.ThreadId)
                ? await _codex.StartThreadAsync(NullIfWhiteSpace(_settings().CodexModel), directory,
                    workspaceWrite: true, cancellationToken)
                : await _codex.ResumeThreadAsync(task.ThreadId, directory, workspaceWrite: true, cancellationToken);
            await SendPatchAsync($"api/runner/tasks/{task.Id}", new
            {
                action = "checkpoint", deviceId = _config!.DeviceId, threadId
            }, cancellationToken);
            var conversation = string.Join("\n", (task.Messages ?? []).Select(message =>
                $"{message.Role.ToUpperInvariant()} ({message.Kind}): {message.Content}"));
            await UpdateProgressAsync(task.Id, "Searching task context",
                "Checking attachments and the DAGNUE file index for relevant local files.", 32, estimatedSeconds, cancellationToken);
            var indexedFileContext = await BuildIndexedFileContextAsync(task, indexedCandidates, cancellationToken);
            var prompt = "Execute this remote task using the tools available on this computer. Input files are under ./inputs. " +
                "Put every file the user should receive under ./results and never write anywhere else. Read-only inspection outside this task workspace " +
                "is allowed only when needed to locate a user-requested file. Never inspect credentials, browser profiles, payments, private keys, " +
                "security settings, or unrelated personal content. Never substitute an internet result when the user asked for a file on this computer. " +
                "Treat the selected capability tags as the primary search route. Use their registered vault dictionaries and authorized tools first; " +
                "only widen to general computer search when the tagged route cannot answer the request. " +
                "For file-related requests, use the DAGNUE FIND index matches below before probing drives or guessing a location. Verified file candidates are " +
                "copied under ./indexed-candidates so they remain readable inside this task. Verify a candidate matches the request before using it. " +
                "Prefer an exact filename and a path whose directory context matches the request. If several " +
                "plausible indexed matches remain, show their filenames and locations and ask the user to choose. " +
                "If a drive is locked, a path is unavailable, the request is ambiguous, there are multiple plausible choices, or essential information is missing, " +
                "do not guess. Ask one concise question by returning exactly DAGNUE_NEEDS_INPUT on the first line and the question plus useful choices below it. " +
                "To offer an optional improvement before continuing, return DAGNUE_SUGGESTION on the first line and the suggestion below it. " +
                "Otherwise complete the task and return a concise, intelligent result. RESPONSE STYLE: lead with the direct answer or total in the first sentence. " +
                "Do not narrate setup, retries, fallbacks, runtimes, connection checks, SQL, tool calls, or steps unless the user asked. " +
                "FINAL RESPONSE CONTRACT: use no more than two short sentences and 45 words. State only the completed outcome and at most one useful observation. " +
                "Never mention local paths, the workspace, the results directory, uploads, or internal file-citation markup; result files are displayed automatically. " +
                "When it is safe and inexpensive, check one or two closely related facts that help interpret or validate the answer, state one useful observation, " +
                "and offer one specific next action. For workforce counts, consider adjacent appointment groups such as casual, job order, coterminous, temporary, " +
                "or city officials when those categories exist. Never invent a comparison; query it or omit it.\n\n" +
                capabilityBridge.Instructions + "\n\n" +
                $"TASK: {task.Title}\nPRIORITY: {task.Priority}\nTAGS: {string.Join(", ", task.Tags ?? [])}\nORIGINAL INSTRUCTIONS:\n{task.Instructions}\n\nCONVERSATION:\n{conversation}";
            if (!string.IsNullOrWhiteSpace(indexedFileContext))
                prompt += $"\n\nDAGNUE FIND INDEX MATCHES (verified local paths):\n{indexedFileContext}";
            var progressState = new ExecutionProgressState("Working on the task",
                "DAGNUE AI is analyzing the request and choosing the next action.", 38);
            void OnStreamEvent(object? _, CodexStreamEvent streamEvent) =>
                UpdateExecutionProgress(progressState, streamEvent.Method);
            _codex.StreamEvent += OnStreamEvent;
            using var progressCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var progressLoop = RunExecutionProgressAsync(task.Id, estimatedSeconds, progressState,
                progressCancellation.Token);
            CodexTurnResult run;
            try
            {
                run = await _codex.RunTurnAsync(prompt, threadId, null,
                    new CodexTurnOptions(directory, WorkspaceWrite: true, NetworkAccess: true,
                        ApprovalPolicy: "never"), cancellationToken);
            }
            finally
            {
                _codex.StreamEvent -= OnStreamEvent;
                progressCancellation.Cancel();
                try { await progressLoop; } catch (OperationCanceledException) { }
            }
            if (!string.IsNullOrWhiteSpace(run.Error) || run.WasInterrupted)
                throw new InvalidOperationException(run.Error ?? "The Codex task was interrupted.");
            await UpdateProgressAsync(task.Id, "Checking the result",
                "Reviewing what was done and checking whether DAGNUE needs your input.", 88, 45, cancellationToken);
            if (TryReadInteractiveReply(run.FinalText, out var message, out var messageKind))
            {
                await UpdateAsync(task.Id, "awaiting_input", null, null, run.ThreadId, cancellationToken,
                    message, messageKind);
                _notifications.Show("Remote task needs your reply", task.Title);
                return;
            }
            await UpdateProgressAsync(task.Id, "Uploading result files",
                "Sending the completed files to your secure task conversation.", 95, 20, cancellationToken);
            var uploadedFileCount = await UploadResultsAsync(task.Id, results, cancellationToken);
            var summary = RemoteTaskResultFormatter.Format(run.FinalText, uploadedFileCount > 0);
            await UpdateAsync(task.Id, "awaiting_review", summary, null, run.ThreadId, cancellationToken,
                summary, "message");
            _notifications.Show("Remote task ready for review", task.Title);
        }
        catch (Exception ex)
        {
            await UpdateAsync(task.Id, "failed", null, RemoteTaskResultFormatter.FormatError(ex.Message),
                null, cancellationToken);
            _notifications.Show("Remote task needs attention", task.Title);
        }
        finally
        {
            _capabilityBroker.RevokeTask(task.Id);
        }
    }

    private async Task DownloadAsync(string taskId, RemoteFile file, string directory, CancellationToken cancellationToken)
    {
        using var response = await _http!.GetAsync($"api/runner/tasks/{taskId}/files/{file.Id}", cancellationToken);
        response.EnsureSuccessStatusCode();
        var safeName = Path.GetFileName(file.Filename);
        await using var output = File.Create(Path.Combine(directory, safeName));
        await response.Content.CopyToAsync(output, cancellationToken);
    }

    private async Task<int> UploadResultsAsync(string taskId, string resultsDirectory, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(resultsDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path)).Where(file => file.Length <= 50L * 1024 * 1024).Take(10).ToArray();
        var uploaded = 0;
        foreach (var file in files)
        {
            await using var stream = File.OpenRead(file.FullName);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = file.Length;
            using var request = new HttpRequestMessage(HttpMethod.Post, $"api/runner/tasks/{taskId}/results")
            {
                Content = content
            };
            request.Headers.TryAddWithoutValidation("X-Dagnue-Filename", Uri.EscapeDataString(file.Name));
            using var response = await _http!.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            uploaded++;
        }
        return uploaded;
    }

    private Task UpdateAsync(string taskId, string status, string? summary, string? error, string? threadId,
        CancellationToken cancellationToken, string? message = null, string? messageKind = null) =>
        SendPatchAsync($"api/runner/tasks/{taskId}", new
        { status, deviceId = _config!.DeviceId, summary, error, threadId, message, messageKind }, cancellationToken);

    private async Task<HttpResponseMessage> PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        var response = await _http!.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task SendPatchAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, path) { Content = JsonContent.Create(body) };
        using var response = await _http!.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task UpdateProgressAsync(string taskId, string phase, string detail, int progressPercent,
        int? etaSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http!.PutAsJsonAsync($"api/runner/tasks/{taskId}/progress",
                new { phase, detail, progressPercent, etaSeconds }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { }
    }

    private async Task RunExecutionProgressAsync(string taskId, int estimatedSeconds,
        ExecutionProgressState state, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        do
        {
            var elapsed = Math.Max(0, (int)(DateTimeOffset.UtcNow - started).TotalSeconds);
            var snapshot = state.Snapshot();
            var timeProgress = 36 + (int)Math.Round(Math.Min(1, elapsed / (double)estimatedSeconds) * 50);
            var percent = Math.Min(88, Math.Max(snapshot.Percent, timeProgress));
            var remaining = Math.Max(0, estimatedSeconds - elapsed);
            await UpdateProgressAsync(taskId, snapshot.Phase, snapshot.Detail, percent, remaining,
                cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task MonitorTaskControlAsync(string taskId, RemoteExecutionControl control,
        CancellationTokenSource executionCancellation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                var response = await _http!.GetFromJsonAsync<RunnerTaskControlResponse>(
                    $"api/runner/tasks/{taskId}", cancellationToken);
                var status = response?.Task?.Status;
                if (status is "paused" or "cancelled" or "completed")
                {
                    control.Request(status);
                    executionCancellation.Cancel();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
        }
    }

    private static void UpdateExecutionProgress(ExecutionProgressState state, string method)
    {
        if (method.Contains("command", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("tool", StringComparison.OrdinalIgnoreCase))
            state.Update("Using computer tools", "DAGNUE is performing a task step on this computer.", 58);
        else if (method.Contains("item/started", StringComparison.OrdinalIgnoreCase) ||
                 method.Contains("item/completed", StringComparison.OrdinalIgnoreCase))
            state.Update("Working through task steps", "DAGNUE is completing the planned actions.", 52);
        else if (method.Contains("delta", StringComparison.OrdinalIgnoreCase))
            state.Update("Preparing the response", "DAGNUE is organizing the result and any files for you.", 76);
    }

    private static int EstimateDurationSeconds(RemoteTask task)
    {
        var request = $"{task.Title} {task.Instructions} " + string.Join(' ',
            (task.Messages ?? []).Where(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                .Select(message => message.Content));
        var seconds = 360;
        if (LooksLikeFileRequest(request)) seconds = 180;
        if (Regex.IsMatch(request, @"(?i)\b(build|create|generate|analy[sz]e|report|spreadsheet|document|presentation|code|website)\b"))
            seconds = Math.Max(seconds, 600);
        if (Regex.IsMatch(request, @"(?i)\b(install|uninstall|migrate|convert|research|scan all|entire drive)\b"))
            seconds = Math.Max(seconds, 900);
        seconds += Math.Min(300, (task.Files?.Count ?? 0) * 60);
        return Math.Clamp(seconds, 120, 1_200);
    }

    private RemoteRunnerConfig? LoadConfig()
    {
        var baseUrl = Environment.GetEnvironmentVariable("DAGNUE_REMOTE_BASE_URL");
        var token = Environment.GetEnvironmentVariable("DAGNUE_REMOTE_DEVICE_TOKEN");
        var sitesBypassToken = Environment.GetEnvironmentVariable("DAGNUE_SITES_BYPASS_TOKEN");
        if (!string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(token))
            return new RemoteRunnerConfig(baseUrl, token, true, "office-computer", Environment.MachineName,
                sitesBypassToken);
        var path = Path.Combine(_dataDirectory, "remote-runner.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<RemoteRunnerConfig>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static bool RequiresApproval(string text)
    {
        string[] terms = ["delete", "remove", "overwrite", "password", "credential", "payment", "purchase",
            "administrator", "registry", "firewall", "security setting", "install", "uninstall", "shutdown",
            "restart", "format", "send email", "publish", "upload publicly", "screenshot", "screen capture"];
        return terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<string?> BuildIndexedFileContextAsync(RemoteTask task, string candidateDirectory,
        CancellationToken cancellationToken)
    {
        var request = task.Messages?.Reverse().FirstOrDefault(message =>
            message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
            LooksLikeFileRequest(message.Content))?.Content;
        if (string.IsNullOrWhiteSpace(request)) request = $"{task.Title} {task.Instructions}";
        if (!LooksLikeFileRequest(request)) return null;

        var queryTerms = ExtractFileQueryTerms(request);
        if (queryTerms.Count == 0) return null;

        var candidateMap = new Dictionary<string, FileGraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in queryTerms.Take(8))
        {
            var termResult = await _fileSearch.SearchAsync(term, maximumResults: 80,
                cancellationToken: cancellationToken);
            foreach (var item in termResult.Items) candidateMap.TryAdd(item.Path, item);
        }
        var presentationRequest = Regex.IsMatch(request, @"(?i)\b(powerpoint|presentation|pptx?|slides?)\b");
        var verified = candidateMap.Values
            .Where(item => item.IsDirectory ? Directory.Exists(item.Path) : File.Exists(item.Path))
            .Select(item => new
            {
                Item = item,
                Score = queryTerms.Count(term =>
                    item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    item.Path.Contains(term, StringComparison.OrdinalIgnoreCase)) * 20
                    + (presentationRequest && !item.IsDirectory &&
                       (Path.GetExtension(item.Name).Equals(".pptx", StringComparison.OrdinalIgnoreCase) ||
                        Path.GetExtension(item.Name).Equals(".ppt", StringComparison.OrdinalIgnoreCase)) ? 35 : 0)
                    + (item.IsDirectory ? 0 : 5)
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Item.Name.Length)
            .Select(candidate => candidate.Item)
            .Take(12)
            .ToArray();
        if (verified.Length == 0) return null;

        var context = new StringBuilder();
        context.AppendLine($"AI-assisted search terms: {string.Join(", ", queryTerms.Take(8))}");
        for (var index = 0; index < verified.Length; index++)
        {
            var item = verified[index];
            var type = item.IsDirectory ? "folder" : "file";
            context.Append("- ").Append(type).Append(": ").Append(item.Path);
            if (!item.IsDirectory && item.SizeBytes <= 50L * 1024 * 1024)
            {
                try
                {
                    var stagedName = $"{index + 1:00} - {Path.GetFileName(item.Path)}";
                    var stagedPath = Path.Combine(candidateDirectory, stagedName);
                    File.Copy(item.Path, stagedPath, overwrite: true);
                    context.Append(" | staged: ./indexed-candidates/").Append(stagedName);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            context.AppendLine();
        }
        if (candidateMap.Count > verified.Length)
            context.AppendLine($"- More indexed candidates exist ({candidateMap.Count:N0} sampled); ask the user only when the best remaining files are genuinely ambiguous.");
        return context.ToString().TrimEnd();
    }

    private static bool LooksLikeFileRequest(string request)
    {
        string[] signals = ["file", "folder", "directory", "document", "image", "photo", "logo", "pdf",
            "png", "jpg", "jpeg", "svg", "docx", "xlsx", "spreadsheet", "csv", "attach", "upload",
            "download", "locate", "find", "copy"];
        return signals.Any(signal => Regex.IsMatch(request, $@"(?i)(?<![a-z0-9]){Regex.Escape(signal)}(?![a-z0-9])"));
    }

    private static IReadOnlyList<string> ExtractFileQueryTerms(string request)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "at", "be", "can", "copy", "could", "directory", "do",
            "document", "download", "file", "files", "find", "folder", "folders", "for", "from", "get",
            "give", "here", "i", "image", "in", "it", "locate", "me", "my", "of", "on", "please",
            "send", "that", "the", "this", "to", "upload", "want", "we", "with", "you",
            "powerpoint", "presentation", "pwesentation", "presentasion", "held", "last", "think"
        };
        return Regex.Matches(request, @"[A-Za-z0-9][A-Za-z0-9._-]*")
            .Select(match => match.Value.Trim('.', '-', '_'))
            .Where(value => value.Length >= 2 && !stopWords.Contains(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();
    }

    private static bool TryReadInteractiveReply(string? text, out string message, out string kind)
    {
        var value = text?.Trim() ?? string.Empty;
        const string questionMarker = "DAGNUE_NEEDS_INPUT";
        const string suggestionMarker = "DAGNUE_SUGGESTION";
        var questionIndex = value.IndexOf(questionMarker, StringComparison.OrdinalIgnoreCase);
        if (questionIndex >= 0)
        {
            message = value[(questionIndex + questionMarker.Length)..].TrimStart('`', '\r', '\n', ' ', ':'); kind = "question";
            if (string.IsNullOrWhiteSpace(message)) message = "I need more information before I can continue. What should I use?";
            return true;
        }
        var suggestionIndex = value.IndexOf(suggestionMarker, StringComparison.OrdinalIgnoreCase);
        if (suggestionIndex >= 0)
        {
            message = value[(suggestionIndex + suggestionMarker.Length)..].TrimStart('`', '\r', '\n', ' ', ':'); kind = "suggestion";
            if (string.IsNullOrWhiteSpace(message)) message = "I have a suggestion before I continue. Would you like me to proceed?";
            return true;
        }
        message = string.Empty; kind = "question"; return false;
    }

    private static void ClearPreviousResults(string resultsDirectory)
    {
        var resolved = Path.GetFullPath(resultsDirectory);
        if (!Directory.Exists(resolved) ||
            !(Path.GetFileName(resolved).Equals("results", StringComparison.OrdinalIgnoreCase) ||
              Path.GetFileName(resolved).Equals("indexed-candidates", StringComparison.OrdinalIgnoreCase))) return;
        foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
            File.Delete(file);
        foreach (var child in Directory.EnumerateDirectories(resolved)
                     .OrderByDescending(path => path.Length))
            Directory.Delete(child, recursive: true);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loop is not null) try { await _loop; } catch (OperationCanceledException) { }
        _http?.Dispose(); _lifetime.Dispose();
    }

    private sealed record RemoteRunnerConfig(string BaseUrl, string Token, bool Enabled = true,
        string DeviceId = "office-computer", string DeviceName = "Office computer",
        string? SitesBypassToken = null);
    private sealed record ClaimResponse([property:JsonPropertyName("task")] RemoteTask? Task);
    private sealed record RunnerTaskControlResponse([property:JsonPropertyName("task")] RunnerTaskControl? Task);
    private sealed record RunnerTaskControl([property:JsonPropertyName("status")] string Status);
    private sealed record RemoteTaskDashboardResponse(
        [property:JsonPropertyName("tasks")] IReadOnlyList<RemoteTaskDashboardItem>? Tasks);
    private sealed record RemoteTask(
        [property:JsonPropertyName("id")] string Id,
        [property:JsonPropertyName("title")] string Title,
        [property:JsonPropertyName("instructions")] string Instructions,
        [property:JsonPropertyName("priority")] string Priority,
        [property:JsonPropertyName("approved_at")] string? ApprovedAt,
        [property:JsonPropertyName("thread_id")] string? ThreadId,
        [property:JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
        [property:JsonPropertyName("files")] IReadOnlyList<RemoteFile>? Files,
        [property:JsonPropertyName("messages")] IReadOnlyList<RemoteMessage>? Messages);
    private sealed record RemoteFile(
        [property:JsonPropertyName("id")] string Id,
        [property:JsonPropertyName("filename")] string Filename);
    private sealed record RemoteMessage(
        [property:JsonPropertyName("role")] string Role,
        [property:JsonPropertyName("kind")] string Kind,
        [property:JsonPropertyName("content")] string Content);

    private sealed class RemoteExecutionControl
    {
        private string? _status;
        public bool WasRequested => Volatile.Read(ref _status) is not null;
        public void Request(string status) => Interlocked.CompareExchange(ref _status, status, null);
    }

    private sealed class ExecutionProgressState(string phase, string detail, int percent)
    {
        private readonly object _gate = new();
        private string _phase = phase;
        private string _detail = detail;
        private int _percent = percent;

        public void Update(string nextPhase, string nextDetail, int nextPercent)
        {
            lock (_gate)
            {
                _phase = nextPhase;
                _detail = nextDetail;
                _percent = Math.Max(_percent, nextPercent);
            }
        }

        public (string Phase, string Detail, int Percent) Snapshot()
        {
            lock (_gate) return (_phase, _detail, _percent);
        }
    }
}

public sealed record RemoteTaskDashboardItem(
    [property:JsonPropertyName("id")] string Id,
    [property:JsonPropertyName("title")] string Title,
    [property:JsonPropertyName("instructions")] string Instructions,
    [property:JsonPropertyName("priority")] string Priority,
    [property:JsonPropertyName("status")] string Status,
    [property:JsonPropertyName("result_summary")] string? ResultSummary,
    [property:JsonPropertyName("error_message")] string? ErrorMessage,
    [property:JsonPropertyName("created_at")] string CreatedAt,
    [property:JsonPropertyName("updated_at")] string UpdatedAt,
    [property:JsonPropertyName("tags")] IReadOnlyList<string>? Tags,
    [property:JsonPropertyName("messages")] IReadOnlyList<RemoteTaskDashboardMessage>? Messages,
    [property:JsonPropertyName("files")] IReadOnlyList<RemoteTaskDashboardFile>? Files,
    [property:JsonPropertyName("progress")] RemoteTaskDashboardProgress? Progress);

public sealed record RemoteTaskDashboardMessage(
    [property:JsonPropertyName("role")] string Role,
    [property:JsonPropertyName("kind")] string Kind,
    [property:JsonPropertyName("content")] string Content,
    [property:JsonPropertyName("created_at")] string CreatedAt);

public sealed record RemoteTaskDashboardFile(
    [property:JsonPropertyName("id")] string Id,
    [property:JsonPropertyName("kind")] string Kind,
    [property:JsonPropertyName("filename")] string Filename,
    [property:JsonPropertyName("size")] long Size);

public sealed record RemoteTaskDashboardProgress(
    [property:JsonPropertyName("phase")] string Phase,
    [property:JsonPropertyName("detail")] string Detail,
    [property:JsonPropertyName("progress_percent")] int ProgressPercent,
    [property:JsonPropertyName("eta_seconds")] int? EtaSeconds,
    [property:JsonPropertyName("updated_at")] string UpdatedAt);
