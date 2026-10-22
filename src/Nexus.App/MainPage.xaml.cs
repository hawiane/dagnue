using System.Text;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Nexus.App.Controls;
using Nexus.App.Services;
using Nexus.Core.Models;
using Nexus.Core.SystemServices;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Nexus.App;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _telemetryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _remoteTaskSyncTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _modeDockCollapseTimer = new() { Interval = TimeSpan.FromMilliseconds(360) };
    private TextBlock? _streamingMessage;
    private int _pendingApprovals;
    private int _userTaskCount;
    private int _completedLocalTaskCount;
    private int _scheduledAppTaskCount;
    private bool _turnRunning;
    private bool _taskTurnRunning;
    private List<TaskWorkspace> _taskWorkspaces = [];
    private readonly Dictionary<string, TaskWorkspaceWindow> _taskWorkspaceWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskDetailWindow> _taskDetailWindows = new(StringComparer.OrdinalIgnoreCase);
    private NewTaskWindow? _newTaskWindow;
    private FindWindow? _findWindow;
    private AppsWindow? _appsWindow;
    private AccessVaultWindow? _accessVaultWindow;
    private TaskWorkspace? _selectedTaskWorkspace;
    private readonly StringBuilder _taskStreamBuffer = new();
    private bool _taskWorkspaceMaximized;
    private bool _remoteDashboardRefreshing;
    private string? _selectedRemoteTaskId;
    private string? _remoteDashboardSignature;
    private IReadOnlyList<RemoteTaskDashboardItem> _remoteDashboardTasks = [];
    private static readonly Uri RemoteTaskDeskUri = new("https://dhalor-remote-task-desk.urgptcry.chatgpt.site");
    private readonly List<Button> _navigationButtons = [];
    private Storyboard? _navigationMotion;
    private Storyboard? _channelMotion;
    private Storyboard? _modeDockMotion;
    private Storyboard? _coreModeMotion;
    private Storyboard? _coreDashboardAmbientMotion;
    private Storyboard? _coreDashboardTransitionMotion;
    private Storyboard? _notificationDockMotion;
    private Storyboard? _notificationScrollMotion;
    private bool _windowInteractionActive;
    private bool _channelExpanded;
    private bool _modeDockExpanded;
    private bool _meshModeActive;
    private bool _coreDashboardExpanded;
    private bool _notificationCollapsed;
    private bool _notificationTransitioning;
    private int _notificationIndex;
    private string _briefingNotificationTitle = "DAGNUE is preparing your briefing.";
    private string _briefingNotificationBody = "Local systems are starting.";
    private readonly List<AdvisoryNotification> _notificationItems = [];
    private FrameworkElement? _activeSheet;
    private readonly HashSet<SystemOptimizationKind> _optimizationsRunning = [];

    private sealed record AdvisoryNotification(string Key, string Title, string Body, DateTimeOffset Timestamp);

    public MainPage()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(GlobalEscape_KeyDown), true);
        OrbitalNavigator.RotationChanged += (_, args) => NeuralCore.NavigationRotationDegrees = (float)args.Degrees;
        NeuralCore.OptimizationRequested += NeuralCore_OptimizationRequested;
        _navigationButtons.AddRange([CoreNavButton, ScheduleNavButton, ActivityNavButton, MemoryNavButton, SettingsNavButton]);
        WorkspaceGrid.SizeChanged += (_, _) => PositionReturnButton();
        SchedulePanel.SizeChanged += (_, _) => PositionReturnButton();
        ActivityPanel.SizeChanged += (_, _) => PositionReturnButton();
        MemoryPanel.SizeChanged += (_, _) => PositionReturnButton();
        SettingsPanel.SizeChanged += (_, _) => PositionReturnButton();
        TasksPanel.SizeChanged += (_, _) => PositionReturnButton();
        SetActiveNavigation(CoreNavButton);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _telemetryTimer.Tick += async (_, _) => await RefreshTelemetryAsync();
        _remoteTaskSyncTimer.Tick += async (_, _) =>
        {
            if (_activeSheet is null || RemoteTaskTabContent.Visibility == Visibility.Visible)
                await RefreshRemoteTaskDashboardAsync();
        };
        _modeDockCollapseTimer.Tick += (_, _) =>
        {
            _modeDockCollapseTimer.Stop();
            AnimateModeDock(false);
        };
    }

    private async void NeuralCore_OptimizationRequested(object? sender, SystemOptimizationKind kind)
    {
        if (!_optimizationsRunning.Add(kind)) return;
        NeuralCore.SetOptimizationState(kind, NeuralCoreControl.OptimizationVisualState.Working);
        try
        {
            var result = await App.Services.Optimizer.OptimizeAsync(kind);
            NeuralCore.SetOptimizationState(kind, NeuralCoreControl.OptimizationVisualState.Success);
            App.Services.Notifications.Show($"{kind} optimization complete", result.Summary);
            await App.Services.Repository.AddAuditAsync(new AuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                "system-optimization", kind.ToString(), "completed", ActionRisk.Low, result.Summary));
            await RefreshTelemetryAsync();
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            NeuralCore.SetOptimizationState(kind, NeuralCoreControl.OptimizationVisualState.Failed);
            App.Services.Notifications.Show($"{kind} optimization could not finish", ex.Message);
            App.Services.LogCrash(ex);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        finally
        {
            NeuralCore.SetOptimizationState(kind, NeuralCoreControl.OptimizationVisualState.Idle);
            _optimizationsRunning.Remove(kind);
        }
    }

    private void GlobalEscape_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || e.Handled) return;

        if (_channelExpanded)
        {
            _channelExpanded = false;
            AnimateChannelDock(false);
            FocusCoreSurface();
            e.Handled = true;
            return;
        }

        if (_meshModeActive && MeshBrain.HandleEscape())
        {
            e.Handled = true;
            return;
        }

        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is Control)
        {
            FocusCoreSurface();
            e.Handled = true;
        }
    }

    private void FocusCoreSurface()
    {
        if (_meshModeActive) MeshBrain.Focus(FocusState.Programmatic);
        else OrbitalNavigator.Focus(FocusState.Programmatic);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var services = App.Services;
        services.Initialized += Services_Initialized;
        services.ReminderTriggered += Services_ReminderTriggered;
        services.Codex.StreamEvent += Codex_StreamEvent;
        services.Codex.ApprovalRequested += Codex_ApprovalRequested;
        services.Codex.ConnectionError += Codex_ConnectionError;
        services.Notifications.NotificationShown += Notifications_NotificationShown;
        MeshBrain.BeginFilesystemIndex(services.FileGraph);
        MeshBrain.AttachFileActivity(services.FileActivity);
        RefreshNotificationCarousel();
        ApplyNotificationDockState(true);
        ApplyChannelDockState(_channelExpanded);
        ApplyModeDockState(false);
        ApplyCoreModeState(false);
        ApplyCoreTaskDashboardState(false);
        ConfigureCoreTaskDashboardMotion();
        ConfigureNavigationMotion(reducedMotion: false);
        _telemetryTimer.Start();
        _remoteTaskSyncTimer.Start();
        AddConversationCard("system", "DAGNUE native shell started. Establishing the secure Codex channel.");
        if (services.IsInitialized) _ = ApplyInitializedStateAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _telemetryTimer.Stop();
        _remoteTaskSyncTimer.Stop();
        _modeDockCollapseTimer.Stop();
        _navigationMotion?.Stop();
        _channelMotion?.Stop();
        _modeDockMotion?.Stop();
        _coreModeMotion?.Stop();
        _coreDashboardAmbientMotion?.Stop();
        _coreDashboardTransitionMotion?.Stop();
        _notificationDockMotion?.Stop();
        _notificationScrollMotion?.Stop();
        var services = App.Services;
        services.Initialized -= Services_Initialized;
        services.ReminderTriggered -= Services_ReminderTriggered;
        services.Codex.StreamEvent -= Codex_StreamEvent;
        services.Codex.ApprovalRequested -= Codex_ApprovalRequested;
        services.Codex.ConnectionError -= Codex_ConnectionError;
        services.Notifications.NotificationShown -= Notifications_NotificationShown;
        MeshBrain.DetachFileActivity();
        MeshBrain.StopFilesystemIndex();
    }

    public void SetWindowInteractionMode(bool active)
    {
        if (_windowInteractionActive == active) return;
        _windowInteractionActive = active;
        NeuralCore.SetAnimationPaused(active || _meshModeActive);
        OrbitalNavigator.SetAnimationPaused(active || _meshModeActive);
        MeshBrain.SetAnimationPaused(active || !_meshModeActive);
        ScheduleChrome.SetAnimationPaused(active);
        ActivityChrome.SetAnimationPaused(active);
        MemoryChrome.SetAnimationPaused(active);
        SettingsChrome.SetAnimationPaused(active);
        if (active)
        {
            _navigationMotion?.Pause();
            _coreDashboardAmbientMotion?.Pause();
            _telemetryTimer.Stop();
        }
        else
        {
            _navigationMotion?.Resume();
            _coreDashboardAmbientMotion?.Resume();
            _telemetryTimer.Start();
            if (_activeSheet is null || RemoteTaskTabContent.Visibility == Visibility.Visible)
                _ = RefreshRemoteTaskDashboardAsync(force: true);
        }
    }

    private void Services_Initialized(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () => await ApplyInitializedStateAsync());

    private async Task ApplyInitializedStateAsync()
    {
        LoadSettings(App.Services.Settings);
        await RefreshTelemetryAsync();
        await RefreshRemindersAsync();
        await RefreshTaskWorkspacesAsync();
        await RefreshAuditAsync();
        SetBrainState(App.Services.Codex.IsConnected ? BrainState.Idle : BrainState.Offline);
        AddConversationCard("system", App.Services.Codex.IsConnected
            ? "Codex connected. Analysis, streamed progress, approvals, and cancellation are online."
            : "Codex is unavailable. Local schedules, health monitoring, and tools remain online.");
    }

    private async Task RefreshTelemetryAsync()
    {
        if (!App.Services.IsInitialized) return;
        var snapshot = App.Services.Health.Capture(App.Services.Codex.IsConnected,
            _turnRunning || _taskTurnRunning ? 1 : 0, _pendingApprovals);
        CpuText.Text = $"{snapshot.CpuPercent:0}%";
        MemoryText.Text = $"{snapshot.MemoryPercent:0}%";
        DiskText.Text = $"{snapshot.DiskPercent:0}%";
        TaskText.Text = snapshot.ActiveTasks.ToString();
        PowerText.Text = $"POWER: {snapshot.PowerStatus.ToUpperInvariant()}";
        NetworkText.Text = $"NETWORK: {(snapshot.NetworkAvailable ? "ONLINE" : "OFFLINE")}";
        ClockText.Text = DateTimeOffset.Now.ToString("ddd, MMM dd · HH:mm:ss").ToUpperInvariant();
        SidebarDayText.Text = DateTimeOffset.Now.ToString("dddd").ToUpperInvariant();
        SidebarTimeText.Text = DateTimeOffset.Now.ToString("HH:mm");
        SidebarDateText.Text = DateTimeOffset.Now.ToString("dd MMM yyyy").ToUpperInvariant();
        CodexText.Text = $"CODEX: {(snapshot.CodexConnected ? "CONNECTED" : "OFFLINE")}";
        QueueText.Text = $"QUEUE: {snapshot.ActiveTasks}";
        ApprovalText.Text = $"APPROVALS: {snapshot.PendingApprovals}";
        OrbitalNavigator.UpdateTelemetry(snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.DiskPercent,
            snapshot.PowerStatus, snapshot.NetworkAvailable, snapshot.CodexConnected, snapshot.ActiveTasks,
            snapshot.PendingApprovals, DateTimeOffset.Now);
        NeuralCore.UpdateTelemetry(snapshot);
        NeuralCore.UpdateTaskLoad(snapshot.ActiveTasks + _scheduledAppTaskCount + _taskWorkspaces.Count, _userTaskCount);
        var capabilityProfiles = await App.Services.AccessVault.GetProfilesAsync(enabledOnly: true);
        var capabilityDomains = capabilityProfiles.SelectMany(DagnueCapabilityDictionary.GetProfileTags)
            .Where(tag => tag != "GENERAL").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var reasoningReadiness = snapshot.CodexConnected ? 30 : 5;
        var vaultReadiness = Math.Min(25, capabilityProfiles.Count * 4);
        var domainReadiness = Math.Min(25, capabilityDomains * 4);
        var systemReadiness = (int)Math.Round(SystemHealthScore.Calculate(snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.DiskPercent) * 0.20);
        var readiness = reasoningReadiness + vaultReadiness + domainReadiness + systemReadiness;
        NeuralCore.UpdateAiReadiness(readiness, snapshot.CodexConnected ? 45 : 0, capabilityProfiles.Count, capabilityDomains,
            reasoningReadiness, vaultReadiness, domainReadiness, systemReadiness);
        MeshBrain.UpdateTelemetry(snapshot);
        ConnectionLabel.Text = snapshot.CodexConnected ? "CODEX CONNECTED" : "LOCAL MODE";
        ConnectionDot.Fill = new SolidColorBrush(snapshot.CodexConnected
            ? Color.FromArgb(255, 0, 224, 166) : Color.FromArgb(255, 255, 184, 77));
        var reminders = await App.Services.Repository.GetRemindersAsync(true);
        var briefing = App.Services.Briefings.Create(reminders, snapshot, DateTimeOffset.Now);
        _briefingNotificationTitle = $"{briefing.Greeting}.";
        _briefingNotificationBody = briefing.Notices.Count == 0
            ? briefing.Summary
            : $"{briefing.Summary} {string.Join(" ", briefing.Notices)}";
        RefreshNotificationCarousel();
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendPromptAsync();

    private async Task SendPromptAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || _turnRunning) return;
        PromptBox.Text = string.Empty;
        AddConversationCard("user", prompt);
        _streamingMessage = AddConversationCard("assistant", "", true);
        SetTurnState(true);
        SetBrainState(BrainState.Understanding);
        try
        {
            if (!App.Services.Codex.IsConnected) await App.Services.Codex.StartAsync();
            SetBrainState(BrainState.Reasoning);
            var safePrompt = "You are the reasoning engine inside DAGNUE, a native Windows desktop assistant. " +
                "Analyze the user's request carefully. Be concise and useful. Do not perform destructive, credential, payment, " +
                "security-setting, or administrator actions without explicit interactive approval. User request: " + prompt;
            var result = await App.Services.Codex.RunTurnAsync(safePrompt);
            if (_streamingMessage is not null && string.IsNullOrWhiteSpace(_streamingMessage.Text))
                _streamingMessage.Text = string.IsNullOrWhiteSpace(result.FinalText) ? "Task completed without a text response." : result.FinalText;
            SetBrainState(result.Error is not null ? BrainState.Warning : BrainState.Completed);
            await App.Services.Repository.SaveMessageAsync(new ConversationMessage(Guid.NewGuid().ToString("N"), result.ThreadId,
                "user", prompt, DateTimeOffset.Now));
            await App.Services.Repository.SaveMessageAsync(new ConversationMessage(Guid.NewGuid().ToString("N"), result.ThreadId,
                "assistant", _streamingMessage?.Text ?? result.FinalText, DateTimeOffset.Now, result.Error is null ? "complete" : "error"));
            await App.Services.Repository.AddAuditAsync(new AuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                "codex", prompt, result.Error is null ? "completed" : "failed", ActionRisk.ReadOnly, result.Error));
            ThreadLabel.Text = $"Thread {ShortId(result.ThreadId)}";
        }
        catch (Exception ex)
        {
            if (_streamingMessage is not null) _streamingMessage.Text = $"Codex connection error: {ex.Message}";
            SetBrainState(BrainState.Warning);
            App.Services.LogCrash(ex);
        }
        finally
        {
            SetTurnState(false);
            _streamingMessage = null;
            await Task.Delay(900);
            SetBrainState(App.Services.Codex.IsConnected ? BrainState.Idle : BrainState.Offline);
        }
    }

    private void Codex_StreamEvent(object? sender, CodexStreamEvent e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_taskTurnRunning && !string.IsNullOrEmpty(e.Text))
            {
                _taskStreamBuffer.Append(e.Text);
                var live = _taskStreamBuffer.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
                if (live.Length > 220) live = "…" + live[^219..];
                TaskAiStatusDetailText.Text = string.IsNullOrWhiteSpace(live)
                    ? "Codex is building the task form..."
                    : live;
            }
            if (!string.IsNullOrEmpty(e.Text) && _streamingMessage is not null)
            {
                _streamingMessage.Text += e.Text;
                ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null);
            }
            if (e.Method.Contains("command", StringComparison.OrdinalIgnoreCase)) SetBrainState(BrainState.Acting);
            else if (e.Method.Contains("reason", StringComparison.OrdinalIgnoreCase)) SetBrainState(BrainState.Reasoning);
        });
    }

    private void Codex_ApprovalRequested(object? sender, ApprovalPrompt prompt)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            _pendingApprovals++;
            SetBrainState(BrainState.AwaitingApproval);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Codex requests permission",
                Content = $"{prompt.Summary}\n\nReview this action carefully before allowing it.",
                PrimaryButtonText = "Allow once",
                CloseButtonText = "Decline",
                DefaultButton = ContentDialogButton.Close
            };
            var result = await dialog.ShowAsync();
            await App.Services.Codex.RespondToApprovalAsync(prompt.RequestId, result == ContentDialogResult.Primary);
            _pendingApprovals--;
            SetBrainState(_turnRunning ? BrainState.Reasoning : BrainState.Idle);
            await App.Services.Repository.AddAuditAsync(new AuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                "approval", prompt.Summary, result == ContentDialogResult.Primary ? "allowed" : "declined", ActionRisk.High));
        });
    }

    private void Codex_ConnectionError(object? sender, string error) => DispatcherQueue.TryEnqueue(() =>
    {
        ConnectionLabel.Text = "CODEX CONNECTION ISSUE";
        SetBrainState(BrainState.Warning);
    });

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        await App.Services.Codex.InterruptAsync();
        SetBrainState(BrainState.Warning);
    }

    private async void Voice_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Settings.VoiceEnabled) return;
        VoiceButton.IsEnabled = false;
        SetBrainState(BrainState.Listening);
        try
        {
            var text = await App.Services.Speech.ListenOnceAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                PromptBox.Text = text;
                await SendPromptAsync();
            }
        }
        catch (Exception ex)
        {
            AddConversationCard("system", $"Voice recognition is unavailable: {ex.Message}");
        }
        finally
        {
            VoiceButton.IsEnabled = true;
            if (!_turnRunning) SetBrainState(App.Services.Codex.IsConnected ? BrainState.Idle : BrainState.Offline);
        }
    }

    private async void LaunchApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string appName }) return;
        var request = App.Services.Tools.CreateLaunchRequest(appName);
        var decision = App.Services.Permissions.Evaluate(request, App.Services.Settings);
        var allowed = decision.Allowed;
        if (decision.RequiresConfirmation)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "Allow computer action?", Content = request.Summary,
                PrimaryButtonText = "Open", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
            };
            allowed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        if (allowed)
        {
            App.Services.Tools.Launch(request.Parameters!["target"]);
            await App.Services.Repository.AddAuditAsync(new AuditEntry(request.Id, DateTimeOffset.Now, "application",
                request.Summary, "completed", request.Risk));
        }
    }

    private void AddReminder_Click(object sender, RoutedEventArgs e)
    {
        if (_newTaskWindow is not null)
        {
            _newTaskWindow.Activate();
            return;
        }
        var window = new NewTaskWindow();
        _newTaskWindow = window;
        window.TaskCreated += _ => DispatcherQueue.TryEnqueue(async () => await RefreshRemindersAsync());
        window.Closed += (_, _) => _newTaskWindow = null;
        window.Activate();
    }

    private async Task RefreshRemindersAsync()
    {
        ReminderList.Children.Clear();
        var reminders = (await App.Services.Repository.GetRemindersAsync()).ToList();
        await ImportLegacyRoutinesAsync(reminders);
        reminders = (await App.Services.Repository.GetRemindersAsync()).ToList();
        _userTaskCount = reminders.Count(reminder => reminder.Status != TaskProgressStatus.Done);
        _completedLocalTaskCount = reminders.Count(reminder => reminder.Status == TaskProgressStatus.Done);
        var ordered = reminders.OrderBy(x => x.Status == TaskProgressStatus.Done ? 2 : x.IsToday ? 0 : 1)
            .ThenByDescending(x => x.Urgency).ThenBy(x => x.HasSchedule ? x.DueAt : x.Deadline ?? DateTimeOffset.MaxValue);
        string? currentGroup = null;
        foreach (var reminder in ordered)
        {
            var group = reminder.Status == TaskProgressStatus.Done ? "COMPLETED" : reminder.IsToday ? "TODAY" : "UPCOMING / SOMEDAY";
            if (group != currentGroup)
            {
                ReminderList.Children.Add(new TextBlock
                {
                    Text = group, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = group == "TODAY" ? CyanBrush() : MutedBrush(),
                    Margin = new Thickness(0, currentGroup is null ? 0 : 8, 0, 2)
                });
                currentGroup = group;
            }
            var delete = TaskIconButton("×", "Remove task", reminder.Id);
            delete.Click += DeleteReminder_Click;
            var open = TaskIconButton("↗", "Open task details", reminder.Id);
            open.Click += OpenTaskDetail_Click;
            var status = TaskIconButton(StatusGlyph(reminder.Status),
                $"Change status: {StatusLabel(reminder.Status)}", reminder.Id);
            status.Click += CycleTaskStatus_Click;
            var runAi = TaskIconButton("✦", "Run this task with AI", reminder.Id);
            runAi.IsEnabled = reminder.Status != TaskProgressStatus.Done && !_turnRunning && !_taskTurnRunning;
            runAi.Click += RunTaskWithAi_Click;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{UrgencyGlyph(reminder.Urgency)}  {reminder.Title}", FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = UrgencyBrush(reminder.Urgency) },
                    new TextBlock { Text = TaskTimingLabel(reminder), Foreground = MutedBrush(), FontSize = 10, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = AiStateLabel(reminder), Foreground = MutedBrush(), FontSize = 9, TextWrapping = TextWrapping.Wrap }
                }
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(status);
            actions.Children.Add(runAi);
            actions.Children.Add(open);
            actions.Children.Add(delete);
            Grid.SetColumn(actions, 1); grid.Children.Add(actions);
            ReminderList.Children.Add(WrapTaskRow(grid));
        }
        if (ReminderList.Children.Count == 0) ReminderList.Children.Add(new TextBlock { Text = "No tasks yet. Add something for today or give it a schedule.", Foreground = MutedBrush() });

        _scheduledAppTaskCount = reminders.Count(item => item.TaskType == UserTaskType.Recurring && item.Status != TaskProgressStatus.Done);
        var due = reminders.Count(item => item.Status != TaskProgressStatus.Done &&
            ((item.HasSchedule && item.DueAt <= DateTimeOffset.Now) || item.Deadline <= DateTimeOffset.Now));
        var doneToday = reminders.Count(item => item.CompletedAt?.LocalDateTime.Date == DateTime.Today);
        ScheduleNoticeText.Text = due > 0
            ? $"TASK SIGNALS // {due} NEED ATTENTION  ·  {doneToday} COMPLETED TODAY — open a task to review results or ask AI what comes next"
            : $"TASK SIGNALS // NOTHING OVERDUE  ·  {doneToday} COMPLETED TODAY — completed work and result links remain in each task";
        NeuralCore.UpdateTaskLoad((_turnRunning || _taskTurnRunning ? 1 : 0) + _scheduledAppTaskCount + _taskWorkspaces.Count, _userTaskCount);
    }

    private async void CycleTaskStatus_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var task = (await App.Services.Repository.GetRemindersAsync()).FirstOrDefault(x => x.Id == id);
        if (task is null) return;
        var next = task.Status switch
        {
            TaskProgressStatus.NotYet => TaskProgressStatus.Doing,
            TaskProgressStatus.Doing => TaskProgressStatus.Done,
            _ => TaskProgressStatus.NotYet
        };
        var updated = next == TaskProgressStatus.Done
            ? UserTaskLifecycle.Complete(task, "Marked done manually.", kind: "manual-completion")
            : task with { Status = next, IsEnabled = task.HasSchedule && task.DueAt > DateTimeOffset.Now };
        await App.Services.Repository.SaveReminderAsync(updated);
        if (next == TaskProgressStatus.Done)
            App.Services.Notifications.Show(task.TaskType == UserTaskType.Recurring ? "Routine occurrence completed" : "Task completed", task.Title);
        await RefreshRemindersAsync();
    }

    private async void RunTaskWithAi_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } || _turnRunning || _taskTurnRunning) return;
        var task = (await App.Services.Repository.GetRemindersAsync()).FirstOrDefault(x => x.Id == id);
        if (task is null || task.Status == TaskProgressStatus.Done) return;

        await App.Services.Repository.SaveReminderAsync(task with { Status = TaskProgressStatus.Doing });
        await RefreshRemindersAsync();
        var instruction = string.IsNullOrWhiteSpace(task.AiInstruction) ? task.Title : task.AiInstruction;
        var taskTags = task.Tags is { Count: > 0 } ? task.Tags : DagnueCapabilityDictionary.SuggestTags($"{task.Title} {instruction}");
        var capabilityProfiles = await App.Services.AccessVault.GetProfilesAsync(enabledOnly: true);
        var capabilityRoute = DagnueCapabilityDictionary.BuildRoutingContext(taskTags, capabilityProfiles);
        var prompt = $"Execute this to-do using the computer tools available to you. Task: {task.Title}. " +
            $"Task instructions: {instruction}. Urgency: {task.Urgency}. " +
            "Use the selected tag route first and widen to general search only if it is insufficient. " +
            "Respect the active permission boundary and request interactive approval before sensitive or destructive actions.\n\n" + capabilityRoute;
        AddConversationCard("user", $"Run scheduled task: {task.Title}");
        _streamingMessage = AddConversationCard("assistant", "", true);
        SetTurnState(true);
        SetBrainState(BrainState.Acting);
        try
        {
            if (!App.Services.Codex.IsConnected) await App.Services.Codex.StartAsync();
            var previousThread = App.Services.Codex.ActiveThreadId;
            var threadId = task.AiThreadId;
            if (string.IsNullOrWhiteSpace(threadId))
                threadId = await App.Services.Codex.StartThreadAsync(model: NullIfWhiteSpace(App.Services.Settings.CodexModel));
            else
                try { threadId = await App.Services.Codex.ResumeThreadAsync(threadId); }
                catch { threadId = await App.Services.Codex.StartThreadAsync(model: NullIfWhiteSpace(App.Services.Settings.CodexModel)); }
            var result = await App.Services.Codex.RunTurnAsync(prompt, threadId);
            if (!string.IsNullOrWhiteSpace(previousThread) && !string.Equals(previousThread, threadId, StringComparison.Ordinal))
                try { await App.Services.Codex.ResumeThreadAsync(previousThread); } catch { }
            var summary = string.IsNullOrWhiteSpace(result.FinalText) ? "AI task completed." : result.FinalText.Trim();
            if (_streamingMessage is not null && string.IsNullOrWhiteSpace(_streamingMessage.Text)) _streamingMessage.Text = summary;
            var succeeded = result.Error is null && !result.WasInterrupted;
            var executionState = task with
            {
                Status = succeeded ? TaskProgressStatus.Doing : TaskProgressStatus.NotYet,
                AiLastRunAt = DateTimeOffset.Now,
                AiLastResult = succeeded ? summary : result.Error ?? "AI run interrupted.",
                AiThreadId = result.ThreadId
            };
            var updated = succeeded
                ? UserTaskLifecycle.Complete(executionState, summary, kind: "ai-completion")
                : UserTaskLifecycle.AddActivity(executionState, "ai-failed", executionState.AiLastResult ?? "AI run interrupted.");
            await App.Services.Repository.SaveReminderAsync(updated);
            await App.Services.Repository.AddAuditAsync(new AuditEntry(Guid.NewGuid().ToString("N"), DateTimeOffset.Now,
                "task-ai", task.Title, succeeded ? "completed" : "failed", ActionRisk.Medium, updated.AiLastResult));
            App.Services.Notifications.Show(succeeded ? "AI task completed" : "AI task needs attention", task.Title);
            SetBrainState(succeeded ? BrainState.Completed : BrainState.Warning);
        }
        catch (Exception ex)
        {
            await App.Services.Repository.SaveReminderAsync(task with
            {
                Status = TaskProgressStatus.NotYet, AiLastRunAt = DateTimeOffset.Now, AiLastResult = $"Failed: {ex.Message}"
            });
            if (_streamingMessage is not null) _streamingMessage.Text = $"AI task failed: {ex.Message}";
            SetBrainState(BrainState.Warning);
            App.Services.LogCrash(ex);
        }
        finally
        {
            SetTurnState(false);
            _streamingMessage = null;
            await RefreshRemindersAsync();
            await Task.Delay(900);
            SetBrainState(App.Services.Codex.IsConnected ? BrainState.Idle : BrainState.Offline);
        }
    }

    private async void DeleteReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            await App.Services.Repository.DeleteReminderAsync(id);
            await RefreshRemindersAsync();
        }
    }

    private async void OpenTaskDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (_taskDetailWindows.TryGetValue(id, out var existing))
        {
            existing.Activate();
            return;
        }
        var task = (await App.Services.Repository.GetRemindersAsync()).FirstOrDefault(item => item.Id == id);
        if (task is null) return;
        var window = new TaskDetailWindow(task);
        _taskDetailWindows[id] = window;
        window.TaskUpdated += _ => DispatcherQueue.TryEnqueue(async () => await RefreshRemindersAsync());
        window.Closed += (_, _) => _taskDetailWindows.Remove(id);
        window.Activate();
    }

    private async Task ImportLegacyRoutinesAsync(IReadOnlyCollection<Reminder> existing)
    {
        var ids = existing.Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var routine in await App.Services.Repository.GetRoutinesAsync())
        {
            if (!routine.IsEnabled) continue;
            var id = $"legacy-routine-{routine.Id}";
            if (ids.Contains(id))
            {
                await App.Services.Repository.SaveRoutineAsync(routine with { IsEnabled = false });
                continue;
            }
            var recurrence = routine.Trigger.StartsWith("weekdays:", StringComparison.OrdinalIgnoreCase)
                ? RecurrenceKind.Weekdays
                : routine.Trigger.Contains(':') && !routine.Trigger.StartsWith("daily:", StringComparison.OrdinalIgnoreCase)
                    ? RecurrenceKind.Weekly : RecurrenceKind.Daily;
            var timeText = routine.Trigger.Split(':', 2).ElementAtOrDefault(1);
            var time = TimeSpan.TryParse(timeText, out var parsed) ? parsed : new TimeSpan(9, 0, 0);
            var due = DateTimeOffset.Now.Date.Add(time);
            if (due <= DateTimeOffset.Now) due = due.AddDays(1);
            var imported = new Reminder(id, routine.Name, "Imported from the former AI Routines section.", due,
                recurrence, routine.IsEnabled, routine.CreatedAt, AiInstruction: routine.Prompt,
                TaskType: UserTaskType.Recurring, ExecutionMode: TaskExecutionMode.AiAssisted,
                Activity: [new TaskActivity(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, "imported", "Converted from legacy AI Routine.")]);
            await App.Services.Repository.SaveReminderAsync(imported);
            await App.Services.Repository.SaveRoutineAsync(routine with { IsEnabled = false });
        }
    }

    private async void CreateTaskWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var title = TaskWorkspaceTitleBox.Text.Trim();
        var instruction = TaskWorkspaceInstructionBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(instruction)) return;
        var now = DateTimeOffset.Now;
        var workspace = new TaskWorkspace(Guid.NewGuid().ToString("N"), title, instruction, null,
            JsonSerializer.Serialize(new
            {
                message = "Mini system ready. Command its AI to build the tools and workflow you need.",
                controls = new object[] { new { id = "task-purpose", type = "text", label = instruction } }
            }), "ready", now, now, 3);
        await App.Services.Repository.SaveTaskWorkspaceAsync(workspace);
        TaskWorkspaceTitleBox.Text = string.Empty;
        TaskWorkspaceInstructionBox.Text = string.Empty;
        await RefreshTaskWorkspacesAsync();
        OpenTaskWorkspace(workspace, buildImmediately: true);
    }

    private async Task RefreshTaskWorkspacesAsync(string? selectId = null)
    {
        if (!App.Services.IsInitialized) return;
        _taskWorkspaces = [.. await App.Services.Repository.GetTaskWorkspacesAsync()];
        for (var index = 0; index < _taskWorkspaces.Count; index++)
        {
            var item = _taskWorkspaces[index];
            if (!string.Equals(item.Status, "error", StringComparison.OrdinalIgnoreCase) || !HasValidTaskForm(item.FormJson)) continue;
            item = item with { Status = "ready", UpdatedAt = DateTimeOffset.Now };
            _taskWorkspaces[index] = item;
            await App.Services.Repository.SaveTaskWorkspaceAsync(item);
        }
        NeuralCore.UpdateTaskLoad((_turnRunning || _taskTurnRunning ? 1 : 0) + _scheduledAppTaskCount + _taskWorkspaces.Count, _userTaskCount);
        TaskWorkspaceList.Children.Clear();
        foreach (var workspace in _taskWorkspaces)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var button = new Button
            {
                Content = $"{workspace.Title}\n{workspace.Status.ToUpperInvariant()} // OPEN MINI APP",
                Tag = workspace.Id,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                MinHeight = 58
            };
            button.Click += TaskWorkspace_Select;
            row.Children.Add(button);
            var delete = new Button { Content = "DELETE", Tag = workspace.Id, MinWidth = 88 };
            delete.Click += DeleteTaskWorkspaceCard_Click;
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);
            TaskWorkspaceList.Children.Add(row);
        }
        if (_taskWorkspaces.Count == 0)
            TaskWorkspaceList.Children.Add(new TextBlock { Text = "NO TASK WORKSPACES", Foreground = MutedBrush() });
        _selectedTaskWorkspace = null;
        RenderSelectedTaskWorkspace();
    }

    private void TaskWorkspace_Select(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var workspace = _taskWorkspaces.FirstOrDefault(item => item.Id == id);
        if (workspace is not null) OpenTaskWorkspace(workspace);
    }

    private void OpenTaskWorkspace(TaskWorkspace workspace, bool buildImmediately = false)
    {
        if (_taskWorkspaceWindows.TryGetValue(workspace.Id, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = new TaskWorkspaceWindow(workspace, buildImmediately);
        _taskWorkspaceWindows[workspace.Id] = window;
        window.WorkspaceUpdated += updated => DispatcherQueue.TryEnqueue(async () => await RefreshTaskWorkspacesAsync());
        window.Closed += (_, _) => _taskWorkspaceWindows.Remove(workspace.Id);
        window.Activate();
    }

    private async void DeleteTaskWorkspaceCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (_taskWorkspaceWindows.TryGetValue(id, out var window)) window.Close();
        await App.Services.Repository.DeleteTaskWorkspaceAsync(id);
        await RefreshTaskWorkspacesAsync();
    }

    private void RenderSelectedTaskWorkspace()
    {
        TaskDynamicFormHost.Children.Clear();
        var workspace = _selectedTaskWorkspace;
        TaskWorkspaceDetailPanel.Visibility = workspace is null ? Visibility.Collapsed : Visibility.Visible;
        TaskWorkspaceMaximizeButton.IsEnabled = workspace is not null;
        if (!_taskWorkspaceMaximized) TaskWorkspaceMaximizeButton.Content = "EXPAND MINI APP";
        TaskWorkspaceHeader.Text = workspace?.Title.ToUpperInvariant() ?? "SELECT OR CREATE A TASK";
        TaskWorkspaceBoundaryText.Text = workspace is null
            ? "FORM SANDBOX // NO UNDECLARED CONTROLS"
            : $"FORM SANDBOX V{workspace.FormContractVersion} // THREAD {ShortId(workspace.ThreadId ?? "PENDING")} // {workspace.Status.ToUpperInvariant()}";
        DeleteTaskWorkspaceButton.IsEnabled = workspace is not null;
        TaskAiPromptBox.IsEnabled = workspace is not null && !_taskTurnRunning;
        TaskAiSendButton.IsEnabled = workspace is not null && !_taskTurnRunning;
        if (!_taskTurnRunning)
        {
            TaskAiProgressRing.IsActive = false;
            TaskAiStatusText.Text = workspace is null ? "TASK AI IDLE" : $"TASK AI // {workspace.Status.ToUpperInvariant()}";
            TaskAiStatusDetailText.Text = workspace is null
                ? "Create a task or send an instruction to begin."
                : workspace.ThreadId is null ? "Dedicated session has not started yet." : $"Dedicated Codex session {ShortId(workspace.ThreadId)} is online.";
        }
        if (workspace is null) return;
        try
        {
            using var document = JsonDocument.Parse(workspace.FormJson);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message))
                TaskDynamicFormHost.Children.Add(new TextBlock
                {
                    Text = message.GetString() ?? string.Empty, TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromArgb(230, 0, 229, 255)), FontSize = 11
                });
            if (!root.TryGetProperty("controls", out var controls) || controls.ValueKind != JsonValueKind.Array) return;
            foreach (var control in controls.EnumerateArray().Take(30)) RenderTaskControl(control);
        }
        catch (JsonException)
        {
            TaskDynamicFormHost.Children.Add(new TextBlock { Text = "FORM SCHEMA REJECTED", Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 48, 58)) });
        }
    }

    private void RenderTaskControl(JsonElement control)
    {
        var type = control.TryGetProperty("type", out var typeValue) ? typeValue.GetString()?.ToLowerInvariant() : "text";
        var label = control.TryGetProperty("label", out var labelValue) ? labelValue.GetString() ?? string.Empty : string.Empty;
        switch (type)
        {
            case "input":
                TaskDynamicFormHost.Children.Add(new TextBox { Header = label, PlaceholderText = label });
                break;
            case "checkbox":
                TaskDynamicFormHost.Children.Add(new CheckBox { Content = label });
                break;
            case "button":
                var action = control.TryGetProperty("action", out var actionValue) ? actionValue.GetString() : label;
                var button = new Button { Content = label, Tag = action ?? label, HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += TaskDynamicAction_Click;
                TaskDynamicFormHost.Children.Add(button);
                break;
            case "status":
                TaskDynamicFormHost.Children.Add(new Border
                {
                    Padding = new Thickness(10), BorderBrush = new SolidColorBrush(Color.FromArgb(100, 0, 229, 255)),
                    BorderThickness = new Thickness(1), Child = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }
                });
                break;
            default:
                TaskDynamicFormHost.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
                break;
        }
    }

    private async void TaskAiSend_Click(object sender, RoutedEventArgs e) => await RunTaskAiTurnAsync(TaskAiPromptBox.Text.Trim());

    private async void TaskAiPromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await RunTaskAiTurnAsync(TaskAiPromptBox.Text.Trim());
    }

    private async void TaskDynamicAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action }) await RunTaskAiTurnAsync($"The user activated form action: {action}");
    }

    private async Task RunTaskAiTurnAsync(string request)
    {
        var workspace = _selectedTaskWorkspace;
        if (workspace is null || string.IsNullOrWhiteSpace(request) || _taskTurnRunning || _turnRunning) return;
        _taskTurnRunning = true;
        _taskStreamBuffer.Clear();
        TaskAiPromptBox.Text = string.Empty;
        TaskAiPromptBox.IsEnabled = TaskAiSendButton.IsEnabled = false;
        TaskWorkspaceBoundaryText.Text = "TASK AI // REBUILDING ISOLATED FORM";
        SetTaskAiStatus("QUEUED", "Preparing the isolated task workspace...", active: true);
        try
        {
            SetTaskAiStatus("WORKING", "Mini Codex is reading APP.md and executing inside its workspace...", active: true);
            var run = await App.Services.MiniApps.ExecuteAsync(workspace, request);
            workspace = run.Workspace;
            _selectedTaskWorkspace = workspace;
            await RefreshTaskWorkspacesAsync(workspace.Id);
            SetTaskAiStatus("READY", run.Command.ResultSummary ?? "Command completed.", active: false);
        }
        catch (Exception ex)
        {
            TaskWorkspaceBoundaryText.Text = $"TASK AI ERROR // {ex.Message}";
            SetTaskAiStatus("ERROR", ex.Message, active: false, error: true);
        }
        finally
        {
            _taskTurnRunning = false;
            TaskAiPromptBox.IsEnabled = TaskAiSendButton.IsEnabled = _selectedTaskWorkspace is not null;
        }
    }

    private void SetTaskAiStatus(string status, string detail, bool active, bool error = false)
    {
        TaskAiProgressRing.IsActive = active;
        TaskAiStatusText.Text = $"TASK AI // {status}";
        TaskAiStatusText.Foreground = new SolidColorBrush(error
            ? Color.FromArgb(255, 255, 48, 58)
            : Color.FromArgb(255, 0, 229, 255));
        TaskAiStatusDetailText.Text = detail;
    }

    private static string ExtractTaskFormJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException("Task AI did not return a form.");
        return text[start..(end + 1)];
    }

    private static bool HasValidTaskForm(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("controls", out var controls) &&
                   controls.ValueKind == JsonValueKind.Array && controls.GetArrayLength() <= 30;
        }
        catch (JsonException) { return false; }
    }

    private void TaskWorkspaceMaximize_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskWorkspace is null) return;
        if (_taskWorkspaceMaximized)
        {
            _taskWorkspaceMaximized = false;
            _selectedTaskWorkspace = null;
            TaskWorkspaceListPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(TaskWorkspaceDetailPanel, 1);
            Grid.SetColumnSpan(TaskWorkspaceDetailPanel, 1);
            RenderSelectedTaskWorkspace();
            return;
        }
        _taskWorkspaceMaximized = true;
        TaskWorkspaceListPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(TaskWorkspaceDetailPanel, 0);
        Grid.SetColumnSpan(TaskWorkspaceDetailPanel, 2);
        TaskWorkspaceMaximizeButton.Content = "RETURN TO TASKS";
    }

    private async void DeleteTaskWorkspace_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTaskWorkspace is null) return;
        await App.Services.Repository.DeleteTaskWorkspaceAsync(_selectedTaskWorkspace.Id);
        _selectedTaskWorkspace = null;
        await RefreshTaskWorkspacesAsync();
    }

    private async Task RefreshAuditAsync()
    {
        AuditList.Children.Clear();
        foreach (var entry in await App.Services.Repository.GetRecentAuditAsync())
        {
            AuditList.Children.Add(WrapPanel(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = entry.Summary, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"{entry.Timestamp:g} · {entry.Category.ToUpperInvariant()} · {entry.Status.ToUpperInvariant()}", FontSize = 10, Foreground = MutedBrush() }
                }
            }));
        }
        if (AuditList.Children.Count == 0) AuditList.Children.Add(new TextBlock { Text = "No actions recorded yet.", Foreground = MutedBrush() });
    }

    private async void SearchMemory_Click(object sender, RoutedEventArgs e)
    {
        MemoryList.Children.Clear();
        foreach (var memory in await App.Services.Repository.SearchMemoryAsync(MemorySearchBox.Text.Trim()))
        {
            MemoryList.Children.Add(WrapPanel(new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = memory.Category.ToUpperInvariant(), FontSize = 10, Foreground = CyanBrush() },
                    new TextBlock { Text = memory.Content, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,0) }
                }
            }));
        }
        if (MemoryList.Children.Count == 0) MemoryList.Children.Add(new TextBlock { Text = "No matching local memory.", Foreground = MutedBrush() });
    }

    private async void AddMemory_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MemoryContentBox.Text)) return;
        var now = DateTimeOffset.Now;
        await App.Services.Repository.SaveMemoryAsync(new MemoryEntry(Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(MemoryCategoryBox.Text) ? "note" : MemoryCategoryBox.Text.Trim(),
            MemoryContentBox.Text.Trim(), now, now));
        MemoryContentBox.Text = string.Empty;
        MemorySearchBox.Text = string.Empty;
        SearchMemory_Click(sender, e);
    }

    private async void AttachContext_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.Settings;
        if (!settings.ScreenContextEnabled && !settings.ClipboardContextEnabled)
        {
            AddConversationCard("system", "Context access is disabled. Enable active-app or clipboard context in Settings first.");
            return;
        }
        var context = await App.Services.Context.CaptureExplicitContextAsync(settings.ClipboardContextEnabled);
        if (string.IsNullOrWhiteSpace(context))
        {
            AddConversationCard("system", "No permitted context was available.");
            return;
        }
        PromptBox.Text = $"{PromptBox.Text}\n\n[Explicitly attached context]\n{context}".Trim();
    }

    private async void RefreshConnectors_Click(object sender, RoutedEventArgs e)
    {
        ConnectorList.Children.Clear();
        if (!App.Services.Codex.IsConnected)
        {
            ConnectorList.Children.Add(new TextBlock { Text = "Codex is offline.", Foreground = MutedBrush() });
            return;
        }
        var connectors = await App.Services.Codex.ListConnectorsAsync();
        foreach (var connector in connectors)
        {
            ConnectorList.Children.Add(new TextBlock
            {
                Text = $"{connector.Name} · {(connector.IsAuthenticated ? "AUTHORIZED" : connector.Status.ToUpperInvariant())}",
                Foreground = connector.IsAuthenticated ? CyanBrush() : MutedBrush()
            });
        }
        if (ConnectorList.Children.Count == 0)
            ConnectorList.Children.Add(new TextBlock { Text = "No configured MCP connectors were reported.", Foreground = MutedBrush() });
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var selected = PermissionCombo.SelectedTag;
        Enum.TryParse<PermissionLevel>(selected, out var permission);
        var current = App.Services.Settings;
        var settings = current with
        {
            PermissionLevel = permission,
            StartWithWindows = StartupToggle.IsOn,
            ShowAtStartup = StartupWindowToggle.IsOn,
            NotificationsEnabled = NotificationToggle.IsOn,
            VoiceEnabled = VoiceToggle.IsOn,
            ReducedMotion = ReducedMotionToggle.IsOn,
            LowPowerMode = LowPowerToggle.IsOn,
            ScreenContextEnabled = ScreenContextToggle.IsOn,
            ClipboardContextEnabled = ClipboardToggle.IsOn
        };
        await App.Services.SaveSettingsAsync(settings);
        await App.Services.Startup.SetEnabledAsync(settings.StartWithWindows);
        NeuralCore.ReducedMotion = settings.ReducedMotion || settings.LowPowerMode;
        PermissionLabel.Text = settings.PermissionLevel.ToString().ToUpperInvariant();
        App.Services.Notifications.Show("Configuration saved", "DAGNUE safety and experience settings were updated.");
    }

    private void OpenAccessVault_Click(object sender, RoutedEventArgs e)
    {
        if (_accessVaultWindow is null)
        {
            _accessVaultWindow = new AccessVaultWindow();
            _accessVaultWindow.Closed += (_, _) => _accessVaultWindow = null;
        }
        _accessVaultWindow.Activate();
    }

    private void LoadSettings(AssistantSettings settings)
    {
        PermissionLabel.Text = settings.PermissionLevel.ToString().ToUpperInvariant();
        PermissionCombo.SelectedIndex = (int)settings.PermissionLevel;
        StartupToggle.IsOn = settings.StartWithWindows;
        StartupWindowToggle.IsOn = settings.ShowAtStartup;
        NotificationToggle.IsOn = settings.NotificationsEnabled;
        VoiceToggle.IsOn = settings.VoiceEnabled;
        ReducedMotionToggle.IsOn = settings.ReducedMotion;
        LowPowerToggle.IsOn = settings.LowPowerMode;
        ScreenContextToggle.IsOn = settings.ScreenContextEnabled;
        ClipboardToggle.IsOn = settings.ClipboardContextEnabled;
        NeuralCore.ReducedMotion = settings.ReducedMotion || settings.LowPowerMode;
        ConfigureNavigationMotion(settings.ReducedMotion || settings.LowPowerMode);
    }

    private async void Navigation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        SetActiveNavigation((Button)sender);
        OrbitalNavigator.SetActiveRoute(tag);
        NeuralCore.ActiveNavigationIndex = tag switch
        {
            "core" => 0,
            "schedule" => 1,
            "tasks" => 2,
            "activity" => 3,
            "memory" => 4,
            "settings" => 5,
            _ => 0
        };
        await NavigateToRouteAsync(tag);
    }

    private void TaskSurfaceTab_Click(object sender, RoutedEventArgs e)
    {
        var showRemote = sender is Button { Tag: string tag } && tag == "remote";
        CurrentTaskTabContent.Visibility = showRemote ? Visibility.Collapsed : Visibility.Visible;
        RemoteTaskTabContent.Visibility = showRemote ? Visibility.Visible : Visibility.Collapsed;

        CurrentTaskTabButton.Background = new SolidColorBrush(showRemote
            ? Color.FromArgb(0x12, 0x00, 0xE5, 0xFF)
            : Color.FromArgb(0x44, 0x00, 0xE5, 0xFF));
        CurrentTaskTabButton.BorderBrush = new SolidColorBrush(showRemote
            ? Color.FromArgb(0x55, 0x00, 0xE5, 0xFF)
            : Color.FromArgb(0xFF, 0x00, 0xE5, 0xFF));
        RemoteTaskTabButton.Background = new SolidColorBrush(showRemote
            ? Color.FromArgb(0x44, 0x00, 0xE5, 0xFF)
            : Color.FromArgb(0x12, 0x00, 0xE5, 0xFF));
        RemoteTaskTabButton.BorderBrush = new SolidColorBrush(showRemote
            ? Color.FromArgb(0xFF, 0x00, 0xE5, 0xFF)
            : Color.FromArgb(0x55, 0x00, 0xE5, 0xFF));

        if (showRemote) _ = RefreshRemoteTaskDashboardAsync(force: true);
    }

    private async void RefreshRemoteTask_Click(object sender, RoutedEventArgs e) =>
        await RefreshRemoteTaskDashboardAsync(force: true);

    private async void OpenRemoteTaskBrowser_Click(object sender, RoutedEventArgs e) =>
        await Launcher.LaunchUriAsync(RemoteTaskDeskUri);

    private async Task RefreshRemoteTaskDashboardAsync(bool force = false)
    {
        if (_remoteDashboardRefreshing || !App.Services.IsInitialized) return;
        _remoteDashboardRefreshing = true;
        RemoteTaskSyncText.Text = "SYNCING // SECURE CHANNEL";
        try
        {
            var tasks = await App.Services.RemoteTasks.GetDashboardTasksAsync();
            var signature = string.Join('|', tasks.Select(task =>
                $"{task.Id}:{task.Status}:{task.UpdatedAt}:{task.Progress?.ProgressPercent}:{task.Messages?.Count}:{task.Files?.Count}"));
            RemoteTaskSyncText.Text = $"LIVE // {DateTimeOffset.Now:HH:mm:ss}";
            _remoteDashboardTasks = tasks;
            UpdateRemoteDashboardMetrics(serverOnline: true);
            if (!force && signature == _remoteDashboardSignature) return;

            _remoteDashboardSignature = signature;
            if (_selectedRemoteTaskId is null || tasks.All(task => task.Id != _selectedRemoteTaskId))
                _selectedRemoteTaskId = tasks.FirstOrDefault()?.Id;
            RenderRemoteTaskList();
            RenderSelectedRemoteTask();
        }
        catch (Exception ex)
        {
            RemoteTaskSyncText.Text = "SYNC INTERRUPTED";
            RemoteTaskProgressText.Text = $"The native task channel could not refresh: {ex.Message}";
            UpdateRemoteDashboardMetrics(serverOnline: false);
        }
        finally { _remoteDashboardRefreshing = false; }
    }

    private void RenderRemoteTaskList()
    {
        RemoteTaskList.Children.Clear();
        if (_remoteDashboardTasks.Count == 0)
        {
            RemoteTaskList.Children.Add(new TextBlock
            {
                Text = "NO ONLINE TASK SIGNALS // Tasks sent from the web will appear here automatically.",
                Margin = new Thickness(8), FontSize = 9, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 106, 132, 143))
            });
            return;
        }

        foreach (var task in _remoteDashboardTasks)
        {
            var selected = task.Id == _selectedRemoteTaskId;
            var button = new Button
            {
                Tag = task.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(selected
                    ? Color.FromArgb(215, 0, 57, 70) : Color.FromArgb(175, 4, 20, 28)),
                BorderBrush = new SolidColorBrush(selected
                    ? Color.FromArgb(255, 0, 229, 255) : Color.FromArgb(90, 0, 229, 255)),
                BorderThickness = new Thickness(1), Padding = new Thickness(10, 8, 10, 8)
            };
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labels = new StackPanel { Spacing = 3 };
            labels.Children.Add(new TextBlock
            {
                Text = task.Title, FontSize = 9, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 252, 255))
            });
            if (task.Tags is { Count: > 0 }) labels.Children.Add(new TextBlock
            {
                Text = string.Join("  //  ", task.Tags.Select(tag => tag.Replace('_', ' '))), FontSize = 6,
                CharacterSpacing = 45, Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 229, 255))
            });
            labels.Children.Add(new TextBlock
            {
                Text = task.Progress?.Phase?.ToUpperInvariant() ?? task.Status.Replace('_', ' ').ToUpperInvariant(),
                FontSize = 7, Foreground = new SolidColorBrush(Color.FromArgb(255, 106, 170, 181))
            });
            content.Children.Add(labels);
            var percent = new TextBlock
            {
                Text = task.Progress is null ? "" : $"{task.Progress.ProgressPercent}%",
                FontSize = 8, Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 229, 255)),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(percent, 1); content.Children.Add(percent);
            button.Content = content;
            button.Click += RemoteTaskListItem_Click;
            RemoteTaskList.Children.Add(button);
        }
    }

    private void UpdateRemoteDashboardMetrics(bool serverOnline)
    {
        var doing = _remoteDashboardTasks.Count(task => task.Status == "running")
                    + (_turnRunning || _taskTurnRunning ? 1 : 0);
        var remoteNotDone = _remoteDashboardTasks.Count(task => task.Status is not ("completed" or "cancelled"));
        var remoteDone = _remoteDashboardTasks.Count(task => task.Status == "completed");
        CoreDashboardActivityRing.IsActive = doing > 0;
        CoreDashboardSignalText.Text = !serverOnline ? "OFFLINE" : doing > 0 ? "EXECUTING" : "ONLINE";
        CoreDashboardSignalText.Foreground = new SolidColorBrush(!serverOnline
            ? Color.FromArgb(255, 255, 112, 104)
            : doing > 0 ? Color.FromArgb(255, 0, 229, 255) : Color.FromArgb(255, 88, 225, 156));
        CoreDashboardLocalCount.Text = (_userTaskCount + _completedLocalTaskCount).ToString();
        CoreDashboardOnlineCount.Text = _remoteDashboardTasks.Count.ToString();
        CoreDashboardDoingCount.Text = doing.ToString();
        CoreDashboardNotDoneCount.Text = (_userTaskCount + remoteNotDone).ToString();
        CoreDashboardDoneCount.Text = (_completedLocalTaskCount + remoteDone).ToString();
        CoreDashboardCollapsedCount.Text = (doing > 0 ? doing : _userTaskCount + remoteNotDone).ToString();
        CoreDashboardCollapsedState.Text = doing > 0 ? "DOING" : "OPEN";
        CoreDashboardCollapsedLocal.Text = (_userTaskCount + _completedLocalTaskCount).ToString("00");
        CoreDashboardCollapsedRemote.Text = _remoteDashboardTasks.Count.ToString("00");

        var focusTask = _remoteDashboardTasks.FirstOrDefault(task => task.Status == "running")
                        ?? _remoteDashboardTasks.FirstOrDefault(task => task.Status is not ("completed" or "cancelled"));
        if (focusTask is not null)
        {
            CoreDashboardTaskTitle.Text = focusTask.Title.ToUpperInvariant();
            CoreDashboardTaskState.Text = focusTask.Progress is null
                ? focusTask.Status.Replace('_', ' ').ToUpperInvariant()
                : $"{focusTask.Progress.Phase.ToUpperInvariant()} // {focusTask.Progress.Detail}";
            CoreDashboardProgressBar.Value = focusTask.Progress?.ProgressPercent ?? 0;
            CoreDashboardCollapsedPercent.Text = $"{Math.Clamp(focusTask.Progress?.ProgressPercent ?? 0, 0, 100):00}%";
            CoreDashboardEtaText.Text = focusTask.Progress?.EtaSeconds is > 0
                ? $"ABOUT {Math.Max(1, (int)Math.Ceiling(focusTask.Progress.EtaSeconds.Value / 60d))} MIN LEFT"
                : focusTask.Status.Replace('_', ' ').ToUpperInvariant();
        }
        else if (_turnRunning || _taskTurnRunning)
        {
            CoreDashboardTaskTitle.Text = "LOCAL AI TASK IN PROGRESS";
            CoreDashboardTaskState.Text = "DAGNUE is executing a task on this computer.";
            CoreDashboardProgressBar.Value = 50;
            CoreDashboardCollapsedPercent.Text = "50%";
            CoreDashboardEtaText.Text = "EXECUTING";
        }
        else
        {
            CoreDashboardTaskTitle.Text = "NO ACTIVE TASK";
            CoreDashboardTaskState.Text = serverOnline
                ? "Local and online task channels are synchronized."
                : "Online task channel is temporarily unavailable.";
            CoreDashboardProgressBar.Value = 0;
            CoreDashboardCollapsedPercent.Text = serverOnline ? "SYNC" : "OFF";
            CoreDashboardEtaText.Text = remoteDone > 0 ? $"{remoteDone} ONLINE COMPLETED" : "IDLE";
        }
    }

    private void RemoteTaskListItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        _selectedRemoteTaskId = id;
        RenderRemoteTaskList();
        RenderSelectedRemoteTask();
    }

    private void RenderSelectedRemoteTask()
    {
        var task = _remoteDashboardTasks.FirstOrDefault(item => item.Id == _selectedRemoteTaskId);
        RemoteTaskConversation.Children.Clear();
        if (task is null)
        {
            RemoteTaskDetailTitle.Text = "SELECT AN ONLINE TASK";
            RemoteTaskDetailStatus.Text = "The server conversation will appear here.";
            RemoteTaskProgressBar.Value = 0; RemoteTaskProgressText.Text = "WAITING FOR TASK SIGNAL";
            RemoteTaskEtaText.Text = string.Empty; RemoteTaskReplyBox.IsEnabled = false;
            RemoteTaskReplyButton.IsEnabled = false; RemoteTaskApproveButton.Visibility = Visibility.Collapsed;
            RemoteTaskRetryButton.Visibility = Visibility.Collapsed; RemoteTaskCompleteButton.Visibility = Visibility.Collapsed;
            return;
        }

        RemoteTaskDetailTitle.Text = task.Title.ToUpperInvariant();
        RemoteTaskDetailStatus.Text = $"{task.Status.Replace('_', ' ').ToUpperInvariant()} // {task.Priority.ToUpperInvariant()} PRIORITY" +
                                      (task.Tags is { Count: > 0 } ? $" // ROUTE {string.Join(" + ", task.Tags.Select(tag => tag.Replace('_', ' ')))}" : string.Empty);
        RemoteTaskProgressBar.Value = task.Progress?.ProgressPercent ?? 0;
        RemoteTaskProgressText.Text = task.Progress is null
            ? task.Status.Replace('_', ' ').ToUpperInvariant()
            : $"{task.Progress.Phase.ToUpperInvariant()} // {task.Progress.Detail}";
        RemoteTaskEtaText.Text = task.Progress?.EtaSeconds is > 0
            ? $"ABOUT {Math.Max(1, (int)Math.Ceiling(task.Progress.EtaSeconds.Value / 60d))} MIN LEFT" : string.Empty;

        foreach (var message in task.Messages ?? []) AddRemoteConversationCard(message.Role, message.Kind, message.Content);
        if (!string.IsNullOrWhiteSpace(task.ResultSummary)) AddRemoteConversationCard("assistant", "result", task.ResultSummary);
        if (!string.IsNullOrWhiteSpace(task.ErrorMessage)) AddRemoteConversationCard("system", "error", task.ErrorMessage);
        foreach (var file in task.Files?.Where(file => file.Kind == "result") ?? [])
            AddRemoteConversationCard("assistant", "file", $"RESULT FILE // {file.Filename} // {FormatBytes(file.Size)}");

        var canReply = task.Status is "awaiting_input" or "awaiting_review" or "completed" or "failed";
        RemoteTaskReplyBox.IsEnabled = canReply; RemoteTaskReplyButton.IsEnabled = canReply;
        RemoteTaskApproveButton.Visibility = task.Status == "approval_required" ? Visibility.Visible : Visibility.Collapsed;
        RemoteTaskRetryButton.Visibility = task.Status is "failed" or "cancelled" ? Visibility.Visible : Visibility.Collapsed;
        RemoteTaskCompleteButton.Visibility = task.Status == "awaiting_review" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddRemoteConversationCard(string role, string kind, string content)
    {
        var user = role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8), MaxWidth = 560,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = new SolidColorBrush(user ? Color.FromArgb(220, 0, 55, 66) : Color.FromArgb(205, 5, 21, 29)),
            BorderBrush = new SolidColorBrush(user ? Color.FromArgb(210, 0, 229, 255) : Color.FromArgb(100, 0, 229, 255)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2)
        };
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = user ? "YOU" : kind.ToUpperInvariant() is "QUESTION" or "SUGGESTION" ? $"DAGNUE AI // {kind.ToUpperInvariant()}" : "DAGNUE AI",
            FontSize = 7, CharacterSpacing = 80, Foreground = new SolidColorBrush(Color.FromArgb(255, 0, 229, 255))
        });
        stack.Children.Add(new TextBlock
        {
            Text = content, FontSize = 9, TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 226, 242, 245))
        });
        card.Child = stack; RemoteTaskConversation.Children.Add(card);
    }

    private async void RemoteTaskReply_Click(object sender, RoutedEventArgs e)
    {
        var message = RemoteTaskReplyBox.Text.Trim();
        if (_selectedRemoteTaskId is null || string.IsNullOrWhiteSpace(message)) return;
        RemoteTaskReplyButton.IsEnabled = false;
        try
        {
            await App.Services.RemoteTasks.SendDashboardActionAsync(_selectedRemoteTaskId, "reply", message);
            RemoteTaskReplyBox.Text = string.Empty;
            await RefreshRemoteTaskDashboardAsync(force: true);
        }
        catch (Exception ex) { RemoteTaskProgressText.Text = $"REPLY FAILED // {ex.Message}"; }
        finally { RemoteTaskReplyButton.IsEnabled = true; }
    }

    private async void RemoteTaskApprove_Click(object sender, RoutedEventArgs e) => await RunRemoteTaskActionAsync("approve");
    private async void RemoteTaskRetry_Click(object sender, RoutedEventArgs e) => await RunRemoteTaskActionAsync("retry");
    private async void RemoteTaskComplete_Click(object sender, RoutedEventArgs e) => await RunRemoteTaskActionAsync("complete");

    private async Task RunRemoteTaskActionAsync(string action)
    {
        if (_selectedRemoteTaskId is null) return;
        try
        {
            await App.Services.RemoteTasks.SendDashboardActionAsync(_selectedRemoteTaskId, action);
            await RefreshRemoteTaskDashboardAsync(force: true);
        }
        catch (Exception ex) { RemoteTaskProgressText.Text = $"ACTION FAILED // {ex.Message}"; }
    }

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024d):0.0} KB";

    private async void OrbitalNavigation_NavigationRequested(object? sender, Controls.OrbitalNavigationRequestedEventArgs e) =>
        await NavigateToRouteAsync(e.Route);

    private async Task NavigateToRouteAsync(string tag)
    {
        if (tag == "find")
        {
            if (_findWindow is null)
            {
                _findWindow = new FindWindow();
                _findWindow.Closed += (_, _) => _findWindow = null;
            }
            _findWindow.Activate();
            OrbitalNavigator.SetActiveRoute("core");
            return;
        }
        if (tag == "apps")
        {
            if (_appsWindow is null)
            {
                _appsWindow = new AppsWindow();
                _appsWindow.Closed += (_, _) => _appsWindow = null;
            }
            _appsWindow.Activate();
            OrbitalNavigator.SetActiveRoute("core");
            return;
        }
        if (tag == "tasks")
        {
            _selectedTaskWorkspace = null;
            _taskWorkspaceMaximized = false;
            TaskWorkspaceListPanel.Visibility = Visibility.Visible;
            Grid.SetColumn(TaskWorkspaceDetailPanel, 1);
            Grid.SetColumnSpan(TaskWorkspaceDetailPanel, 1);
            TaskWorkspaceDetailPanel.Visibility = Visibility.Collapsed;
        }
        CorePanel.Visibility = Visibility.Visible;
        SchedulePanel.Visibility = tag == "schedule" ? Visibility.Visible : Visibility.Collapsed;
        TasksPanel.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPanel.Visibility = tag == "activity" ? Visibility.Visible : Visibility.Collapsed;
        MemoryPanel.Visibility = tag == "memory" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ScheduleChrome.SetAnimationEnabled(tag == "schedule");
        TasksChrome.SetAnimationEnabled(tag == "tasks");
        ActivityChrome.SetAnimationEnabled(tag == "activity");
        MemoryChrome.SetAnimationEnabled(tag == "memory");
        SettingsChrome.SetAnimationEnabled(tag == "settings");
        HudReturnButton.Visibility = tag == "core" ? Visibility.Collapsed : Visibility.Visible;
        OrbitalNavigator.SetActiveRoute(tag);
        FrameworkElement? selectedSheet = tag switch
        {
            "schedule" => SchedulePanel,
            "tasks" => TasksPanel,
            "activity" => ActivityPanel,
            "memory" => MemoryPanel,
            "settings" => SettingsPanel,
            _ => null
        };
        _activeSheet = selectedSheet;
        if (selectedSheet is not null)
        {
            selectedSheet.UpdateLayout();
            PositionReturnButton();
            AnimateHudSheet(selectedSheet);
        }
        if (tag == "schedule") await RefreshRemindersAsync();
        if (tag == "tasks") await RefreshTaskWorkspacesAsync();
        if (tag == "activity") await RefreshAuditAsync();
    }

    private void PositionReturnButton()
    {
        if (_activeSheet is null || _activeSheet.Visibility != Visibility.Visible ||
            _activeSheet.ActualWidth <= 0 || _activeSheet.ActualHeight <= 0)
        {
            return;
        }

        var topLeft = _activeSheet.TransformToVisual(WorkspaceGrid).TransformPoint(new Point(0, 0));
        HudReturnButton.Margin = new Thickness(
            topLeft.X + 13 - WorkspaceGrid.Padding.Left,
            topLeft.Y + 10 - WorkspaceGrid.Padding.Top,
            0,
            0);
    }

    private static void AnimateHudSheet(FrameworkElement sheet)
    {
        sheet.Opacity = 0;
        sheet.RenderTransformOrigin = new Point(0.5, 0.5);
        var transform = new CompositeTransform { ScaleX = 0.965, ScaleY = 0.965, TranslateY = 18 };
        sheet.RenderTransform = transform;
        var storyboard = new Storyboard();
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 0.97,
            Duration = new Duration(TimeSpan.FromMilliseconds(330)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, sheet);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);
        var rise = new DoubleAnimation
        {
            From = 18,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(390)),
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "TranslateY");
        storyboard.Children.Add(rise);
        foreach (var property in new[] { "ScaleX", "ScaleY" })
        {
            var scale = new DoubleAnimation
            {
                From = 0.965,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(390)),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(scale, transform);
            Storyboard.SetTargetProperty(scale, property);
            storyboard.Children.Add(scale);
        }
        storyboard.Begin();
    }

    private void ChannelToggle_Click(object sender, RoutedEventArgs e)
    {
        _channelExpanded = !_channelExpanded;
        AnimateChannelDock(_channelExpanded);
        if (_channelExpanded)
        {
            DispatcherQueue.TryEnqueue(() => PromptBox.Focus(FocusState.Programmatic));
        }
        else
        {
            FocusCoreSurface();
        }
    }

    private void ChannelDock_LostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (focused is not null && IsWithinElement(focused, ChannelDock)) return;
            if (!_channelExpanded) return;
            _channelExpanded = false;
            AnimateChannelDock(false);
        });
    }

    private static bool IsWithinElement(DependencyObject candidate, DependencyObject ancestor)
    {
        for (var current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private void AnimateChannelDock(bool expanded)
    {
        var currentPanelY = ChannelPanelTransform.TranslateY;
        var currentPanelScaleX = ChannelPanelTransform.ScaleX;
        var currentPanelScaleY = ChannelPanelTransform.ScaleY;
        var currentPanelOpacity = ChannelPanel.Opacity;
        var currentContentY = ChannelContentTransform.TranslateY;
        var currentContentOpacity = ChannelContent.Opacity;
        var currentIconRotation = ChannelTabIconTransform.Rotation;
        _channelMotion?.Stop();
        ChannelPanel.IsHitTestVisible = expanded;
        // Yield render time to the navigation control while the dock is moving.
        // The toggle remains enabled so a second click can reverse immediately.
        MeshBrain.SetAnimationPaused(true);

        var duration = expanded ? 260 : 170;
        var contentDuration = expanded ? 210 : 105;
        var contentDelay = expanded ? 35 : 0;
        var storyboard = new Storyboard();

        void AddMorph(DependencyObject target, string property, double from, double to,
            int milliseconds, int delay = 0)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = expanded
                    ? new QuinticEase { EasingMode = EasingMode.EaseOut }
                    : new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        AddMorph(ChannelPanelTransform, "TranslateY", currentPanelY, expanded ? 0 : 44, duration);
        AddMorph(ChannelPanelTransform, "ScaleX", currentPanelScaleX, expanded ? 1 : 0.94, duration);
        AddMorph(ChannelPanelTransform, "ScaleY", currentPanelScaleY, expanded ? 1 : 0.88, duration);
        AddMorph(ChannelPanel, "Opacity", currentPanelOpacity, expanded ? 1 : 0, duration);
        AddMorph(ChannelContentTransform, "TranslateY", currentContentY, expanded ? 0 : 28,
            contentDuration, contentDelay);
        AddMorph(ChannelContent, "Opacity", currentContentOpacity, expanded ? 1 : 0,
            contentDuration, contentDelay);
        AddMorph(ChannelTabIconTransform, "Rotation", currentIconRotation, expanded ? 180 : 0, duration);

        storyboard.Completed += (_, _) =>
        {
            ApplyChannelDockState(expanded);
            MeshBrain.SetAnimationPaused(_windowInteractionActive || !_meshModeActive);
            _channelMotion = null;
        };
        _channelMotion = storyboard;
        storyboard.Begin();
    }

    private void ApplyChannelDockState(bool expanded)
    {
        ChannelDockTransform.TranslateX = 0;
        ChannelPanelTransform.TranslateY = expanded ? 0 : 44;
        ChannelPanelTransform.ScaleX = expanded ? 1 : 0.94;
        ChannelPanelTransform.ScaleY = expanded ? 1 : 0.88;
        ChannelPanel.Opacity = expanded ? 1 : 0;
        ChannelContentTransform.TranslateX = 0;
        ChannelContentTransform.TranslateY = expanded ? 0 : 28;
        ChannelContent.Opacity = expanded ? 1 : 0;
        ChannelTabIconTransform.Rotation = expanded ? 180 : 0;
        ChannelPanel.IsHitTestVisible = expanded;

        var action = expanded ? "Collapse Codex channel" : "Open Codex channel";
        AutomationProperties.SetName(ChannelToggleButton, action);
        ToolTipService.SetToolTip(ChannelToggleButton, action);
    }

    private void ModeTabDock_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _modeDockCollapseTimer.Stop();
        AnimateModeDock(true);
    }

    private void ModeDockReveal_Click(object sender, RoutedEventArgs e)
    {
        _modeDockCollapseTimer.Stop();
        AnimateModeDock(true);
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var workspacePoint = e.GetCurrentPoint(WorkspaceGrid).Position;
        var overCollapsedSensor = workspacePoint.X is >= 0 and <= 68 &&
                                  workspacePoint.Y is >= 28 and <= 158;
        var dockPoint = e.GetCurrentPoint(ModeTabDock).Position;
        var overExpandedDock = _modeDockExpanded &&
                               dockPoint.X is >= 0 and <= 410 && dockPoint.Y is >= 0 and <= 104;

        if (overCollapsedSensor || overExpandedDock)
        {
            _modeDockCollapseTimer.Stop();
            AnimateModeDock(true);
        }
        else if (_modeDockExpanded && !_modeDockCollapseTimer.IsEnabled)
        {
            _modeDockCollapseTimer.Start();
        }
    }

    private void ModeTabDock_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _modeDockCollapseTimer.Stop();
        _modeDockCollapseTimer.Start();
    }

    private void AnimateModeDock(bool expanded)
    {
        if (_modeDockExpanded == expanded) return;
        _modeDockExpanded = expanded;
        _modeDockMotion?.Stop();
        ModeTabElements.IsHitTestVisible = expanded;

        var duration = expanded ? 640 : 470;
        var contentDuration = expanded ? 430 : 230;
        var contentDelay = expanded ? 130 : 0;
        var storyboard = new Storyboard();

        void AddMotion(DependencyObject target, string property, double from, double to,
            int milliseconds, int delay = 0)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = expanded
                    ? new QuinticEase { EasingMode = EasingMode.EaseOut }
                    : new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }

        AddMotion(ModeTabDockTransform, "TranslateX", expanded ? -360 : 0, expanded ? 0 : -360, duration);
        AddMotion(ModeTabElementsTransform, "TranslateX", expanded ? -24 : 0, expanded ? 0 : -24,
            contentDuration, contentDelay);
        AddMotion(ModeTabElements, "Opacity", expanded ? 0 : 1, expanded ? 1 : 0,
            contentDuration, contentDelay);
        AddMotion(ModeRailScan, "Opacity", expanded ? 0.18 : 0.82, expanded ? 0.82 : 0.18,
            contentDuration, contentDelay);

        storyboard.Completed += (_, _) =>
        {
            ApplyModeDockState(expanded);
            _modeDockMotion = null;
        };
        _modeDockMotion = storyboard;
        storyboard.Begin();
    }

    private void ApplyModeDockState(bool expanded)
    {
        _modeDockExpanded = expanded;
        ModeTabDockTransform.TranslateX = expanded ? 0 : -360;
        ModeTabElementsTransform.TranslateX = expanded ? 0 : -24;
        ModeTabElements.Opacity = expanded ? 1 : 0;
        ModeTabElements.IsHitTestVisible = expanded;
        ModeRailScan.Opacity = expanded ? 0.82 : 0.18;
    }

    private void CoreModeTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode }) return;
        var showMesh = string.Equals(mode, "mesh", StringComparison.OrdinalIgnoreCase);
        if (showMesh == _meshModeActive) return;
        AnimateCoreMode(showMesh);
    }

    private void AnimateCoreMode(bool showMesh)
    {
        _coreModeMotion?.Stop();
        var outgoing = showMesh ? RadialStatusView : MeshBrainView;
        var incoming = showMesh ? MeshBrainView : RadialStatusView;
        var outgoingTransform = showMesh ? RadialStatusTransform : MeshBrainTransform;
        var incomingTransform = showMesh ? MeshBrainTransform : RadialStatusTransform;

        _meshModeActive = showMesh;
        incoming.Visibility = Visibility.Visible;
        incoming.IsHitTestVisible = true;
        outgoing.IsHitTestVisible = false;
        incoming.Opacity = 0;
        incomingTransform.TranslateX = 270;
        incomingTransform.ScaleX = incomingTransform.ScaleY = 0.965;
        outgoing.Opacity = 1;
        outgoingTransform.TranslateX = 0;
        outgoingTransform.ScaleX = outgoingTransform.ScaleY = 1;
        SetCoreModeSelection(showMesh);

        NeuralCore.SetAnimationPaused(_windowInteractionActive || showMesh);
        OrbitalNavigator.SetAnimationPaused(_windowInteractionActive || showMesh);
        MeshBrain.SetAnimationPaused(_windowInteractionActive || !showMesh);

        var storyboard = new Storyboard();

        static void AddFly(Storyboard motion, DependencyObject target, string property, double from, double to,
            int milliseconds, int delay, EasingFunctionBase easing)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                BeginTime = TimeSpan.FromMilliseconds(delay),
                EasingFunction = easing
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            motion.Children.Add(animation);
        }

        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var enterEase = new QuinticEase { EasingMode = EasingMode.EaseOut };
        AddFly(storyboard, outgoingTransform, "TranslateX", 0, -290, 185, 0, exitEase);
        AddFly(storyboard, outgoingTransform, "ScaleX", 1, 0.95, 185, 0, exitEase);
        AddFly(storyboard, outgoingTransform, "ScaleY", 1, 0.95, 185, 0, exitEase);
        AddFly(storyboard, outgoing, "Opacity", 1, 0, 150, 0, exitEase);
        AddFly(storyboard, incomingTransform, "TranslateX", 270, 0, 265, 58, enterEase);
        AddFly(storyboard, incomingTransform, "ScaleX", 0.965, 1, 265, 58, enterEase);
        AddFly(storyboard, incomingTransform, "ScaleY", 0.965, 1, 265, 58, enterEase);
        AddFly(storyboard, incoming, "Opacity", 0, 1, 220, 72, enterEase);

        storyboard.Completed += (_, _) =>
        {
            ApplyCoreModeState(showMesh);
            _coreModeMotion = null;
        };
        _coreModeMotion = storyboard;
        storyboard.Begin();
    }

    private void ApplyCoreModeState(bool showMesh)
    {
        _meshModeActive = showMesh;
        RadialStatusView.Visibility = showMesh ? Visibility.Collapsed : Visibility.Visible;
        RadialStatusView.Opacity = showMesh ? 0 : 1;
        RadialStatusView.IsHitTestVisible = !showMesh;
        RadialStatusTransform.TranslateX = 0;
        RadialStatusTransform.TranslateY = 0;
        RadialStatusTransform.ScaleX = RadialStatusTransform.ScaleY = 1;

        MeshBrainView.Visibility = showMesh ? Visibility.Visible : Visibility.Collapsed;
        MeshBrainView.Opacity = showMesh ? 1 : 0;
        MeshBrainView.IsHitTestVisible = showMesh;
        MeshBrainTransform.TranslateX = 0;
        MeshBrainTransform.ScaleX = MeshBrainTransform.ScaleY = 1;

        NeuralCore.SetAnimationPaused(_windowInteractionActive || showMesh);
        OrbitalNavigator.SetAnimationPaused(_windowInteractionActive || showMesh);
        MeshBrain.SetAnimationPaused(_windowInteractionActive || !showMesh);
        SetCoreModeSelection(showMesh);
    }

    private void SetCoreModeSelection(bool showMesh)
    {
        RadialModeButton.Background = new SolidColorBrush(showMesh
            ? Color.FromArgb(185, 10, 23, 31)
            : Color.FromArgb(116, 0, 190, 205));
        MeshModeButton.Background = new SolidColorBrush(showMesh
            ? Color.FromArgb(116, 0, 190, 205)
            : Color.FromArgb(185, 10, 23, 31));
        RadialModeButton.Foreground = new SolidColorBrush(showMesh
            ? Color.FromArgb(218, 205, 230, 236)
            : Color.FromArgb(255, 246, 253, 255));
        MeshModeButton.Foreground = new SolidColorBrush(showMesh
            ? Color.FromArgb(255, 246, 253, 255)
            : Color.FromArgb(218, 205, 230, 236));
    }

    private void SetBrainState(BrainState state)
    {
        NeuralCore.State = state;
        MeshBrain.State = state;
    }

    private void SetActiveNavigation(Button activeButton)
    {
        foreach (var button in _navigationButtons)
        {
            var selected = ReferenceEquals(button, activeButton);
            button.Background = new SolidColorBrush(selected
                ? Color.FromArgb(86, 0, 185, 207)
                : Color.FromArgb(51, 18, 29, 39));
            button.BorderBrush = new SolidColorBrush(selected
                ? Color.FromArgb(164, 0, 229, 255)
                : Color.FromArgb(34, 25, 207, 226));
            button.Opacity = selected ? 1 : 0.78;
        }
    }

    private void ConfigureNavigationMotion(bool reducedMotion)
    {
        _navigationMotion?.Stop();
        _navigationMotion = null;

        NavScanTransform.Y = reducedMotion ? 180 : 0;
        NavScanLine.Opacity = reducedMotion ? 0.22 : 0.7;
        RailNodeTop.Opacity = reducedMotion ? 0.5 : 0.3;
        RailNodeMiddle.Opacity = reducedMotion ? 0.4 : 0.2;
        RailNodeBottom.Opacity = reducedMotion ? 0.5 : 0.25;
        BriefingSweepTransform.X = reducedMotion ? 280 : 0;
        BriefingSweep.Opacity = reducedMotion ? 0.28 : 0.9;
        BriefingPulse.Opacity = reducedMotion ? 0.55 : 0.25;
        TelemetryStrip.Opacity = 1;
        LiveStatusStrip.Opacity = 1;
        ChannelSignal.Opacity = 1;
        if (reducedMotion || !IsLoaded) return;

        var motion = new Storyboard();
        var scan = new DoubleAnimation
        {
            From = 0,
            To = 690,
            Duration = new Duration(TimeSpan.FromSeconds(7.5)),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(scan, NavScanTransform);
        Storyboard.SetTargetProperty(scan, "Y");
        motion.Children.Add(scan);

        AddNodePulse(motion, RailNodeTop, 0);
        AddNodePulse(motion, RailNodeMiddle, 0.8);
        AddNodePulse(motion, RailNodeBottom, 1.6);
        AddNodePulse(motion, BriefingPulse, 0.35);
        AddTravelTrace(motion, BriefingSweepTransform, 600, 5.2);
        AddAmbientPulse(motion, TelemetryStrip, 0.74, 3.4, 0);
        AddAmbientPulse(motion, LiveStatusStrip, 0.68, 3.8, 0.9);
        AddAmbientPulse(motion, ChannelSignal, 0.72, 3.1, 1.45);
        _navigationMotion = motion;
        motion.Begin();
    }

    private static void AddTravelTrace(Storyboard storyboard, TranslateTransform transform, double distance, double seconds)
    {
        var trace = new DoubleAnimation
        {
            From = 0,
            To = distance,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(trace, transform);
        Storyboard.SetTargetProperty(trace, "X");
        storyboard.Children.Add(trace);
    }

    private static void AddAmbientPulse(Storyboard storyboard, UIElement element, double minimumOpacity, double seconds, double delaySeconds)
    {
        var pulse = new DoubleAnimation
        {
            From = minimumOpacity,
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, element);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        storyboard.Children.Add(pulse);
    }

    private static void AddNodePulse(Storyboard storyboard, UIElement node, double delaySeconds)
    {
        var pulse = new DoubleAnimation
        {
            From = 0.18,
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(1.35)),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, node);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        storyboard.Children.Add(pulse);
    }

    private void Notifications_NotificationShown(int count) => DispatcherQueue.TryEnqueue(() =>
        RefreshNotificationCarousel(selectNewest: true));

    private void RefreshNotificationCarousel(bool selectNewest = false)
    {
        var previousKey = _notificationItems.Count > 0 && _notificationIndex >= 0 && _notificationIndex < _notificationItems.Count
            ? _notificationItems[_notificationIndex].Key
            : null;
        _notificationItems.Clear();
        foreach (var item in App.Services.Notifications.History.OrderByDescending(item => item.Sequence))
            _notificationItems.Add(new AdvisoryNotification($"system:{item.Sequence}", item.Title, item.Body, item.Timestamp));
        _notificationItems.Add(new AdvisoryNotification("static:codex-link",
            "Codex neural link synchronized.",
            "Analysis channel is online. Local approvals and command routing are ready.",
            DateTimeOffset.Now.AddSeconds(-1)));
        _notificationItems.Add(new AdvisoryNotification("static:system-matrix",
            "System matrix stable.",
            "Telemetry, filesystem cognition, schedules, and background sentinels are operational.",
            DateTimeOffset.Now.AddSeconds(-2)));
        _notificationItems.Add(new AdvisoryNotification("briefing", _briefingNotificationTitle,
            _briefingNotificationBody, DateTimeOffset.Now));

        _notificationIndex = selectNewest
            ? 0
            : previousKey is null ? Math.Clamp(_notificationIndex, 0, _notificationItems.Count - 1)
            : Math.Max(0, _notificationItems.FindIndex(item => item.Key == previousKey));
        UpdateNotificationSequence(_notificationItems.Count);
        ApplyCurrentNotification();
        ApplyNotificationDepthStack();
    }

    private void ApplyCurrentNotification()
    {
        if (_notificationItems.Count == 0) return;
        _notificationIndex = Math.Clamp(_notificationIndex, 0, _notificationItems.Count - 1);
        var item = _notificationItems[_notificationIndex];
        BriefingGreeting.Text = item.Title;
        BriefingSummary.Text = item.Body;
        NoticeRomanText.Text = ToRomanNumeral(_notificationIndex + 1);
        NotificationPositionText.Text = $"{ToRomanNumeral(_notificationIndex + 1)} / {ToRomanNumeral(_notificationItems.Count)}";
        AutomationProperties.SetName(NoticeRomanText,
            $"Current notification {_notificationIndex + 1} of {_notificationItems.Count}");
        AutomationProperties.SetName(NotificationMessageLayer,
            $"Notification {_notificationIndex + 1} of {_notificationItems.Count}. {item.Title}. {item.Body}");
    }

    private void NotificationDock_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_notificationCollapsed || _notificationTransitioning || _notificationItems.Count < 2) return;
        var delta = e.GetCurrentPoint(NotificationDock).Properties.MouseWheelDelta;
        var step = delta < 0 ? 1 : -1;
        var nextIndex = (_notificationIndex + step + _notificationItems.Count) % _notificationItems.Count;
        AnimateNotificationScroll(nextIndex, delta < 0 ? -1 : 1);
        e.Handled = true;
    }

    private void AnimateNotificationScroll(int nextIndex, int direction)
    {
        _notificationScrollMotion?.Stop();
        _notificationTransitioning = true;
        var outgoing = new Storyboard();
        AddNotificationMotion(outgoing, NotificationExpandedLayer, "Opacity", 1, 0.16, 260,
            new CubicEase { EasingMode = EasingMode.EaseIn });
        AddNotificationMotion(outgoing, NotificationExpandedTransform, "TranslateY", 0, direction * 22, 260,
            new CubicEase { EasingMode = EasingMode.EaseIn });
        AddNotificationMotion(outgoing, NotificationExpandedTransform, "ScaleX", 1, 0.94, 260,
            new CubicEase { EasingMode = EasingMode.EaseIn });
        AddNotificationMotion(outgoing, NotificationExpandedTransform, "ScaleY", 1, 0.94, 260,
            new CubicEase { EasingMode = EasingMode.EaseIn });
        var stackEase = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (direction < 0 && _notificationItems.Count > 1)
        {
            AddNotificationMotion(outgoing, NotificationBackCard1, "Opacity", NotificationBackCard1.Opacity, 0.96, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform1, "TranslateX", NotificationBackTransform1.TranslateX, 0, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform1, "TranslateY", NotificationBackTransform1.TranslateY, 0, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform1, "ScaleX", NotificationBackTransform1.ScaleX, 1, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform1, "ScaleY", NotificationBackTransform1.ScaleY, 1, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackCard2, "Opacity", NotificationBackCard2.Opacity, 0.52, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform2, "TranslateX", NotificationBackTransform2.TranslateX, 14, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform2, "TranslateY", NotificationBackTransform2.TranslateY, -22, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackCard3, "Opacity", NotificationBackCard3.Opacity, 0.30, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform3, "TranslateX", NotificationBackTransform3.TranslateX, 28, 300, stackEase);
            AddNotificationMotion(outgoing, NotificationBackTransform3, "TranslateY", NotificationBackTransform3.TranslateY, -46, 300, stackEase);
        }
        outgoing.Completed += (_, _) =>
        {
            _notificationIndex = nextIndex;
            ApplyCurrentNotification();
            ApplyNotificationDepthStack();
            NotificationExpandedLayer.Opacity = 0;
            NotificationExpandedTransform.TranslateY = -direction * 22;
            NotificationExpandedTransform.ScaleX = NotificationExpandedTransform.ScaleY = 0.94;

            var incoming = new Storyboard();
            var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
            AddNotificationMotion(incoming, NotificationExpandedLayer, "Opacity", 0, 1, 420, ease);
            AddNotificationMotion(incoming, NotificationExpandedTransform, "TranslateY", -direction * 22, 0, 420, ease);
            AddNotificationMotion(incoming, NotificationExpandedTransform, "ScaleX", 0.94, 1, 420, ease);
            AddNotificationMotion(incoming, NotificationExpandedTransform, "ScaleY", 0.94, 1, 420, ease);
            incoming.Completed += (_, _) =>
            {
                NotificationExpandedLayer.Opacity = 1;
                NotificationExpandedTransform.TranslateY = 0;
                NotificationExpandedTransform.ScaleX = NotificationExpandedTransform.ScaleY = 1;
                _notificationTransitioning = false;
                _notificationScrollMotion = null;
            };
            _notificationScrollMotion = incoming;
            incoming.Begin();
        };
        _notificationScrollMotion = outgoing;
        outgoing.Begin();
    }

    private static void AddNotificationMotion(Storyboard storyboard, DependencyObject target, string property,
        double from, double to, int milliseconds, EasingFunctionBase easing)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            EasingFunction = easing
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void NotificationCollapse_Click(object sender, RoutedEventArgs e) =>
        AnimateNotificationDock(!_notificationCollapsed);

    private void CoreTaskDashboardToggle_Click(object sender, RoutedEventArgs e) =>
        AnimateCoreTaskDashboard(!_coreDashboardExpanded);

    private void ConfigureCoreTaskDashboardMotion()
    {
        _coreDashboardAmbientMotion?.Stop();
        var motion = new Storyboard();
        var scan = new DoubleAnimation
        {
            From = -18, To = 92, Duration = new Duration(TimeSpan.FromSeconds(2.8)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(scan, CoreDashboardScanTransform);
        Storyboard.SetTargetProperty(scan, "X");
        motion.Children.Add(scan);
        var pulse = new DoubleAnimation
        {
            From = 0.38, To = 1, Duration = new Duration(TimeSpan.FromSeconds(1.55)),
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, CoreDashboardCollapsedPulse);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        motion.Children.Add(pulse);
        _coreDashboardAmbientMotion = motion;
        motion.Begin();
    }

    private void AnimateCoreTaskDashboard(bool expanded)
    {
        if (_coreDashboardExpanded == expanded && _coreDashboardTransitionMotion is null) return;
        _coreDashboardTransitionMotion?.Stop();
        _coreDashboardExpanded = expanded;
        CoreTaskDashboardExpandedLayer.IsHitTestVisible = expanded;
        CoreTaskDashboardCollapsedLayer.IsHitTestVisible = !expanded;
        var motion = new Storyboard();
        var easing = new QuinticEase { EasingMode = EasingMode.EaseOut };
        AddNotificationMotion(motion, CoreTaskDashboardExpandedLayer, "Opacity",
            CoreTaskDashboardExpandedLayer.Opacity, expanded ? 1 : 0, expanded ? 520 : 250, easing);
        AddNotificationMotion(motion, CoreTaskDashboardExpandedTransform, "ScaleX",
            CoreTaskDashboardExpandedTransform.ScaleX, expanded ? 1 : 0.78, expanded ? 540 : 260, easing);
        AddNotificationMotion(motion, CoreTaskDashboardExpandedTransform, "ScaleY",
            CoreTaskDashboardExpandedTransform.ScaleY, expanded ? 1 : 0.78, expanded ? 540 : 260, easing);
        AddNotificationMotion(motion, CoreTaskDashboardCollapsedLayer, "Opacity",
            CoreTaskDashboardCollapsedLayer.Opacity, expanded ? 0 : 1, expanded ? 180 : 360, easing);
        AddNotificationMotion(motion, CoreTaskDashboardCollapsedTransform, "ScaleX",
            CoreTaskDashboardCollapsedTransform.ScaleX, expanded ? 0.62 : 1, expanded ? 230 : 420, easing);
        AddNotificationMotion(motion, CoreTaskDashboardCollapsedTransform, "ScaleY",
            CoreTaskDashboardCollapsedTransform.ScaleY, expanded ? 0.62 : 1, expanded ? 230 : 420, easing);
        motion.Completed += (_, _) =>
        {
            ApplyCoreTaskDashboardState(expanded);
            _coreDashboardTransitionMotion = null;
        };
        _coreDashboardTransitionMotion = motion;
        motion.Begin();
    }

    private void ApplyCoreTaskDashboardState(bool expanded)
    {
        _coreDashboardExpanded = expanded;
        CoreTaskDashboardExpandedLayer.Opacity = expanded ? 1 : 0;
        CoreTaskDashboardExpandedTransform.ScaleX = CoreTaskDashboardExpandedTransform.ScaleY = expanded ? 1 : 0.78;
        CoreTaskDashboardCollapsedLayer.Opacity = expanded ? 0 : 1;
        CoreTaskDashboardCollapsedTransform.ScaleX = CoreTaskDashboardCollapsedTransform.ScaleY = expanded ? 0.62 : 1;
        CoreTaskDashboardExpandedLayer.IsHitTestVisible = expanded;
        CoreTaskDashboardCollapsedLayer.IsHitTestVisible = !expanded;
    }

    private void AnimateNotificationDock(bool collapsed)
    {
        if (_notificationCollapsed == collapsed && _notificationDockMotion is null) return;
        _notificationDockMotion?.Stop();
        var storyboard = new Storyboard();
        var easing = new QuinticEase { EasingMode = EasingMode.EaseOut };
        var dockDuration = collapsed ? 300 : 720;

        if (!collapsed)
        {
            NotificationExpandedTransform.TranslateX = -72;
            NotificationExpandedTransform.ScaleX = 0.92;
        }

        var width = new DoubleAnimation
        {
            From = NotificationDock.Width,
            To = collapsed ? 118 : 680,
            Duration = new Duration(TimeSpan.FromMilliseconds(dockDuration)),
            EnableDependentAnimation = true,
            EasingFunction = easing
        };
        Storyboard.SetTarget(width, NotificationDock);
        Storyboard.SetTargetProperty(width, "Width");
        storyboard.Children.Add(width);
        AddNotificationMotion(storyboard, NotificationExpandedLayer, "Opacity",
            NotificationExpandedLayer.Opacity, collapsed ? 0 : 1, collapsed ? 130 : 560, easing);
        AddNotificationMotion(storyboard, NotificationExpandedTransform, "TranslateX",
            NotificationExpandedTransform.TranslateX, collapsed ? -36 : 0, collapsed ? 220 : 720, easing);
        AddNotificationMotion(storyboard, NotificationExpandedTransform, "ScaleX",
            NotificationExpandedTransform.ScaleX, collapsed ? 0.96 : 1, collapsed ? 220 : 720, easing);
        AddNotificationMotion(storyboard, NotificationCollapsedLayer, "Opacity",
            NotificationCollapsedLayer.Opacity, collapsed ? 1 : 0, collapsed ? 230 : 180, easing);
        _notificationCollapsed = collapsed;
        NotificationExpandedLayer.IsHitTestVisible = !collapsed;
        NotificationCollapsedLayer.IsHitTestVisible = collapsed;
        storyboard.Completed += (_, _) =>
        {
            ApplyNotificationDockState(collapsed);
            _notificationDockMotion = null;
        };
        _notificationDockMotion = storyboard;
        storyboard.Begin();
    }

    private void ApplyNotificationDockState(bool collapsed)
    {
        _notificationCollapsed = collapsed;
        NotificationDock.Width = collapsed ? 118 : 680;
        NotificationExpandedLayer.Opacity = collapsed ? 0 : 1;
        NotificationCollapsedLayer.Opacity = collapsed ? 1 : 0;
        NotificationExpandedTransform.TranslateX = collapsed ? -36 : 0;
        NotificationExpandedTransform.ScaleX = collapsed ? 0.96 : 1;
        NotificationExpandedLayer.IsHitTestVisible = !collapsed;
        NotificationCollapsedLayer.IsHitTestVisible = collapsed;
        NotificationStackLayer.Opacity = collapsed ? 0 : 1;
        AutomationProperties.SetName(NotificationCollapseButton,
            collapsed ? "Expand notification carousel" : "Collapse notification carousel");
        ToolTipService.SetToolTip(NotificationCollapseButton,
            collapsed ? "Expand notifications" : "Collapse notifications");
    }

    private void UpdateNotificationSequence(int count)
    {
        CollapsedNoticeRomanText.Text = ToRomanNumeral(count);
        AutomationProperties.SetName(CollapsedNoticeRomanText, $"{count} notifications");
    }

    private void ApplyNotificationDepthStack()
    {
        var cardsBehind = Math.Max(0, _notificationItems.Count - 1);
        NotificationBackCard1.Opacity = cardsBehind >= 1 ? 0.52 : 0;
        NotificationBackCard2.Opacity = cardsBehind >= 2 ? 0.30 : 0;
        NotificationBackCard3.Opacity = cardsBehind >= 3 ? 0.14 : 0;

        NotificationBackTitle1.Text = cardsBehind >= 1
            ? _notificationItems[(_notificationIndex + 1) % _notificationItems.Count].Title : string.Empty;
        NotificationBackTitle2.Text = cardsBehind >= 2
            ? _notificationItems[(_notificationIndex + 2) % _notificationItems.Count].Title : string.Empty;
        NotificationBackTitle3.Text = cardsBehind >= 3
            ? _notificationItems[(_notificationIndex + 3) % _notificationItems.Count].Title : string.Empty;

        NotificationBackTransform1.TranslateX = 14;
        NotificationBackTransform1.TranslateY = -22;
        NotificationBackTransform1.ScaleX = NotificationBackTransform1.ScaleY = 0.982;
        NotificationBackTransform2.TranslateX = 28;
        NotificationBackTransform2.TranslateY = -46;
        NotificationBackTransform2.ScaleX = NotificationBackTransform2.ScaleY = 0.958;
        NotificationBackTransform3.TranslateX = 42;
        NotificationBackTransform3.TranslateY = -70;
        NotificationBackTransform3.ScaleX = NotificationBackTransform3.ScaleY = 0.934;
    }

    private static string ToRomanNumeral(int value)
    {
        if (value <= 0) return "—";
        var remaining = Math.Min(value, 3999);
        var result = new System.Text.StringBuilder();
        var symbols = new (int Value, string Symbol)[]
        {
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        };
        foreach (var (number, symbol) in symbols)
        {
            while (remaining >= number)
            {
                result.Append(symbol);
                remaining -= number;
            }
        }
        if (value > 3999) result.Append('+');
        return result.ToString();
    }

    private async void Services_ReminderTriggered(object? sender, Reminder reminder)
    {
        await DispatcherQueue.EnqueueAsync(() => AddConversationCard("system", $"Task due: {reminder.Title}"));
        await DispatcherQueue.EnqueueAsync(() => _ = RefreshRemindersAsync());
    }

    private void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            e.Handled = true;
            _ = SendPromptAsync();
        }
    }

    private TextBlock AddConversationCard(string role, string text, bool streaming = false)
    {
        var isUser = role == "user";
        var accent = role switch
        {
            "user" => Color.FromArgb(235, 0, 229, 255),
            "assistant" => Color.FromArgb(225, 0, 224, 166),
            _ => Color.FromArgb(190, 93, 188, 205)
        };
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Orbitron-Medium.ttf#Orbitron Medium"),
            FontSize = 8.5,
            LineHeight = 14,
            Foreground = new SolidColorBrush(Color.FromArgb(235, 229, 242, 247)),
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 5, 0, 0)
        };
        var meta = new TextBlock
        {
            Text = role switch
            {
                "user" => "TX // OPERATOR",
                "assistant" => streaming ? "RX // CODEX STREAM" : "RX // CODEX",
                _ => "SYS // LINK EVENT"
            },
            FontFamily = new FontFamily("ms-appx:///Assets/Fonts/Orbitron-Medium.ttf#Orbitron Medium"),
            FontSize = 7,
            CharacterSpacing = 95,
            Foreground = new SolidColorBrush(accent)
        };
        var body = new StackPanel
        {
            Margin = isUser ? new Thickness(10, 10, 13, 9) : new Thickness(13, 10, 10, 9),
            Children = { meta, content }
        };
        var message = new Grid
        {
            Background = new SolidColorBrush(role switch
            {
                "user" => Color.FromArgb(22, 0, 183, 212),
                "assistant" => Color.FromArgb(18, 0, 219, 164),
                _ => Color.FromArgb(14, 106, 147, 160)
            }),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            MaxWidth = 232,
            MinHeight = 50
        };
        var trace = new Polyline
        {
            Points = isUser
                ? new PointCollection { new(0, 0), new(56, 0), new(66, 10), new(66, 22) }
                : new PointCollection { new(66, 0), new(10, 0), new(0, 10), new(0, 22) },
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1,
            Width = 67,
            Height = 23,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = 0.78
        };
        var edge = new Rectangle
        {
            Width = 2,
            Height = 25,
            Fill = new SolidColorBrush(accent),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0.7
        };
        var node = new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = new SolidColorBrush(accent),
            HorizontalAlignment = isUser ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(7, 7, 7, 0),
            Opacity = streaming ? 1 : 0.45
        };
        var lowerTrace = new Rectangle
        {
            Width = isUser ? 38 : 54,
            Height = 1,
            Fill = new SolidColorBrush(Color.FromArgb(95, accent.R, accent.G, accent.B)),
            HorizontalAlignment = isUser ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        message.Children.Add(trace);
        message.Children.Add(edge);
        message.Children.Add(node);
        message.Children.Add(lowerTrace);
        message.Children.Add(body);
        ConversationList.Children.Add(message);
        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null);
        return content;
    }

    private void SetTurnState(bool running)
    {
        _turnRunning = running;
        SendButton.IsEnabled = !running;
        VoiceButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
    }

    private static Border WrapPanel(UIElement child) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(185, 11, 19, 27)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(48, 39, 220, 236)),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14), Child = child
    };

    private static Grid WrapTaskRow(UIElement child)
    {
        var frame = new Grid { MinHeight = 58 };
        frame.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(205, 5, 17, 24)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(72, 0, 229, 255)),
            BorderThickness = new Thickness(1), Padding = new Thickness(12, 9, 10, 9), Child = child
        });
        frame.Children.Add(new Rectangle
        {
            Width = 3, Margin = new Thickness(0, 8, 0, 8), HorizontalAlignment = HorizontalAlignment.Left,
            Fill = new SolidColorBrush(Color.FromArgb(220, 0, 229, 255))
        });
        frame.Children.Add(new Rectangle
        {
            Width = 58, Height = 2, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(14, 0, 0, 0),
            Fill = new SolidColorBrush(Color.FromArgb(150, 0, 229, 255))
        });
        return frame;
    }

    private static Button TaskIconButton(string glyph, string accessibleName, string taskId)
    {
        var button = new Button
        {
            Content = glyph, Tag = taskId, Width = 40, Height = 36, MinWidth = 40, MinHeight = 36,
            Padding = new Thickness(0), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        };
        ToolTipService.SetToolTip(button, accessibleName);
        AutomationProperties.SetName(button, accessibleName);
        return button;
    }

    private static SolidColorBrush MutedBrush() => new(Color.FromArgb(255, 120, 144, 156));
    private static SolidColorBrush CyanBrush() => new(Color.FromArgb(255, 0, 229, 255));
    private static SolidColorBrush UrgencyBrush(TaskUrgency urgency) => new(urgency switch
    {
        TaskUrgency.Critical => Color.FromArgb(255, 255, 82, 96),
        TaskUrgency.High => Color.FromArgb(255, 255, 184, 77),
        TaskUrgency.Low => Color.FromArgb(255, 120, 144, 156),
        _ => Color.FromArgb(255, 226, 244, 248)
    });
    private static string UrgencyGlyph(TaskUrgency urgency) => urgency switch
    {
        TaskUrgency.Critical => "▲▲",
        TaskUrgency.High => "▲",
        TaskUrgency.Low => "▽",
        _ => "◆"
    };
    private static string StatusLabel(TaskProgressStatus status) => status switch
    {
        TaskProgressStatus.Doing => "DOING",
        TaskProgressStatus.Done => "DONE",
        _ => "NOT YET"
    };
    private static string StatusGlyph(TaskProgressStatus status) => status switch
    {
        TaskProgressStatus.Doing => "◉",
        TaskProgressStatus.Done => "✓",
        _ => "○"
    };
    private static string TaskTimingLabel(Reminder task)
    {
        var type = task.TaskType switch { UserTaskType.Recurring => "ROUTINARY", UserTaskType.Someday => "SOMEDAY", _ => "RUN ONCE" };
        var parts = new List<string> { task.Urgency.ToString().ToUpperInvariant(), StatusLabel(task.Status), type };
        if (task.ExecutionMode == TaskExecutionMode.AiAssisted) parts.Add("AI ASSISTED");
        if (task.IsToday) parts.Add("TODAY");
        if (task.HasSchedule) parts.Add($"SCHEDULE {task.DueAt:ddd, MMM d · HH:mm}");
        if (task.Deadline is { } deadline) parts.Add($"DEADLINE {deadline:ddd, MMM d}");
        if (!task.IsToday && !task.HasSchedule && task.Deadline is null) parts.Add("SOMEDAY");
        return string.Join("  ·  ", parts);
    }
    private static string AiStateLabel(Reminder task)
    {
        if (!string.IsNullOrWhiteSpace(task.ResultSummary))
        {
            var result = task.ResultSummary.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (result.Length > 180) result = result[..180] + "…";
            return $"LAST RESULT · {result}" + (string.IsNullOrWhiteSpace(task.ResultPath) ? "" : $"  ·  {task.ResultPath}");
        }
        if (!string.IsNullOrWhiteSpace(task.AiLastResult))
        {
            var result = task.AiLastResult.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (result.Length > 180) result = result[..180] + "…";
            return $"AI LAST RUN · {result}";
        }
        return string.IsNullOrWhiteSpace(task.AiInstruction)
            ? "AI READY · uses task title as instruction"
            : $"AI READY · {task.AiInstruction}";
    }
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string ShortId(string id) => id.Length <= 8 ? id : id[..8];
}

internal static class DispatcherQueueExtensions
{
    public static Task EnqueueAsync(this Microsoft.UI.Dispatching.DispatcherQueue queue, Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.TryEnqueue(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } });
        return completion.Task;
    }
}
