using System.Runtime.InteropServices;
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

public sealed class NewTaskWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly InputNonClientPointerSource _nonClient;
    private readonly nint _handle;
    private readonly TextBox _title = Field("What needs to be done?");
    private readonly TextBox _instructions = Field("AI instructions and context (optional)", multiline: true);
    private readonly AlienCycleSelector _type = Choice(("RUN ONCE", UserTaskType.OneTime),
        ("ROUTINARY", UserTaskType.Recurring), ("SOMEDAY", UserTaskType.Someday));
    private readonly AlienCycleSelector _execution = Choice(("MANUAL", TaskExecutionMode.Manual),
        ("AI ASSISTED", TaskExecutionMode.AiAssisted));
    private readonly AlienCycleSelector _urgency = Choice(("LOW", TaskUrgency.Low),
        ("NORMAL", TaskUrgency.Normal), ("HIGH", TaskUrgency.High), ("CRITICAL", TaskUrgency.Critical));
    private readonly AlienCycleSelector _recurrence = Choice(("NO REPEAT", RecurrenceKind.None),
        ("DAILY", RecurrenceKind.Daily), ("WEEKDAYS", RecurrenceKind.Weekdays),
        ("WEEKLY", RecurrenceKind.Weekly), ("MONTHLY", RecurrenceKind.Monthly));
    private readonly AlienToggleChip _today = new() { Content = "TODAY", IsChecked = true };
    private readonly AlienToggleChip _scheduled = new() { Content = "SCHEDULE" };
    private readonly AlienDatePicker _date = new();
    private readonly AlienTimeSelector _time = new();
    private readonly AlienDatePicker _deadline = new();
    private readonly TextBlock _feedback = new() { FontSize = 9, Foreground = Muted() };
    private readonly StackPanel _tagChips = new() { Orientation = Orientation.Horizontal, Spacing = 7 };
    private IReadOnlyList<string> _selectedTags = ["GENERAL"];
    private bool _tagsTouched;
    private readonly Button _create = Action("CREATE TASK");
    private readonly Button _cancel = Action("CANCEL");
    private readonly AlienFormChrome _chrome = new();
    private bool _saving;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    public event Action<Reminder>? TaskCreated;

    public NewTaskWindow()
    {
        Title = "DAGNUE // NEW TASK";
        _handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;
        _presenter = (OverlappedPresenter)_appWindow.Presenter;
        _nonClient = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        SystemBackdrop = new MicaBackdrop();
        _appWindow.Resize(new SizeInt32(1340, 645));
        _date.Date = DateTimeOffset.Now.Date;
        _time.SelectedTime = DateTime.Now.TimeOfDay.Add(TimeSpan.FromHours(1));
        Select(_urgency, TaskUrgency.Normal);
        Content = BuildLayout();
        _chrome.SetAnimationEnabled(true);
        UpdateCaptionRegions();
        _appWindow.Changed += (_, args) => { if (args.DidSizeChange) UpdateCaptionRegions(); };
        _create.Click += async (_, _) => await CreateAsync();
        _cancel.Click += (_, _) => Close();
        _title.TextChanged += (_, _) => RefreshTags();
        _instructions.TextChanged += (_, _) => RefreshTags();
        RefreshTags();
        Activated += (_, _) => _title.Focus(FocusState.Programmatic);
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Background = Brush(255, 2, 8, 13) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildTitleBar());

        var frame = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        frame.Children.Add(_chrome);
        var content = new Grid { Margin = new Thickness(28, 25, 32, 28), RowSpacing = 13 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = "NEW TASK // INSTRUCTION NODE", FontSize = 22, CharacterSpacing = 60,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Cyan()
        });
        heading.Children.Add(new TextBlock
        {
            Text = "DEFINE THE TASK ONCE // DAGNUE KEEPS ITS SCHEDULE, RESULT, AND AI CONTEXT",
            FontSize = 8, CharacterSpacing = 55, Foreground = Muted()
        });
        content.Children.Add(heading);

        var identity = new Grid { ColumnSpacing = 12 };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identity.Children.Add(Labeled("TASK SIGNAL", _title));
        Add(identity, Labeled("AI INSTRUCTIONS", _instructions), 1);
        Grid.SetRow(identity, 1); content.Children.Add(identity);

        var tagRouter = new StackPanel { Spacing = 5 };
        tagRouter.Children.Add(new TextBlock { Text = "DAGNUE SUGGESTED ROUTE // SELECT SYSTEMS TO CHECK FIRST", FontSize = 7, CharacterSpacing = 60, Foreground = Muted() });
        var tagScroll = new ScrollViewer { HorizontalScrollMode = ScrollMode.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _tagChips };
        tagRouter.Children.Add(tagScroll); Grid.SetRow(tagRouter, 2); content.Children.Add(tagRouter);

        var modes = Columns(4);
        Add(modes, Labeled("TASK TYPE", _type), 0);
        Add(modes, Labeled("EXECUTION", _execution), 1);
        Add(modes, Labeled("URGENCY", _urgency), 2);
        Add(modes, Labeled("REPEAT CYCLE", _recurrence), 3);
        Grid.SetRow(modes, 3); content.Children.Add(modes);

        var timing = new Grid { ColumnSpacing = 12 };
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        timing.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var flags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        flags.Children.Add(_today); flags.Children.Add(_scheduled);
        Add(timing, Labeled("ACTIVE SIGNALS", flags), 0);
        Add(timing, Labeled("SCHEDULE DATE", _date), 1);
        Add(timing, Labeled("TIME // ±15 MIN", _time), 2);
        Add(timing, Labeled("DEADLINE", _deadline), 3);
        Grid.SetRow(timing, 4); content.Children.Add(timing);

        var footer = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_feedback);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        actions.Children.Add(_cancel); actions.Children.Add(_create);
        Grid.SetColumn(actions, 1); footer.Children.Add(actions);
        Grid.SetRow(footer, 5); content.Children.Add(footer);
        frame.Children.Add(content);
        Grid.SetRow(frame, 1); root.Children.Add(frame);
        return root;
    }

    private async Task CreateAsync()
    {
        if (_saving || string.IsNullOrWhiteSpace(_title.Text))
        {
            _feedback.Text = "TASK SIGNAL REQUIRED // Enter what needs to be done.";
            return;
        }
        _saving = true;
        _create.IsEnabled = _cancel.IsEnabled = false;
        try
        {
            var hasSchedule = _scheduled.IsChecked == true && _date.Date is not null && _time.SelectedTime is not null;
            var due = DateTimeOffset.MaxValue;
            if (hasSchedule)
            {
                var date = _date.Date!.Value;
                var time = _time.SelectedTime!.Value;
                due = new DateTimeOffset(date.Year, date.Month, date.Day, time.Hours, time.Minutes, 0,
                    DateTimeOffset.Now.Offset);
            }
            var deadline = _deadline.Date is { } value
                ? new DateTimeOffset(value.Year, value.Month, value.Day, 23, 59, 0, DateTimeOffset.Now.Offset)
                : (DateTimeOffset?)null;
            var taskType = Selected(_type, UserTaskType.OneTime);
            var recurrence = taskType == UserTaskType.Recurring
                ? Selected(_recurrence, RecurrenceKind.Daily) : RecurrenceKind.None;
            if (taskType == UserTaskType.Recurring && recurrence == RecurrenceKind.None)
                recurrence = RecurrenceKind.Daily;
            var reminder = new Reminder(Guid.NewGuid().ToString("N"), _title.Text.Trim(), null, due,
                recurrence, hasSchedule, DateTimeOffset.Now, Status: TaskProgressStatus.NotYet,
                Urgency: Selected(_urgency, TaskUrgency.Normal), IsToday: _today.IsChecked == true,
                HasSchedule: hasSchedule, Deadline: deadline, AiInstruction: Blank(_instructions.Text),
                TaskType: taskType, ExecutionMode: Selected(_execution, TaskExecutionMode.Manual), Tags: _selectedTags);
            await App.Services.Repository.SaveReminderAsync(reminder);
            App.Services.Notifications.Show("Task added",
                reminder.IsToday ? $"{reminder.Title} · TODAY" : reminder.Title);
            TaskCreated?.Invoke(reminder);
            Close();
        }
        catch (Exception ex)
        {
            _feedback.Text = $"CREATE FAILED // {ex.Message}";
            App.Services.LogCrash(ex);
            _saving = false;
            _create.IsEnabled = _cancel.IsEnabled = true;
        }
    }

    private Grid BuildTitleBar()
    {
        var bar = new Grid { Height = 38, Background = Brush(245, 3, 12, 18) };
        bar.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Brush(110, 0, 229, 255) });
        bar.Children.Add(new TextBlock
        {
            Text = "◇  DAGNUE // NEW TASK COMMAND INTERFACE", FontSize = 9, CharacterSpacing = 110,
            Margin = new Thickness(15, 0, 145, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Brush(200, 0, 229, 255), IsHitTestVisible = false
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(CaptionButton("—", () => _presenter.Minimize(), false));
        buttons.Children.Add(CaptionButton("◇", () =>
        {
            if (_presenter.State == OverlappedPresenterState.Maximized) _presenter.Restore();
            else _presenter.Maximize();
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
            Content = text, Width = 46, Height = 37, Padding = new Thickness(0),
            Background = Brush(0, 0, 0, 0), BorderThickness = new Thickness(0),
            Foreground = danger ? Brush(255, 255, 85, 103) : Brush(200, 0, 229, 255)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBox Field(string placeholder, bool multiline = false) => new()
    {
        PlaceholderText = placeholder, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
        MinHeight = multiline ? 82 : 42, Style = Application.Current.Resources["AlienTextBoxStyle"] as Style
    };

    private static Button Action(string text) => new()
    {
        Content = text, MinWidth = 150, Height = 42,
        Style = Application.Current.Resources["AlienActionButtonStyle"] as Style
    };

    private static AlienCycleSelector Choice<T>(params (string Label, T Value)[] values) where T : Enum
    {
        var selector = new AlienCycleSelector();
        selector.SetOptions(values.Select(item => (item.Label, (object)item.Value)).ToArray());
        return selector;
    }

    private static T Selected<T>(AlienCycleSelector selector, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(selector.SelectedValue, out var value) ? value : fallback;

    private static void Select<T>(AlienCycleSelector selector, T value) where T : Enum => selector.SelectValue(value);
    private static Grid Columns(int count)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        for (var index = 0; index < count; index++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }
    private static void Add(Grid grid, FrameworkElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }
    private static StackPanel Labeled(string label, UIElement element)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 7, CharacterSpacing = 60, Foreground = Muted() });
        panel.Children.Add(element);
        return panel;
    }
    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private void RefreshTags()
    {
        if (!_tagsTouched) _selectedTags = DagnueCapabilityDictionary.SuggestTags($"{_title.Text} {_instructions.Text}");
        _tagChips.Children.Clear();
        foreach (var definition in DagnueCapabilityDictionary.Definitions)
        {
            var chip = new AlienToggleChip { Content = definition.Label, IsChecked = _selectedTags.Contains(definition.Key) };
            ToolTipService.SetToolTip(chip, definition.Description);
            chip.Click += (_, _) =>
            {
                _tagsTouched = true;
                var next = _selectedTags.ToList();
                if (chip.IsChecked == true) { next.RemoveAll(x => x == "GENERAL"); if (!next.Contains(definition.Key)) next.Add(definition.Key); }
                else if (next.Count > 1) next.Remove(definition.Key);
                _selectedTags = DagnueCapabilityDictionary.NormalizeTags(next);
                if (_selectedTags.Count == 0) _selectedTags = ["GENERAL"];
                RefreshTags();
            };
            _tagChips.Children.Add(chip);
        }
    }
    private static SolidColorBrush Cyan() => Brush(255, 0, 229, 255);
    private static SolidColorBrush Muted() => Brush(255, 120, 144, 156);
    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));
}
