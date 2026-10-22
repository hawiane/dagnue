using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace Nexus.App;

public sealed class TaskWorkspaceWindow : Window
{
    private const string TaskModel = "gpt-5.6-sol";
    private static readonly SemaphoreSlim AiGate = new(1, 1);
    private readonly SemaphoreSlim _formStateGate = new(1, 1);
    private readonly OverlappedPresenter _presenter;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly nint _windowHandle;
    private TaskWorkspace _workspace;
    private readonly StackPanel _formHost = new() { Spacing = 10 };
    private readonly StackPanel _historyHost = new() { Spacing = 8 };
    private readonly TextBlock _status = new() { Text = "TASK AI // READY", FontSize = 11 };
    private readonly TextBlock _detail = new() { Text = "This mini app owns an isolated Codex session.", FontSize = 9, TextWrapping = TextWrapping.Wrap };
    private readonly ProgressRing _progress = new() { Width = 28, Height = 28 };
    private readonly TextBox _prompt = new() { PlaceholderText = "Command this mini app AI, or paste an image with Ctrl+V..." };
    private readonly Button _send = new() { Content = "TRANSMIT // TASK AI", MinWidth = 178 };
    private readonly Button _removeImage = new() { Content = "REMOVE", MinWidth = 82 };
    private readonly Button _undo = new() { Content = "UNDO", MinWidth = 72 };
    private readonly Button _redo = new() { Content = "REDO", MinWidth = 72 };
    private readonly Button _resetConversation = new() { Content = "RESET CONVERSATION", MinWidth = 150 };
    private readonly Image _imagePreview = new() { Height = 125, MaxWidth = 260, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _imageLabel = new() { FontSize = 9, TextWrapping = TextWrapping.Wrap };
    private readonly Border _imagePanel = new() { Visibility = Visibility.Collapsed, Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 8), BorderThickness = new Thickness(1) };
    private readonly StringBuilder _stream = new();
    private string? _pendingImagePath;
    private bool _running;

    public event Action<TaskWorkspace>? WorkspaceUpdated;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    public TaskWorkspaceWindow(TaskWorkspace workspace, bool buildImmediately = false)
    {
        _workspace = workspace;
        Title = $"DAGNUE TASK // {workspace.Title}";
        _windowHandle = WindowNative.GetWindowHandle(this);
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ApplyAlienControlStyles();
        Content = BuildLayout();
        AppWindow.Resize(new SizeInt32(1080, 780));
        ConfigureNonClientRegions();
        AppWindow.Changed += TaskWindow_Changed;
        _send.Click += async (_, _) => await RunAsync(_prompt.Text.Trim());
        _removeImage.Click += (_, _) => ClearPendingImage();
        _undo.Click += async (_, _) => await UndoAsync();
        _redo.Click += async (_, _) => await RedoAsync();
        _resetConversation.Click += async (_, _) => await ResetConversationAsync();
        _prompt.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.V &&
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            {
                args.Handled = true;
                await PasteImageFromClipboardAsync();
                return;
            }
            if (args.Key != VirtualKey.Enter) return;
            args.Handled = true;
            await RunAsync(_prompt.Text.Trim());
        };
        App.Services.Codex.StreamEvent += Codex_StreamEvent;
        Closed += (_, _) =>
        {
            App.Services.Codex.StreamEvent -= Codex_StreamEvent;
            AppWindow.Changed -= TaskWindow_Changed;
        };
        RenderForm();
        Activated += LoadHistoryOnFirstActivation;
        if (buildImmediately || workspace.ThreadId is null)
            Activated += BuildOnFirstActivation;
    }

    private async void LoadHistoryOnFirstActivation(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        Activated -= LoadHistoryOnFirstActivation;
        await RefreshCommandHistoryAsync();
    }

    private async void BuildOnFirstActivation(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        Activated -= BuildOnFirstActivation;
        await RunAsync("Build the initial mini app now. Add useful controls and clear status information.");
    }

    private UIElement BuildLayout()
    {
        var windowRoot = new Grid { Background = new SolidColorBrush(Color.FromArgb(255, 3, 8, 13)) };
        windowRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        windowRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        windowRoot.Children.Add(BuildAlienTitleBar());

        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(245, 4, 11, 16)), Padding = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid();
        var identity = new StackPanel { Spacing = 3 };
        identity.Children.Add(new TextBlock { Text = _workspace.Title.ToUpperInvariant(), FontSize = 23, Foreground = Cyan(), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        identity.Children.Add(new TextBlock { Text = $"AUTONOMOUS MINI SYSTEM // WORKSPACE V{_workspace.FormContractVersion} // {TaskModel.ToUpperInvariant()} // APP.md PERSISTENT", FontSize = 9, Foreground = Muted() });
        header.Children.Add(identity);
        var historyActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        historyActions.Children.Add(_undo); historyActions.Children.Add(_redo); historyActions.Children.Add(_resetConversation);
        header.Children.Add(historyActions);
        root.Children.Add(header);

        var monitor = new Border { Margin = new Thickness(0, 18, 0, 14), Padding = new Thickness(12), BorderThickness = new Thickness(1), BorderBrush = Cyan(110), Background = new SolidColorBrush(Color.FromArgb(150, 7, 21, 29)) };
        var monitorGrid = new Grid { ColumnSpacing = 12 };
        monitorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        monitorGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        monitorGrid.Children.Add(_progress);
        var monitorText = new StackPanel { Spacing = 3 };
        monitorText.Children.Add(_status); monitorText.Children.Add(_detail);
        Grid.SetColumn(monitorText, 1); monitorGrid.Children.Add(monitorText); monitor.Child = monitorGrid;
        Grid.SetRow(monitor, 1); root.Children.Add(monitor);

        var workspaceBody = new Grid { ColumnSpacing = 16 };
        workspaceBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        workspaceBody.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
        workspaceBody.Children.Add(new ScrollViewer { Content = _formHost });
        var history = BuildCommandHistoryPanel();
        Grid.SetColumn(history, 1); workspaceBody.Children.Add(history);
        Grid.SetRow(workspaceBody, 2); root.Children.Add(workspaceBody);

        _imagePanel.BorderBrush = Cyan(110);
        _imagePanel.Background = new SolidColorBrush(Color.FromArgb(150, 7, 21, 29));
        var attachment = new Grid { ColumnSpacing = 12 };
        attachment.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        attachment.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        attachment.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        attachment.Children.Add(_imagePreview);
        var attachmentText = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        attachmentText.Children.Add(new TextBlock { Text = "VISUAL CONTEXT // ATTACHED TO THIS TASK ONLY", FontSize = 10, Foreground = Cyan() });
        attachmentText.Children.Add(_imageLabel);
        Grid.SetColumn(attachmentText, 1); attachment.Children.Add(attachmentText);
        Grid.SetColumn(_removeImage, 2); attachment.Children.Add(_removeImage);
        _imagePanel.Child = attachment;

        var command = new Grid { ColumnSpacing = 12 };
        command.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        command.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        command.Children.Add(BuildCommandTerminal());
        Grid.SetColumn(_send, 1); command.Children.Add(_send);
        var composer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        composer.Children.Add(_imagePanel);
        composer.Children.Add(command);
        Grid.SetRow(composer, 3); root.Children.Add(composer);
        Grid.SetRow(root, 1); windowRoot.Children.Add(root);
        return windowRoot;
    }

    private Border BuildCommandHistoryPanel()
    {
        var panel = new Border
        {
            Padding = new Thickness(13), BorderThickness = new Thickness(1),
            BorderBrush = Cyan(72), Background = new SolidColorBrush(Color.FromArgb(145, 5, 17, 24))
        };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 0, 10) };
        heading.Children.Add(new TextBlock { Text = "COMMAND MEMORY", FontSize = 12, CharacterSpacing = 90, Foreground = Cyan() });
        heading.Children.Add(new TextBlock { Text = "PERSISTENT // TASK-EXCLUSIVE TIMELINE", FontSize = 7, Foreground = Muted() });
        grid.Children.Add(heading);
        var scroll = new ScrollViewer { Content = _historyHost };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        panel.Child = grid;
        return panel;
    }

    private async Task RefreshCommandHistoryAsync()
    {
        _historyHost.Children.Clear();
        var commands = await App.Services.Repository.GetTaskCommandsAsync(_workspace.Id);
        if (commands.Count == 0)
        {
            _undo.IsEnabled = _redo.IsEnabled = false;
            _historyHost.Children.Add(new TextBlock
            {
                Text = "NO COMMANDS RECORDED", FontSize = 8, Foreground = Muted(),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0)
            });
            return;
        }

        foreach (var command in commands)
        {
            var error = string.Equals(command.Status, "error", StringComparison.OrdinalIgnoreCase);
            var running = string.Equals(command.Status, "running", StringComparison.OrdinalIgnoreCase);
            var statusColor = error
                ? new SolidColorBrush(Color.FromArgb(255, 255, 62, 79))
                : running ? new SolidColorBrush(Color.FromArgb(255, 255, 176, 32)) : Cyan();
            var card = new Border
            {
                Padding = new Thickness(10), BorderThickness = new Thickness(1, 0, 0, 0),
                BorderBrush = statusColor, Background = new SolidColorBrush(Color.FromArgb(105, 11, 28, 37))
            };
            var content = new StackPanel { Spacing = 5 };
            var meta = new Grid();
            meta.Children.Add(new TextBlock
            {
                Text = command.SubmittedAt.ToLocalTime().ToString("MMM dd  HH:mm:ss").ToUpperInvariant(),
                FontSize = 7, Foreground = Muted()
            });
            meta.Children.Add(new TextBlock
            {
                Text = (command.IsUndone ? "UNDONE" : command.Status).ToUpperInvariant(), FontSize = 7, Foreground = statusColor,
                HorizontalAlignment = HorizontalAlignment.Right
            });
            content.Children.Add(meta);
            content.Children.Add(new TextBlock
            {
                Text = command.Command, FontSize = 10, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromArgb(238, 229, 247, 251))
            });
            if (!string.IsNullOrWhiteSpace(command.AttachmentPath))
                content.Children.Add(new TextBlock { Text = "◇ IMAGE ATTACHED", FontSize = 7, Foreground = Cyan(190) });
            if (!string.IsNullOrWhiteSpace(command.ResultSummary))
                content.Children.Add(new TextBlock
                {
                    Text = command.ResultSummary, FontSize = 8, TextWrapping = TextWrapping.Wrap,
                    Foreground = error ? statusColor : Muted(), MaxHeight = 48
                });
            card.Child = content;
            _historyHost.Children.Add(card);
        }
        _undo.IsEnabled = commands.Any(x => x.Status == "completed" && !x.IsUndone && !string.IsNullOrWhiteSpace(x.BeforeSnapshotPath));
        _redo.IsEnabled = commands.Any(x => x.Status == "completed" && x.IsUndone && !x.RedoInvalidated && !string.IsNullOrWhiteSpace(x.AfterSnapshotPath));
    }

    private Grid BuildAlienTitleBar()
    {
        var bar = new Grid { Height = 38, Background = new SolidColorBrush(Color.FromArgb(242, 3, 11, 17)) };
        var signalCanvas = new Canvas { IsHitTestVisible = false };
        signalCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 0, Y1 = 37, X2 = 1800, Y2 = 37,
            Stroke = Cyan(65), StrokeThickness = 1
        });
        signalCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 17, Y1 = 9, X2 = 82, Y2 = 9,
            Stroke = Cyan(105), StrokeThickness = 1
        });
        signalCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 82, Y1 = 9, X2 = 94, Y2 = 21,
            Stroke = Cyan(105), StrokeThickness = 1
        });
        var node = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 4, Height = 4, Fill = Cyan(205) };
        Canvas.SetLeft(node, 101); Canvas.SetTop(node, 19); signalCanvas.Children.Add(node);
        bar.Children.Add(signalCanvas);

        var identity = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 9, Margin = new Thickness(16, 0, 150, 0),
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
        };
        identity.Children.Add(new TextBlock { Text = "◇", FontSize = 11, Foreground = Cyan(225) });
        identity.Children.Add(new TextBlock
        {
            Text = $"DAGNUE // TASK MATRIX // {_workspace.Title.ToUpperInvariant()}", FontSize = 8,
            CharacterSpacing = 105, Foreground = new SolidColorBrush(Color.FromArgb(170, 126, 158, 169))
        });
        bar.Children.Add(identity);

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        controls.Children.Add(BuildCaptionButton("—", () => _presenter.Minimize(), false));
        controls.Children.Add(BuildCaptionButton("◇", ToggleMaximize, false));
        controls.Children.Add(BuildCaptionButton("×", Close, true));
        bar.Children.Add(controls);
        return bar;
    }

    private Button BuildCaptionButton(string glyph, Action action, bool danger)
    {
        var foreground = danger ? new SolidColorBrush(Color.FromArgb(225, 255, 80, 101)) : Cyan(190);
        var button = new Button
        {
            Content = glyph, Width = 46, Height = 37, Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0),
            Foreground = foreground, FontSize = 14
        };
        button.Click += (_, _) => action();
        button.PointerEntered += (_, _) => button.Background = new SolidColorBrush(danger
            ? Color.FromArgb(75, 255, 48, 70) : Color.FromArgb(65, 0, 229, 255));
        button.PointerExited += (_, _) => button.Background = new SolidColorBrush(Colors.Transparent);
        return button;
    }

    private void ToggleMaximize()
    {
        if (_presenter.State == OverlappedPresenterState.Maximized) _presenter.Restore();
        else _presenter.Maximize();
    }

    private void TaskWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange) ConfigureNonClientRegions();
    }

    private void ConfigureNonClientRegions()
    {
        var scale = Math.Max(1.0, GetDpiForWindow(_windowHandle) / 96.0);
        var titleHeight = (int)Math.Ceiling(38 * scale);
        var controlsWidth = (int)Math.Ceiling(138 * scale);
        var captionWidth = Math.Max(1, AppWindow.Size.Width - controlsWidth);
        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Caption,
            [new RectInt32(0, 0, captionWidth, titleHeight)]);
        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough,
            [new RectInt32(captionWidth, 0, controlsWidth, titleHeight)]);
    }

    private void ApplyAlienControlStyles()
    {
        if (Application.Current.Resources["AlienTextBoxStyle"] is Style textStyle) _prompt.Style = textStyle;
        if (Application.Current.Resources["AlienActionButtonStyle"] is Style buttonStyle)
        {
            _send.Style = buttonStyle;
            _removeImage.Style = buttonStyle;
            _undo.Style = buttonStyle;
            _redo.Style = buttonStyle;
            _resetConversation.Style = buttonStyle;
        }
        _prompt.Background = new SolidColorBrush(Colors.Transparent);
        _prompt.BorderThickness = new Thickness(0);
        _prompt.FontSize = 13;
        _prompt.CharacterSpacing = 35;
        _prompt.Padding = new Thickness(18, 13, 44, 10);
        _prompt.MinHeight = 52;
    }

    private Grid BuildCommandTerminal()
    {
        var shell = new Grid { MinHeight = 56 };
        shell.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 7, 19, 27)),
            BorderBrush = Cyan(82),
            BorderThickness = new Thickness(1)
        });
        shell.Children.Add(_prompt);

        shell.Children.Add(new Border
        {
            Width = 68, Height = 2, Background = Cyan(230),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        shell.Children.Add(new Border
        {
            Width = 2, Height = 24, Background = Cyan(215),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
        });
        shell.Children.Add(new Border
        {
            Width = 96, Height = 2, Background = Cyan(210),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        });
        shell.Children.Add(new Border
        {
            Width = 2, Height = 18, Background = Cyan(175),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom
        });

        var signal = new Grid
        {
            Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        signal.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Stroke = Cyan(105), StrokeThickness = 1 });
        signal.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 7, Height = 7, Fill = Cyan(235),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        signal.Children.Add(new Border
        {
            Width = 16, Height = 1, Background = Cyan(130),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        signal.Children.Add(new Border
        {
            Width = 1, Height = 16, Background = Cyan(130),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        shell.Children.Add(signal);
        return shell;
    }

    private async Task PasteImageFromClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Bitmap))
            {
                SetStatus("IMAGE REQUIRED", "The clipboard does not currently contain an image.", true);
                return;
            }

            var source = await content.GetBitmapAsync();
            var directory = GetTaskDirectory();
            Directory.CreateDirectory(directory);
            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            var file = await folder.CreateFileAsync($"visual-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.png", CreationCollisionOption.GenerateUniqueName);
            using (var input = await source.OpenReadAsync())
            using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                await RandomAccessStream.CopyAsync(input, output);
                await output.FlushAsync();
            }

            var bitmap = new BitmapImage();
            using (var preview = await file.OpenAsync(FileAccessMode.Read)) bitmap.SetSource(preview);
            _pendingImagePath = file.Path;
            _imagePreview.Source = bitmap;
            _imageLabel.Text = file.Name;
            _imagePanel.Visibility = Visibility.Visible;
            SetStatus("IMAGE READY", "The pasted image will be inspected with the next task-AI command.", false);
        }
        catch (Exception ex)
        {
            SetStatus("IMAGE ERROR", ex.Message, true);
        }
    }

    private void ClearPendingImage()
    {
        _pendingImagePath = null;
        _imagePreview.Source = null;
        _imageLabel.Text = string.Empty;
        _imagePanel.Visibility = Visibility.Collapsed;
    }

    private void RenderForm()
    {
        _formHost.Children.Clear();
        try
        {
            using var document = JsonDocument.Parse(_workspace.FormJson);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var message))
                _formHost.Children.Add(new TextBlock { Text = message.GetString() ?? "", TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Cyan() });
            if (!root.TryGetProperty("controls", out var controls) || controls.ValueKind != JsonValueKind.Array) return;
            var index = 0;
            foreach (var item in controls.EnumerateArray().Take(30)) AddControl(item, index++);
        }
        catch (JsonException) { SetStatus("ERROR", "Saved mini-app schema is invalid.", true); }
    }

    private void AddControl(JsonElement item, int index)
    {
        var type = item.TryGetProperty("type", out var typeNode) ? typeNode.GetString()?.ToLowerInvariant() : "text";
        var label = item.TryGetProperty("label", out var labelNode) ? labelNode.GetString() ?? "" : "";
        var id = item.TryGetProperty("id", out var idNode) && !string.IsNullOrWhiteSpace(idNode.GetString())
            ? idNode.GetString()!
            : $"control-{index}";
        switch (type)
        {
            case "input":
                var input = new TextBox
                {
                    Header = label, PlaceholderText = label, Tag = id,
                    Text = item.TryGetProperty("value", out var valueNode) ? valueNode.GetString() ?? "" : ""
                };
                input.LostFocus += async (_, _) => await PersistControlStateAsync(id, "value", JsonValue.Create(input.Text));
                _formHost.Children.Add(input);
                break;
            case "checkbox":
                var checkbox = new CheckBox
                {
                    Content = label, FontSize = 15, Tag = id,
                    IsChecked = item.TryGetProperty("checked", out var checkedNode) && checkedNode.ValueKind == JsonValueKind.True
                };
                checkbox.Checked += async (_, _) => await PersistControlStateAsync(id, "checked", JsonValue.Create(true));
                checkbox.Unchecked += async (_, _) => await PersistControlStateAsync(id, "checked", JsonValue.Create(false));
                _formHost.Children.Add(checkbox);
                break;
            case "button":
                var action = item.TryGetProperty("action", out var actionNode) ? actionNode.GetString() : label;
                var button = new Button { Content = label, Tag = action, HorizontalAlignment = HorizontalAlignment.Left };
                if (Application.Current.Resources["AlienActionButtonStyle"] is Style buttonStyle) button.Style = buttonStyle;
                button.Click += async (_, _) => await RunAsync($"User activated form action: {button.Tag}");
                _formHost.Children.Add(button); break;
            case "status": _formHost.Children.Add(new Border { Padding = new Thickness(12), BorderThickness = new Thickness(1), BorderBrush = Cyan(100), Child = new TextBlock { Text = label, FontSize = 15, TextWrapping = TextWrapping.Wrap } }); break;
            default: _formHost.Children.Add(new TextBlock { Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap }); break;
        }
    }

    private async Task RunAsync(string request)
    {
        if (_running || (string.IsNullOrWhiteSpace(request) && _pendingImagePath is null)) return;
        if (string.IsNullOrWhiteSpace(request)) request = "Inspect this image and update the mini app appropriately.";
        _running = true; _send.IsEnabled = _prompt.IsEnabled = false; _stream.Clear(); _prompt.Text = "";
        try
        {
            await CaptureVisibleControlStateAsync();
            SetStatus("WORKING", "Mini Codex is reading APP.md and executing inside this workspace...", false, true);
            var images = _pendingImagePath is null ? null : new[] { _pendingImagePath };
            var result = await App.Services.MiniApps.ExecuteAsync(_workspace, request, images);
            _workspace = result.Workspace;
            RenderForm(); ClearPendingImage(); SetStatus("READY", result.Command.ResultSummary ?? "Command completed.", false);
            await RefreshCommandHistoryAsync();
            WorkspaceUpdated?.Invoke(_workspace);
        }
        catch (Exception ex)
        {
            await RefreshCommandHistoryAsync();
            SetStatus("ERROR", ex.Message, true);
        }
        finally
        {
            _running = false; _send.IsEnabled = _prompt.IsEnabled = true;
        }
    }

    private async Task UndoAsync()
    {
        if (_running) return;
        var updated = await App.Services.MiniApps.UndoAsync(_workspace);
        if (updated is null) { SetStatus("UNDO", "Nothing to undo.", false); return; }
        _workspace = updated; RenderForm(); await RefreshCommandHistoryAsync(); WorkspaceUpdated?.Invoke(_workspace);
        SetStatus("UNDONE", "The previous command's files, form, APP.md, and schedules were restored.", false);
    }

    private async Task RedoAsync()
    {
        if (_running) return;
        var updated = await App.Services.MiniApps.RedoAsync(_workspace);
        if (updated is null) { SetStatus("REDO", "Nothing to redo.", false); return; }
        _workspace = updated; RenderForm(); await RefreshCommandHistoryAsync(); WorkspaceUpdated?.Invoke(_workspace);
        SetStatus("REDONE", "The command's resulting files, form, APP.md, and schedules were restored.", false);
    }

    private async Task ResetConversationAsync()
    {
        if (_running) return;
        var dialog = new ContentDialog
        {
            Title = "Reset this mini-system conversation?",
            Content = "This permanently deletes its Codex conversation and command timeline. APP.md, files, form state, and schedules remain available to the fresh AI session.",
            PrimaryButtonText = "RESET", CloseButtonText = "CANCEL", DefaultButton = ContentDialogButton.Close,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _workspace = await App.Services.MiniApps.ResetConversationAsync(_workspace, clearCommandHistory: true);
        await RefreshCommandHistoryAsync(); WorkspaceUpdated?.Invoke(_workspace);
        SetStatus("CONVERSATION RESET", "The next command starts fresh and reads the current APP.md first.", false);
    }

    private async Task<string> StartFreshThreadAsync()
    {
        var directory = GetTaskDirectory();
        Directory.CreateDirectory(directory);
        return await App.Services.Codex.StartThreadAsync(model: TaskModel, cwd: directory, workspaceWrite: true);
    }

    private async Task CaptureVisibleControlStateAsync()
    {
        foreach (var child in _formHost.Children)
        {
            if (child is CheckBox checkbox && checkbox.Tag is string checkboxId)
                await PersistControlStateAsync(checkboxId, "checked", JsonValue.Create(checkbox.IsChecked == true), notify: false);
            else if (child is TextBox input && input.Tag is string inputId)
                await PersistControlStateAsync(inputId, "value", JsonValue.Create(input.Text), notify: false);
        }
    }

    private async Task PersistControlStateAsync(string id, string property, JsonNode? value, bool notify = true)
    {
        await _formStateGate.WaitAsync();
        try
        {
            var root = JsonNode.Parse(_workspace.FormJson) as JsonObject;
            var controls = root?["controls"] as JsonArray;
            if (root is null || controls is null) return;

            var changed = false;
            for (var index = 0; index < controls.Count; index++)
            {
                if (controls[index] is not JsonObject control) continue;
                var controlId = control["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(controlId))
                {
                    controlId = $"control-{index}";
                    control["id"] = controlId;
                    changed = true;
                }
                if (!string.Equals(controlId, id, StringComparison.Ordinal)) continue;
                control[property] = value?.DeepClone();
                changed = true;
                break;
            }
            if (!changed) return;

            _workspace = _workspace with
            {
                FormJson = root.ToJsonString(), FormContractVersion = 3, UpdatedAt = DateTimeOffset.Now
            };
            await App.Services.Repository.SaveTaskWorkspaceAsync(_workspace);
            if (notify) WorkspaceUpdated?.Invoke(_workspace);
        }
        finally { _formStateGate.Release(); }
    }

    private static string NormalizeFormSchema(string schema, string currentSchema)
    {
        var previous = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(currentSchema) is JsonObject oldRoot && oldRoot["controls"] is JsonArray oldControls)
            {
                for (var index = 0; index < oldControls.Count; index++)
                {
                    if (oldControls[index] is not JsonObject oldControl) continue;
                    var oldId = oldControl["id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(oldId)) oldId = $"control-{index}";
                    previous[oldId] = oldControl;
                }
            }
        }
        catch (JsonException) { }

        var root = JsonNode.Parse(schema) as JsonObject ?? throw new InvalidOperationException("Task AI returned an invalid form schema.");
        var controls = root["controls"] as JsonArray ?? throw new InvalidOperationException("Task AI returned an invalid form schema.");
        for (var index = 0; index < controls.Count; index++)
        {
            if (controls[index] is not JsonObject control) continue;
            var id = control["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) id = $"control-{index}";
            control["id"] = id;
            previous.TryGetValue(id, out var prior);
            var type = control["type"]?.GetValue<string>()?.ToLowerInvariant();
            if (type == "checkbox" && control["checked"] is null)
                control["checked"] = prior?["checked"]?.DeepClone() ?? JsonValue.Create(false);
            if (type == "input" && control["value"] is null)
                control["value"] = prior?["value"]?.DeepClone() ?? JsonValue.Create(string.Empty);
        }
        return root.ToJsonString();
    }

    private string GetTaskDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS", "task-workspaces", _workspace.Id);

    private void Codex_StreamEvent(object? sender, CodexStreamEvent e)
    {
        if (!_running || string.IsNullOrEmpty(e.Text)) return;
        DispatcherQueue.TryEnqueue(() => { _stream.Append(e.Text); var value = _stream.ToString().Replace('\r', ' ').Replace('\n', ' '); _detail.Text = value.Length > 260 ? "…" + value[^259..] : value; });
    }

    private void SetStatus(string state, string detail, bool error, bool active = false)
    {
        _progress.IsActive = active; _status.Text = $"TASK AI // {state}"; _status.Foreground = error ? new SolidColorBrush(Color.FromArgb(255, 255, 48, 58)) : Cyan(); _detail.Text = detail;
    }

    private static string ExtractJson(string text) { var a = text.IndexOf('{'); var b = text.LastIndexOf('}'); if (a < 0 || b <= a) throw new InvalidOperationException("Task AI did not return a form."); return text[a..(b + 1)]; }
    private static string GetFormMessage(string schema)
    {
        try
        {
            using var document = JsonDocument.Parse(schema);
            return document.RootElement.TryGetProperty("message", out var message)
                ? message.GetString() ?? "Mini app updated."
                : "Mini app updated.";
        }
        catch (JsonException) { return "Mini app updated."; }
    }
    private static SolidColorBrush Cyan(byte alpha = 255) => new(Color.FromArgb(alpha, 0, 229, 255));
    private static SolidColorBrush Muted() => new(Color.FromArgb(170, 145, 171, 181));
}
