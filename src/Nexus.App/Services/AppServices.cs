using Microsoft.UI.Dispatching;
using Nexus.Core.Briefings;
using Nexus.Core.Codex;
using Nexus.Core.Data;
using Nexus.Core.Models;
using Nexus.Core.Scheduling;
using Nexus.Core.Security;
using Nexus.Core.SystemServices;
using Microsoft.Win32;

namespace Nexus.App.Services;

public sealed record InitializationProgress(string Stage, string Detail, int Percent, bool Warning = false);

public sealed class AppServices : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private MainWindow? _window;
    private TrayIconService? _tray;
    private Task? _schedulerLoop;
    private Task? _commonLocationIndexTask;

    public AppServices()
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS");
        Directory.CreateDirectory(dataDirectory);
        DataDirectory = dataDirectory;
        Repository = new NexusRepository(Path.Combine(dataDirectory, "nexus.db"));
        Credentials = new WindowsCredentialStore();
        AccessVault = new AccessVaultService(Repository, Credentials);
        DesktopCapture = new DesktopCaptureService();
        CapabilityBroker = new CapabilityBrokerService(AccessVault, Repository, DesktopCapture);
        Codex = new CodexAppServerClient();
        MiniApps = new MiniAppAgentService(Repository, Codex);
        Notifications = new NotificationService();
        Startup = new StartupService();
        Speech = new SpeechService();
        Health = new SystemHealthService();
        Optimizer = new SystemOptimizationService();
        Applications = new ApplicationInventoryService();
        Scheduler = new ScheduleEngine();
        Routines = new RoutineEngine();
        TaskSchedules = new TaskScheduleEngine();
        Permissions = new PermissionGuard();
        Tools = new LocalComputerTools();
        Briefings = new BriefingService();
        Context = new ContextService();
        FileGraph = new FileGraphService(Path.Combine(dataDirectory, "filesystem-index-v1.bin"));
        FileActivity = new FileActivityService();
        FileSearch = new IndexedFileSearchService(FileGraph, FileActivity);
        RemoteTasks = new RemoteTaskAgentService(DataDirectory, Codex, Notifications, FileSearch,
            CapabilityBroker, () => Settings);
    }

    public string DataDirectory { get; }
    public NexusRepository Repository { get; }
    public WindowsCredentialStore Credentials { get; }
    public AccessVaultService AccessVault { get; }
    public DesktopCaptureService DesktopCapture { get; }
    public CapabilityBrokerService CapabilityBroker { get; }
    public CodexAppServerClient Codex { get; }
    public MiniAppAgentService MiniApps { get; }
    public NotificationService Notifications { get; }
    public RemoteTaskAgentService RemoteTasks { get; }
    public StartupService Startup { get; }
    public SpeechService Speech { get; }
    public SystemHealthService Health { get; }
    public SystemOptimizationService Optimizer { get; }
    public ApplicationInventoryService Applications { get; }
    public ScheduleEngine Scheduler { get; }
    public RoutineEngine Routines { get; }
    public TaskScheduleEngine TaskSchedules { get; }
    public PermissionGuard Permissions { get; }
    public LocalComputerTools Tools { get; }
    public BriefingService Briefings { get; }
    public ContextService Context { get; }
    public FileGraphService FileGraph { get; }
    public FileActivityService FileActivity { get; }
    public IndexedFileSearchService FileSearch { get; }
    public AssistantSettings Settings { get; private set; } = new();
    public bool IsInitialized { get; private set; }
    public bool IsExitRequested { get; private set; }
    public event EventHandler? Initialized;
    public event EventHandler<Reminder>? ReminderTriggered;
    public event EventHandler<InitializationProgress>? InitializationProgressed;

    public void AttachWindow(MainWindow window)
    {
        _window = window;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _tray = new TrayIconService(hwnd, DispatcherQueue.GetForCurrentThread(), window.ShowFromTray, RequestExit);
    }

    public async Task InitializeAsync()
    {
        try
        {
            ReportInitialization("CORE AWAKENING", "Command kernel responding", 6);
            await Repository.InitializeAsync(_lifetime.Token);
            ReportInitialization("MEMORY VAULT", "Local encrypted workspace verified", 18);
            Settings = await Repository.GetSettingsAsync(_lifetime.Token);
            await CapabilityBroker.EnsureDefaultProfilesAsync(_lifetime.Token);
            CapabilityBroker.Start();
            if (Settings.AssistantName.Equals("NEXUS", StringComparison.OrdinalIgnoreCase))
            {
                Settings = Settings with { AssistantName = "DAGNUE" };
                await Repository.SaveSettingsAsync(Settings, _lifetime.Token);
            }
            ReportInitialization("IDENTITY MATRIX", $"Profile loaded: {Settings.AssistantName.ToUpperInvariant()}", 30);

            var health = Health.Capture();
            ReportInitialization("SYSTEM TELEMETRY",
                $"RAM {health.MemoryPercent:0}%  ·  DISK {health.DiskPercent:0}%  ·  {health.PowerStatus.ToUpperInvariant()}", 43,
                health.DiskPercent >= 90);

            ReportInitialization("UPDATE SENTINEL", InspectWindowsUpdateState(), 55);
            FileActivity.Start();
            _commonLocationIndexTask = RefreshCommonLocationsInBackgroundAsync(_lifetime.Token);
            Notifications.Register();
            ReportInitialization("SIGNAL ARRAY", health.NetworkAvailable ? "Network and notifications online" : "Offline mode available", 65,
                !health.NetworkAvailable);

            ReportInitialization("CODEX NEURAL LINK", "Establishing analysis channel", 74);
            try
            {
                await Codex.StartAsync(cancellationToken: _lifetime.Token);
                ReportInitialization("CODEX NEURAL LINK", "Analysis channel synchronized", 88);
            }
            catch (Exception ex)
            {
                LogCrash(ex);
                ReportInitialization("CODEX NEURAL LINK", "Local systems ready · Codex link deferred", 88, true);
            }
            _schedulerLoop = RunSchedulerAsync(_lifetime.Token);
            RemoteTasks.Start();
            ReportInitialization("TEMPORAL ENGINE", "Schedules and routines armed", 96);
            IsInitialized = true;
            ReportInitialization("DAGNUE ONLINE", "All available systems initialized", 100);
            Initialized?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
        }
    }

    private void ReportInitialization(string stage, string detail, int percent, bool warning = false) =>
        InitializationProgressed?.Invoke(this, new InitializationProgress(stage, detail, percent, warning));

    private async Task RefreshCommonLocationsInBackgroundAsync(CancellationToken cancellationToken)
    {
        try { await FileSearch.RefreshCommonLocationsAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { LogCrash(ex); }
    }

    private static string InspectWindowsUpdateState()
    {
        var build = Environment.OSVersion.Version.Build;
        using var rebootKey = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        if (rebootKey is not null) return $"WINDOWS BUILD {build} · RESTART PENDING";

        using var updateKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\Settings");
        return updateKey is null
            ? $"WINDOWS BUILD {build} · UPDATE STATUS LOCAL"
            : $"WINDOWS BUILD {build} · UPDATE CHANNEL READY";
    }

    public async Task SaveSettingsAsync(AssistantSettings settings)
    {
        Settings = settings;
        await Repository.SaveSettingsAsync(settings, _lifetime.Token);
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try
            {
                var reminders = await Repository.GetRemindersAsync(true, cancellationToken);
                foreach (var reminder in Scheduler.GetDue(reminders, DateTimeOffset.Now))
                {
                    if (Settings.NotificationsEnabled && _tray?.IsPaused != true)
                        Notifications.Show($"Task due: {reminder.Title}",
                            reminder.AiInstruction ?? reminder.Notes ?? "Open TASKS to review what needs to be done.", $"reminder:{reminder.Id}");
                    ReminderTriggered?.Invoke(this, reminder);
                    await Repository.SaveReminderAsync(Scheduler.MarkTriggered(reminder, DateTimeOffset.Now), cancellationToken);
                    await Repository.AddAuditAsync(new AuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                        "schedule", reminder.Title, "triggered", ActionRisk.ReadOnly), cancellationToken);
                }
                var taskSchedules = await Repository.GetTaskSchedulesAsync(enabledOnly: true, cancellationToken: cancellationToken);
                foreach (var schedule in TaskSchedules.GetDue(taskSchedules, DateTimeOffset.Now))
                {
                    var workspace = await Repository.GetTaskWorkspaceAsync(schedule.WorkspaceId, cancellationToken);
                    if (workspace is null) continue;
                    string? summary = null;
                    try
                    {
                        var run = await MiniApps.ExecuteAsync(workspace,
                            $"Scheduled task '{schedule.Name}' is due. Execute it now: {schedule.Prompt}", cancellationToken: cancellationToken);
                        summary = run.Command.ResultSummary;
                        if (Settings.NotificationsEnabled)
                            Notifications.Show($"Mini system: {schedule.Name}", summary ?? "Scheduled task completed.");
                    }
                    catch (Exception ex) { summary = $"Failed: {ex.Message}"; }
                    await Repository.SaveTaskScheduleAsync(TaskSchedules.MarkRun(schedule, DateTimeOffset.Now, summary), cancellationToken);
                    await Repository.AddAuditAsync(new AuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                        "mini-system-schedule", schedule.Name, summary?.StartsWith("Failed:") == true ? "failed" : "completed",
                        ActionRisk.Medium, summary), cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { LogCrash(ex); }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    public void LogCrash(Exception exception)
    {
        try
        {
            File.AppendAllText(Path.Combine(DataDirectory, "nexus.log"),
                $"[{DateTimeOffset.Now:O}] {exception}\n\n");
        }
        catch { }
    }

    public void RequestExit()
    {
        IsExitRequested = true;
        _ = DisposeAsync();
        _window?.ExitFromTray();
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_schedulerLoop is not null)
        {
            try { await _schedulerLoop; } catch (OperationCanceledException) { }
        }
        if (_commonLocationIndexTask is not null)
        {
            try { await _commonLocationIndexTask; } catch (OperationCanceledException) { }
        }
        _tray?.Dispose();
        Notifications.Dispose();
        FileSearch.Dispose();
        FileActivity.Dispose();
        await Speech.DisposeAsync();
        await RemoteTasks.DisposeAsync();
        await CapabilityBroker.DisposeAsync();
        await Codex.DisposeAsync();
        await Repository.DisposeAsync();
        _lifetime.Dispose();
    }
}
