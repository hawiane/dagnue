using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Nexus.Core.Models;

namespace Nexus.Core.Codex;

public sealed class CodexAppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly StringBuilder _turnText = new();
    private Process? _process;
    private CancellationTokenSource? _lifetime;
    private Task? _readerTask;
    private long _requestId;
    private TaskCompletionSource<CodexTurnResult>? _activeTurn;
    private string? _activeThreadId;
    private string? _activeTurnId;

    public bool IsConnected => _process is { HasExited: false };
    public string? ActiveThreadId => _activeThreadId;
    public event EventHandler<CodexStreamEvent>? StreamEvent;
    public event EventHandler<ApprovalPrompt>? ApprovalRequested;
    public event EventHandler<string>? ConnectionError;
    public event EventHandler<string>? Diagnostic;

    public async Task StartAsync(string executable = "codex", CancellationToken cancellationToken = default)
    {
        if (IsConnected) return;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var launch = ResolveLaunch(executable);
        var startInfo = new ProcessStartInfo(launch.FileName, launch.Arguments)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Codex app-server.");
        Diagnostic?.Invoke(this, $"Started Codex process {_process.Id}.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => ConnectionError?.Invoke(this, "Codex app-server stopped.");
        _readerTask = ReadLoopAsync(_lifetime.Token);
        _ = ReadErrorsAsync(_lifetime.Token);

        await RequestAsync("initialize", new
        {
            clientInfo = new { name = "dagnue_desktop", title = "DAGNUE Desktop Assistant", version = "0.1.0" },
            capabilities = new { }
        }, cancellationToken);
        await SendNotificationAsync("initialized", new { }, cancellationToken);
    }

    public async Task<string> StartThreadAsync(string? model = null, string? cwd = null,
        bool workspaceWrite = false, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var parameters = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(model)) parameters["model"] = model;
        if (!string.IsNullOrWhiteSpace(cwd)) parameters["cwd"] = cwd;
        if (workspaceWrite)
        {
            parameters["approvalPolicy"] = CodexApprovalPolicies.Untrusted;
            parameters["sandbox"] = "workspace-write";
            parameters["serviceName"] = "dagnue_mini_system";
        }
        var result = await RequestAsync("thread/start", parameters, cancellationToken);
        _activeThreadId = result.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Codex did not return a thread id.");
        return _activeThreadId;
    }

    public async Task<CodexTurnResult> RunTurnAsync(string prompt, string? threadId = null, CancellationToken cancellationToken = default)
        => await RunTurnAsync(prompt, threadId, null, cancellationToken);

    public async Task<CodexTurnResult> RunTurnAsync(
        string prompt,
        string? threadId,
        IReadOnlyList<string>? localImagePaths,
        CancellationToken cancellationToken = default)
        => await RunTurnAsync(prompt, threadId, localImagePaths, null, cancellationToken);

    public async Task<CodexTurnResult> RunTurnAsync(
        string prompt,
        string? threadId,
        IReadOnlyList<string>? localImagePaths,
        CodexTurnOptions? options,
        CancellationToken cancellationToken = default)
    {
        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            threadId ??= _activeThreadId ?? await StartThreadAsync(cancellationToken: cancellationToken);
            _activeThreadId = threadId;
            _turnText.Clear();
            _activeTurn = new TaskCompletionSource<CodexTurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var input = new List<object> { new { type = "text", text = prompt } };
            if (localImagePaths is not null)
            {
                foreach (var path in localImagePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
                    input.Add(new { type = "localImage", path, detail = "high" });
            }

            object parameters = options is { WorkspaceWrite: true, WorkingDirectory: not null }
                ? new
                {
                    threadId, input, cwd = options.WorkingDirectory,
                    approvalPolicy = CodexApprovalPolicies.Normalize(options.ApprovalPolicy),
                    sandboxPolicy = new
                    {
                        type = "workspaceWrite",
                        writableRoots = new[] { options.WorkingDirectory },
                        networkAccess = options.NetworkAccess
                    }
                }
                : new { threadId, input };

            var result = await RequestAsync("turn/start", parameters, cancellationToken);
            if (result.TryGetProperty("turn", out var turn) && turn.TryGetProperty("id", out var turnId))
                _activeTurnId = turnId.GetString();

            using var registration = cancellationToken.Register(() => _activeTurn.TrySetCanceled(cancellationToken));
            return await _activeTurn.Task;
        }
        finally { _turnGate.Release(); }
    }

    public async Task DeleteThreadAsync(string threadId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return;
        await RequestAsync("thread/delete", new { threadId }, cancellationToken);
        if (string.Equals(_activeThreadId, threadId, StringComparison.Ordinal))
        {
            _activeThreadId = null;
            _activeTurnId = null;
        }
    }

    public async Task<string> ResumeThreadAsync(string threadId, string? cwd = null,
        bool workspaceWrite = false, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var parameters = new Dictionary<string, object?> { ["threadId"] = threadId };
        if (!string.IsNullOrWhiteSpace(cwd)) parameters["cwd"] = cwd;
        if (workspaceWrite)
        {
            parameters["approvalPolicy"] = CodexApprovalPolicies.Untrusted;
            parameters["sandbox"] = "workspace-write";
        }
        var result = await RequestAsync("thread/resume", parameters, cancellationToken);
        _activeThreadId = result.GetProperty("thread").GetProperty("id").GetString() ?? threadId;
        return _activeThreadId;
    }

    public Task InterruptAsync(CancellationToken cancellationToken = default)
    {
        if (_activeThreadId is null || _activeTurnId is null) return Task.CompletedTask;
        return RequestAsync("turn/interrupt", new { threadId = _activeThreadId, turnId = _activeTurnId }, cancellationToken);
    }

    public async Task RespondToApprovalAsync(long requestId, bool approve, bool forSession = false, CancellationToken cancellationToken = default)
    {
        var decision = approve ? (forSession ? "acceptForSession" : "accept") : "decline";
        await SendRawAsync(new { id = requestId, result = new { decision } }, cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectorStatus>> ListConnectorsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RequestAsync("mcpServerStatus/list", new { limit = 100, detail = "toolsAndAuthOnly" }, cancellationToken);
            if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
            var connectors = new List<ConnectorStatus>();
            foreach (var item in data.EnumerateArray())
            {
                var name = GetString(item, "name") ?? GetString(item, "serverName") ?? "Connector";
                var status = GetString(item, "status") ?? "available";
                var authStatus = GetString(item, "authStatus") ?? string.Empty;
                connectors.Add(new ConnectorStatus(name, name, "MCP", true,
                    authStatus.Contains("authenticated", StringComparison.OrdinalIgnoreCase), status));
            }
            return connectors;
        }
        catch
        {
            return [];
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        await SendRawAsync(new { method, id, @params = parameters }, cancellationToken);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task;
    }

    private Task SendNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        SendRawAsync(new { method, @params = parameters }, cancellationToken);

    private async Task SendRawAsync(object payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var json = JsonSerializer.Serialize(payload);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _process!.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            Diagnostic?.Invoke(this, "Sent JSON-RPC message.");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null) break;
                Diagnostic?.Invoke(this, "Received JSON-RPC message.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var id))
                {
                    if (root.TryGetProperty("method", out var requestMethod))
                    {
                        HandleServerRequest(id, requestMethod.GetString() ?? "request", root, line);
                    }
                    else if (_pending.TryRemove(id, out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                            completion.TrySetException(new InvalidOperationException(error.ToString()));
                        else
                            completion.TrySetResult(root.GetProperty("result").Clone());
                    }
                    continue;
                }
                HandleNotification(root, line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ConnectionError?.Invoke(this, ex.Message);
            _activeTurn?.TrySetResult(new CodexTurnResult(_activeThreadId ?? "", _activeTurnId, _turnText.ToString(), false, ex.Message));
        }
    }

    private void HandleServerRequest(long id, string method, JsonElement root, string raw)
    {
        var parameters = root.TryGetProperty("params", out var value) ? value : default;
        var threadId = GetString(parameters, "threadId");
        var turnId = GetString(parameters, "turnId");
        var reason = GetString(parameters, "reason") ?? GetString(parameters, "command") ?? method;
        ApprovalRequested?.Invoke(this, new ApprovalPrompt(id, method, threadId, turnId, reason, raw));
    }

    private void HandleNotification(JsonElement root, string raw)
    {
        if (!root.TryGetProperty("method", out var methodElement)) return;
        var method = methodElement.GetString() ?? "unknown";
        var parameters = root.TryGetProperty("params", out var value) ? value : default;
        var threadId = GetString(parameters, "threadId") ?? GetNestedString(parameters, "thread", "id") ?? _activeThreadId;
        var turnId = GetString(parameters, "turnId") ?? GetNestedString(parameters, "turn", "id") ?? _activeTurnId;
        var text = ExtractText(method, parameters);
        if (!string.IsNullOrEmpty(text) && method.Contains("delta", StringComparison.OrdinalIgnoreCase)) _turnText.Append(text);
        StreamEvent?.Invoke(this, new CodexStreamEvent(method, threadId, turnId, text, raw));

        if (method == "turn/completed")
        {
            var status = GetNestedString(parameters, "turn", "status") ?? "completed";
            var error = parameters.TryGetProperty("turn", out var turn) && turn.TryGetProperty("error", out var errorValue)
                ? errorValue.ToString() : null;
            _activeTurn?.TrySetResult(new CodexTurnResult(threadId ?? "", turnId, _turnText.ToString(), status == "interrupted", error));
        }
    }

    private static string? ExtractText(string method, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "delta", "text", "message" })
        {
            if (parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        }
        if (parameters.TryGetProperty("item", out var item))
        {
            foreach (var name in new[] { "text", "content" })
            {
                if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            }
        }
        return method.Contains("error", StringComparison.OrdinalIgnoreCase) ? parameters.ToString() : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static string? GetNestedString(JsonElement element, string parent, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(parent, out var value) ? GetString(value, name) : null;

    private async Task ReadErrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process is { HasExited: false })
            {
                var line = await _process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                Diagnostic?.Invoke(this, $"Codex stderr: {line}");
                if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)) ConnectionError?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void EnsureConnected()
    {
        if (!IsConnected) throw new InvalidOperationException("Codex app-server is not connected.");
    }

    private static (string FileName, string Arguments) ResolveLaunch(string executable)
    {
        if (!string.Equals(executable, "codex", StringComparison.OrdinalIgnoreCase))
            return (executable, "app-server");

        // Windows Store packages expose codex.exe through an execution alias, but an
        // unpackaged desktop process cannot launch that WindowsApps path directly.
        // Keep a private, update-stable runtime copy in DAGNUE's local data instead.
        var privateRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXUS", "runtime", "codex.exe");
        if (File.Exists(privateRuntime)) return (privateRuntime, "app-server");

        var commandPath = FindWithWhere("codex.cmd");
        if (!string.IsNullOrWhiteSpace(commandPath))
        {
            var node = FindWithWhere("node.exe");
            var script = Path.Combine(Path.GetDirectoryName(commandPath)!, "node_modules", "@openai", "codex", "bin", "codex.js");
            if (!string.IsNullOrWhiteSpace(node) && File.Exists(script))
                return (node, $"\"{script}\" app-server");
            return (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", $"/d /s /c \"\"{commandPath}\" app-server\"");
        }

        var executablePath = FindWithWhere("codex.exe");
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            if (executablePath.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(privateRuntime)!);
                    File.Copy(executablePath, privateRuntime, overwrite: true);
                    return (privateRuntime, "app-server");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return (executablePath, "app-server");
        }

        return (executable, "app-server");
    }

    private static string? FindWithWhere(string name)
    {
        try
        {
            using var where = Process.Start(new ProcessStartInfo("where.exe", name)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (where is null) return null;
            var output = where.StandardOutput.ReadToEnd();
            where.WaitForExit(2000);
            return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(path => File.Exists(path));
        }
        catch { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime?.Cancel();
        if (_process is { HasExited: false })
        {
            _process.StandardInput.Close();
            if (!_process.WaitForExit(1500)) _process.Kill(true);
        }
        if (_readerTask is not null)
        {
            try { await _readerTask; } catch (OperationCanceledException) { }
        }
        _process?.Dispose();
        _lifetime?.Dispose();
        _writeGate.Dispose();
        _turnGate.Dispose();
    }
}
