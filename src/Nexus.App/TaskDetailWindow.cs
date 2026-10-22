using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.App.Controls;
using Nexus.Core.Models;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Nexus.App;

public sealed class TaskDetailWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly InputNonClientPointerSource _nonClient;
    private readonly nint _handle;
    private Reminder _task;
    private readonly TextBox _title = Field("Task title");
    private readonly TextBox _instructions = Field("AI instructions and context", multiline: true);
    private readonly TextBox _result = Field("What was actually done?", multiline: true);
    private readonly TextBox _resultPath = Field("Result file or folder path");
    private readonly TextBox _suggestion = Field("AI recommendation will appear here", multiline: true, readOnly: true);
    private readonly TextBox _additional = Field("Add or change instructions before approving", multiline: true);
    private readonly AlienCycleSelector _type = Choice(("RUN ONCE", UserTaskType.OneTime), ("ROUTINARY", UserTaskType.Recurring), ("SOMEDAY", UserTaskType.Someday));
    private readonly AlienCycleSelector _execution = Choice(("MANUAL", TaskExecutionMode.Manual), ("AI ASSISTED", TaskExecutionMode.AiAssisted));
    private readonly AlienCycleSelector _urgency = Choice(("LOW", TaskUrgency.Low), ("NORMAL", TaskUrgency.Normal), ("HIGH", TaskUrgency.High), ("CRITICAL", TaskUrgency.Critical));
    private readonly AlienCycleSelector _recurrence = Choice(("NO REPEAT", RecurrenceKind.None), ("DAILY", RecurrenceKind.Daily), ("WEEKDAYS", RecurrenceKind.Weekdays), ("WEEKLY", RecurrenceKind.Weekly), ("MONTHLY", RecurrenceKind.Monthly));
    private readonly AlienToggleChip _today = new() { Content = "TODAY" };
    private readonly AlienToggleChip _scheduled = new() { Content = "SCHEDULED" };
    private readonly AlienDatePicker _date = new();
    private readonly AlienTimeSelector _time = new();
    private readonly AlienDatePicker _deadline = new();
    private readonly TextBlock _status = new() { FontSize = 10, CharacterSpacing = 65 };
    private readonly TextBlock _busy = new() { FontSize = 9, Foreground = Muted() };
    private readonly StackPanel _history = new() { Spacing = 7 };
    private readonly Button _save = Action("SAVE CHANGES");
    private readonly Button _done = Action("MARK DONE");
    private readonly Button _run = Action("RUN TASK WITH AI");
    private readonly Button _recommend = Action("ASK AI WHAT NEXT");
    private readonly Button _approve = Action("APPROVE + RUN");
    private readonly Button _openResult = Action("OPEN RESULT");
    private bool _working;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    public event Action<Reminder>? TaskUpdated;

    public TaskDetailWindow(Reminder task)
    {
        _task = task;
        Title = $"DAGNUE TASK // {task.Title}";
        _handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;
        _presenter = (OverlappedPresenter)_appWindow.Presenter;
        _nonClient = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new SizeInt32(1360, 860));
        Content = BuildLayout();
        UpdateCaptionRegions();
        _appWindow.Changed += (_, args) => { if (args.DidSizeChange) UpdateCaptionRegions(); };
        LoadTask();
        _save.Click += async (_, _) => await SaveAsync();
        _done.Click += async (_, _) => await MarkDoneAsync();
        _run.Click += async (_, _) => await RunAiAsync(useSuggestion: false);
        _recommend.Click += async (_, _) => await RecommendAsync();
        _approve.Click += async (_, _) => await RunAiAsync(useSuggestion: true);
        _openResult.Click += (_, _) => OpenResult();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Background = Brush(255, 2, 8, 13) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildTitleBar());
        var heading = new StackPanel { Spacing = 4, Margin = new Thickness(26, 18, 26, 0) };
        heading.Children.Add(new TextBlock { Text = "TASK // EXECUTION & RESULT MATRIX", FontSize = 22, CharacterSpacing = 55, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Cyan() });
        heading.Children.Add(_status);
        heading.Children.Add(_busy);
        Grid.SetRow(heading, 1);
        root.Children.Add(heading);

        var scroll = new ScrollViewer { Margin = new Thickness(26, 18, 26, 22) };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(Section("01 // EDITABLE TASK", BuildEditor()));
        var workDeck = new Grid { ColumnSpacing = 14 };
        workDeck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workDeck.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var result = Section("02 // COMPLETION RESULT", BuildResult());
        var assist = Section("03 // AI NEXT-ACTION ASSIST", BuildRecommendation());
        workDeck.Children.Add(result); Grid.SetColumn(assist, 1); workDeck.Children.Add(assist);
        content.Children.Add(workDeck);
        content.Children.Add(Section("04 // TASK ACTIVITY", _history));
        scroll.Content = content;
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        return root;
    }

    private Grid BuildTitleBar()
    {
        var bar = new Grid { Height = 38, Background = Brush(245, 3, 12, 18) };
        bar.Children.Add(new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brush(110, 0, 229, 255)
        });
        bar.Children.Add(new TextBlock
        {
            Text = "◇  DAGNUE // TASK CONTROL INTERFACE",
            FontSize = 9,
            CharacterSpacing = 110,
            Margin = new Thickness(15, 0, 145, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(200, 0, 229, 255),
            IsHitTestVisible = false
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(CaptionButton("—", () => _presenter.Minimize(), false));
        buttons.Children.Add(CaptionButton("◇", () =>
        {
            if (_presenter.State == OverlappedPresenterState.Maximized)
                _presenter.Restore();
            else
                _presenter.Maximize();
        }, false));
        buttons.Children.Add(CaptionButton("×", Close, true));
        bar.Children.Add(buttons);
        return bar;
    }

    private void UpdateCaptionRegions()
    {
        var scale = Math.Max(1d, GetDpiForWindow(_handle) / 96d);
        var height = (int)Math.Ceiling(38 * scale);
        var buttons = (int)Math.Ceiling(138 * scale);
        var caption = Math.Max(1, _appWindow.Size.Width - buttons);
        _nonClient.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32(0, 0, caption, height)]);
        _nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [new RectInt32(caption, 0, buttons, height)]);
    }

    private static Button CaptionButton(string text, Action action, bool danger)
    {
        var button = new Button
        {
            Content = text,
            Width = 46,
            Height = 37,
            Padding = new Thickness(0),
            Background = Brush(0, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Foreground = danger ? Brush(255, 255, 85, 103) : Brush(200, 0, 229, 255)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private UIElement BuildEditor()
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(_title);
        var row1 = Columns(4);
        Add(row1, Labeled("TYPE", _type), 0); Add(row1, Labeled("EXECUTION", _execution), 1);
        Add(row1, Labeled("URGENCY", _urgency), 2); Add(row1, Labeled("REPEATS", _recurrence), 3);
        panel.Children.Add(row1);
        var row2 = Columns(4);
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
        flags.Children.Add(_today); flags.Children.Add(_scheduled); Add(row2, Labeled("ACTIVE SIGNALS", flags), 0);
        Add(row2, Labeled("SCHEDULE DATE", _date), 1); Add(row2, Labeled("TIME // ±15 MIN", _time), 2); Add(row2, Labeled("DEADLINE", _deadline), 3);
        panel.Children.Add(row2);
        panel.Children.Add(_instructions);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        buttons.Children.Add(_save); buttons.Children.Add(_run); buttons.Children.Add(_done);
        panel.Children.Add(buttons);
        return panel;
    }

    private UIElement BuildResult()
    {
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(_result);
        var row = new Grid { ColumnSpacing = 9 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(_resultPath); Add(row, _openResult, 1);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "The result can be a work summary, a file, a folder, or the exact action completed. Saving the task preserves it in the activity timeline.", FontSize = 9, Foreground = Muted(), TextWrapping = TextWrapping.Wrap });
        return panel;
    }

    private UIElement BuildRecommendation()
    {
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(new TextBlock { Text = "AI reviews this task's instructions, prior completions, results, and activity before proposing the next action.", FontSize = 9, Foreground = Muted(), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_suggestion);
        panel.Children.Add(_additional);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        actions.Children.Add(_recommend); actions.Children.Add(_approve);
        panel.Children.Add(actions);
        return panel;
    }

    private async Task SaveAsync()
    {
        if (_working || string.IsNullOrWhiteSpace(_title.Text)) return;
        var hasSchedule = _scheduled.IsChecked == true && _date.Date is not null && _time.SelectedTime is not null;
        var due = _task.DueAt;
        if (hasSchedule)
        {
            var date = _date.Date!.Value; var time = _time.SelectedTime!.Value;
            due = new DateTimeOffset(date.Year, date.Month, date.Day, time.Hours, time.Minutes, 0, DateTimeOffset.Now.Offset);
        }
        DateTimeOffset? deadline = _deadline.Date is { } d ? new DateTimeOffset(d.Year, d.Month, d.Day, 23, 59, 0, DateTimeOffset.Now.Offset) : null;
        var type = Selected<UserTaskType>(_type, _task.TaskType);
        var recurrence = type == UserTaskType.Recurring ? Selected<RecurrenceKind>(_recurrence, RecurrenceKind.Daily) : RecurrenceKind.None;
        if (type == UserTaskType.Recurring && recurrence == RecurrenceKind.None) recurrence = RecurrenceKind.Daily;
        _task = _task with
        {
            Title = _title.Text.Trim(), AiInstruction = Blank(_instructions.Text), TaskType = type,
            ExecutionMode = Selected<TaskExecutionMode>(_execution, _task.ExecutionMode),
            Urgency = Selected<TaskUrgency>(_urgency, _task.Urgency), Recurrence = recurrence,
            IsToday = _today.IsChecked == true, HasSchedule = hasSchedule, DueAt = due,
            IsEnabled = _task.Status != TaskProgressStatus.Done && hasSchedule, Deadline = deadline,
            ResultSummary = Blank(_result.Text), ResultPath = Blank(_resultPath.Text)
        };
        _task = UserTaskLifecycle.AddActivity(_task, "edited", "Task details saved.", _task.ResultPath);
        await PersistAsync("Changes saved.");
    }

    private async Task MarkDoneAsync()
    {
        if (_working) return;
        await SaveAsync();
        var summary = Blank(_result.Text) ?? "Marked done manually.";
        _task = UserTaskLifecycle.Complete(_task, summary, Blank(_resultPath.Text), "manual-completion");
        await PersistAsync(_task.TaskType == UserTaskType.Recurring ? "Occurrence done; next run scheduled." : "Task marked done.");
        App.Services.Notifications.Show("Task completed", _task.Title);
    }

    private async Task RecommendAsync()
    {
        if (_working) return;
        await SaveAsync();
        await WithWorkingAsync("AI is reviewing previous work and preparing the next action...", async () =>
        {
            var prompt = $"You are assisting with a persistent DAGNUE task. Recommend the single best next action, but do not execute it yet.\n{TaskContext()}\n" +
                "Be concrete. Mention the expected result and any file or folder that should be produced or updated. Keep it reviewable before execution.";
            var result = await RunTurnAsync(prompt);
            if (result.Error is not null || result.WasInterrupted) throw new InvalidOperationException(result.Error ?? "AI recommendation was interrupted.");
            _suggestion.Text = result.FinalText.Trim();
            _task = UserTaskLifecycle.AddActivity(_task with
            {
                AiThreadId = result.ThreadId, AiSuggestion = _suggestion.Text, AiSuggestionAt = DateTimeOffset.Now
            }, "ai-recommendation", _suggestion.Text);
            await PersistAsync("AI recommendation ready.");
        });
    }

    private async Task RunAiAsync(bool useSuggestion)
    {
        if (_working) return;
        await SaveAsync();
        var recommendation = useSuggestion ? Blank(_suggestion.Text) ?? _task.AiSuggestion : null;
        if (useSuggestion && string.IsNullOrWhiteSpace(recommendation))
        {
            _busy.Text = "Generate a recommendation first, then approve it.";
            return;
        }
        await WithWorkingAsync("AI is performing the approved task...", async () =>
        {
            _task = _task with { Status = TaskProgressStatus.Doing };
            await App.Services.Repository.SaveReminderAsync(_task);
            var prompt = "Execute this DAGNUE task using the computer tools available to you. Respect permission and confirmation boundaries. " +
                "When finished, report exactly what was done and include full file/folder paths for any outputs.\n" + TaskContext() +
                (recommendation is null ? "" : $"\nApproved AI recommendation:\n{recommendation}") +
                (string.IsNullOrWhiteSpace(_additional.Text) ? "" : $"\nAdditional approved instructions:\n{_additional.Text.Trim()}");
            var result = await RunTurnAsync(prompt);
            var succeeded = result.Error is null && !result.WasInterrupted;
            var summary = succeeded && !string.IsNullOrWhiteSpace(result.FinalText) ? result.FinalText.Trim() : result.Error ?? "AI run interrupted.";
            var state = _task with { AiThreadId = result.ThreadId, AiLastRunAt = DateTimeOffset.Now, AiLastResult = summary };
            _task = succeeded ? UserTaskLifecycle.Complete(state, summary, kind: "ai-completion")
                : UserTaskLifecycle.AddActivity(state with { Status = TaskProgressStatus.NotYet }, "ai-failed", summary);
            await PersistAsync(succeeded ? "AI completed the task." : "AI run needs attention.");
            App.Services.Notifications.Show(succeeded ? "AI task completed" : "AI task needs attention", _task.Title);
        });
    }

    private async Task<CodexTurnResult> RunTurnAsync(string prompt)
    {
        if (!App.Services.Codex.IsConnected) await App.Services.Codex.StartAsync();
        var previousThread = App.Services.Codex.ActiveThreadId;
        var thread = _task.AiThreadId;
        try
        {
            if (string.IsNullOrWhiteSpace(thread))
                thread = await App.Services.Codex.StartThreadAsync(model: Blank(App.Services.Settings.CodexModel));
            else
                try { thread = await App.Services.Codex.ResumeThreadAsync(thread); }
                catch { thread = await App.Services.Codex.StartThreadAsync(model: Blank(App.Services.Settings.CodexModel)); }
            return await App.Services.Codex.RunTurnAsync(prompt, thread);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(previousThread) && !string.Equals(previousThread, thread, StringComparison.Ordinal))
                try { await App.Services.Codex.ResumeThreadAsync(previousThread); } catch { }
        }
    }

    private string TaskContext()
    {
        var history = string.Join("\n", (_task.Activity ?? []).TakeLast(12).Select(item => $"- {item.Timestamp:g} [{item.Kind}] {item.Summary}"));
        return $"Task: {_task.Title}\nType: {_task.TaskType}\nUrgency: {_task.Urgency}\nInstructions: {_task.AiInstruction ?? _task.Title}\n" +
            $"Previous result: {_task.ResultSummary ?? "none"}\nResult path: {_task.ResultPath ?? "none"}\nRecent activity:\n{(history.Length == 0 ? "none" : history)}";
    }

    private async Task PersistAsync(string message)
    {
        await App.Services.Repository.SaveReminderAsync(_task);
        _busy.Text = message;
        LoadTask();
        TaskUpdated?.Invoke(_task);
    }

    private async Task WithWorkingAsync(string message, Func<Task> action)
    {
        SetWorking(true, message);
        try { await action(); }
        catch (Exception ex) { _busy.Text = $"ERROR // {ex.Message}"; App.Services.LogCrash(ex); }
        finally { SetWorking(false, _busy.Text); }
    }

    private void SetWorking(bool working, string message)
    {
        _working = working; _busy.Text = message;
        foreach (var button in new[] { _save, _done, _run, _recommend, _approve }) button.IsEnabled = !working;
    }

    private void LoadTask()
    {
        _title.Text = _task.Title; _instructions.Text = _task.AiInstruction ?? "";
        _result.Text = _task.ResultSummary ?? _task.AiLastResult ?? ""; _resultPath.Text = _task.ResultPath ?? "";
        _suggestion.Text = _task.AiSuggestion ?? "";
        Select(_type, _task.TaskType); Select(_execution, _task.ExecutionMode); Select(_urgency, _task.Urgency); Select(_recurrence, _task.Recurrence);
        _today.IsChecked = _task.IsToday; _scheduled.IsChecked = _task.HasSchedule;
        _date.Date = _task.HasSchedule && _task.DueAt != DateTimeOffset.MaxValue ? _task.DueAt : null;
        _time.SelectedTime = _task.HasSchedule && _task.DueAt != DateTimeOffset.MaxValue ? _task.DueAt.TimeOfDay : null;
        _deadline.Date = _task.Deadline;
        _status.Text = $"{_task.Status.ToString().ToUpperInvariant()}  //  {_task.TaskType.ToString().ToUpperInvariant()}  //  {_task.ExecutionMode.ToString().ToUpperInvariant()}";
        _done.Content = _task.TaskType == UserTaskType.Recurring ? "COMPLETE OCCURRENCE" : "MARK DONE";
        _openResult.IsEnabled = !string.IsNullOrWhiteSpace(_task.ResultPath) && (File.Exists(_task.ResultPath) || Directory.Exists(_task.ResultPath));
        RenderHistory();
    }

    private void RenderHistory()
    {
        _history.Children.Clear();
        foreach (var item in (_task.Activity ?? []).OrderByDescending(item => item.Timestamp))
            _history.Children.Add(new TextBlock { Text = $"{item.Timestamp:g}  //  {item.Kind.ToUpperInvariant()}\n{item.Summary}" + (string.IsNullOrWhiteSpace(item.ResultPath) ? "" : $"\nRESULT // {item.ResultPath}"), TextWrapping = TextWrapping.Wrap, FontSize = 9, Foreground = item.Kind.Contains("failed", StringComparison.OrdinalIgnoreCase) ? Brush(255, 255, 99, 120) : Muted() });
        if (_history.Children.Count == 0) _history.Children.Add(new TextBlock { Text = "NO TASK ACTIVITY YET", Foreground = Muted(), FontSize = 9 });
    }

    private void OpenResult()
    {
        var path = Blank(_resultPath.Text) ?? _task.ResultPath;
        if (path is null || (!File.Exists(path) && !Directory.Exists(path))) { _busy.Text = "Result path does not exist."; return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static Border Section(string title, UIElement child)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, Foreground = Cyan(), FontSize = 12, CharacterSpacing = 65, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(child);
        return new Border { Background = Brush(205, 7, 18, 25), BorderBrush = Brush(95, 0, 229, 255), BorderThickness = new Thickness(1), Padding = new Thickness(16), Child = panel };
    }

    private static Grid Columns(int count) { var grid = new Grid { ColumnSpacing = 9 }; for (var i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); return grid; }
    private static void Add(Grid grid, FrameworkElement element, int column) { Grid.SetColumn(element, column); grid.Children.Add(element); }
    private static StackPanel Labeled(string label, UIElement element) { var panel = new StackPanel { Spacing = 3 }; panel.Children.Add(new TextBlock { Text = label, FontSize = 7, Foreground = Muted() }); panel.Children.Add(element); return panel; }
    private static TextBox Field(string placeholder, bool multiline = false, bool readOnly = false) => new() { PlaceholderText = placeholder, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 78 : 40, IsReadOnly = readOnly, Style = Application.Current.Resources["AlienTextBoxStyle"] as Style };
    private static Button Action(string text) => new() { Content = text, MinWidth = 150, Height = 40, Style = Application.Current.Resources["AlienActionButtonStyle"] as Style };
    private static AlienCycleSelector Choice<T>(params (string Label, T Value)[] values) where T : Enum { var selector = new AlienCycleSelector(); selector.SetOptions(values.Select(item => (item.Label, (object)item.Value)).ToArray()); return selector; }
    private static T Selected<T>(AlienCycleSelector selector, T fallback) where T : struct, Enum => Enum.TryParse<T>(selector.SelectedValue, out var value) ? value : fallback;
    private static void Select<T>(AlienCycleSelector selector, T value) where T : Enum => selector.SelectValue(value);
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static SolidColorBrush Cyan() => Brush(255, 0, 229, 255);
    private static SolidColorBrush Muted() => Brush(255, 120, 144, 156);
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
}
