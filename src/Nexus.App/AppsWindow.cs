using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Nexus.Core.Models;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI;
using WinRT.Interop;

namespace Nexus.App;

public sealed class AppsWindow : Window
{
    private readonly AppWindow _appWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly InputNonClientPointerSource _nonClient;
    private readonly nint _handle;
    private readonly ListView _runningList = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView _installedList = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _runningStatus = StatusText("SCANNING ACTIVE WINDOWS...");
    private readonly TextBlock _installedStatus = StatusText("SCANNING INSTALLED APPLICATIONS...");
    private readonly TextBox _installedSearch = new() { PlaceholderText = "Filter installed apps...", MinHeight = 40 };
    private readonly ProgressRing _runningProgress = new() { Width = 20, Height = 20 };
    private readonly ProgressRing _installedProgress = new() { Width = 20, Height = 20 };
    private readonly Grid _runningTabHost = new();
    private readonly Grid _installedTabHost = new();
    private readonly Button _runningTabButton = new();
    private readonly Button _installedTabButton = new();
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _runningRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _installedRefreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<(Button Button, string Label)> _runningHeaders = [];
    private readonly List<(Button Button, string Label)> _installedHeaders = [];
    private ObservableCollection<RunningRow> _runningRows = [];
    private IReadOnlyList<InstalledApplicationInfo> _installed = [];
    private int _runningSortColumn;
    private int _installedSortColumn;
    private bool _runningSortAscending = true;
    private bool _installedSortAscending = true;
    private bool _installedLoaded;
    private bool _initialLoadStarted;
    private bool _closed;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    public AppsWindow()
    {
        Title = "DAGNUE APPS // APPLICATION CONTROL MATRIX";
        _handle = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow;
        _presenter = (OverlappedPresenter)_appWindow.Presenter;
        _nonClient = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        SystemBackdrop = new MicaBackdrop();
        _runningList.ItemTemplate = RunningTemplate();
        _installedList.ItemTemplate = InstalledTemplate();
        _installedSearch.TextChanged += InstalledSearch_TextChanged;
        Content = BuildLayout();
        _appWindow.Resize(new SizeInt32(1240, 800));
        UpdateCaptionRegions();
        _appWindow.Changed += (_, args) => { if (args.DidSizeChange) UpdateCaptionRegions(); };
        Activated += async (_, _) =>
        {
            if (_initialLoadStarted) return;
            _initialLoadStarted = true;
            await RefreshRunningAsync();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _lifetime.Cancel();
            _iconCache.Clear();
        };
    }

    private UIElement BuildLayout()
    {
        if (Application.Current.Resources["AlienTextBoxStyle"] is Style textStyle) _installedSearch.Style = textStyle;
        var root = new Grid { Background = Brush(255, 2, 8, 13) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildTitleBar());

        var body = new Grid { Padding = new Thickness(25, 19, 25, 20), RowSpacing = 12 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = "APPS // APPLICATION CONTROL MATRIX", FontSize = 22, CharacterSpacing = 55,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Cyan()
        });
        heading.Children.Add(new TextBlock
        {
            Text = "LIVE PROCESS CONTROL  //  INSTALLED VOLUME  //  AI UPDATE INTELLIGENCE  //  WINDOWS UNINSTALL",
            FontSize = 9, CharacterSpacing = 55, Foreground = Muted()
        });
        body.Children.Add(heading);

        var tabs = BuildAlienTabs();
        Grid.SetRow(tabs, 1); body.Children.Add(tabs);
        Grid.SetRow(body, 1); root.Children.Add(body);
        return root;
    }

    private FrameworkElement BuildAlienTabs()
    {
        var host = new Grid { RowSpacing = 12 };
        host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var selector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        ConfigureTabButton(_runningTabButton, "01  RUNNING APPS // END TASK");
        ConfigureTabButton(_installedTabButton, "02  INSTALLED APPS // VOLUME + UPDATE");
        _runningTabButton.Click += async (_, _) => await SelectTabAsync(running: true);
        _installedTabButton.Click += async (_, _) => await SelectTabAsync(running: false);
        selector.Children.Add(_runningTabButton); selector.Children.Add(_installedTabButton);
        host.Children.Add(selector);
        _runningTabHost.Children.Add(BuildRunningTab());
        _installedTabHost.Children.Add(BuildInstalledTab());
        var content = new Grid(); content.Children.Add(_runningTabHost); content.Children.Add(_installedTabHost);
        Grid.SetRow(content, 1); host.Children.Add(content);
        _ = SelectTabAsync(running: true);
        return host;
    }

    private static void ConfigureTabButton(Button button, string text)
    {
        button.Content = text; button.MinWidth = 270; button.Height = 44;
        button.Style = AlienTabStyle();
    }

    private async Task SelectTabAsync(bool running)
    {
        _runningTabHost.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        _installedTabHost.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        _runningTabButton.Background = Brush(running ? (byte)210 : (byte)90, 4, 33, 43);
        _installedTabButton.Background = Brush(running ? (byte)90 : (byte)210, 4, 33, 43);
        _runningTabButton.Foreground = running ? Cyan() : Muted();
        _installedTabButton.Foreground = running ? Muted() : Cyan();
        if (!running && !_installedLoaded)
        {
            await Task.Yield();
            await RefreshInstalledAsync();
        }
    }

    private UIElement BuildRunningTab()
    {
        var grid = new Grid { RowSpacing = 10, Padding = new Thickness(0, 12, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var refresh = ActionButton("REFRESH LIVE APPS");
        refresh.Click += async (_, _) => await RefreshRunningAsync();
        grid.Children.Add(refresh);
        grid.Children.Add(ColumnHeader(["APP / WINDOW", "PID", "MEMORY", "SAFETY", "ACTION"],
            [520, 85, 110, 260, 140], 1, installed: false));
        var frame = Frame(_runningList); Grid.SetRow(frame, 2); grid.Children.Add(frame);
        var footer = Footer(_runningProgress, _runningStatus); Grid.SetRow(footer, 3); grid.Children.Add(footer);
        return grid;
    }

    private UIElement BuildInstalledTab()
    {
        var grid = new Grid { RowSpacing = 10, Padding = new Thickness(0, 12, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var controls = new Grid { ColumnSpacing = 10 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(_installedSearch);
        var refresh = ActionButton("REFRESH INVENTORY"); refresh.Click += async (_, _) => await RefreshInstalledAsync();
        Grid.SetColumn(refresh, 1); controls.Children.Add(refresh); grid.Children.Add(controls);
        grid.Children.Add(ColumnHeader(["APP / PUBLISHER", "VERSION", "SIZE / VOLUME", "LAST USED", "AI UPDATE", "WINDOWS UNINSTALL"],
            [400, 105, 145, 135, 140, 230], 1, installed: true));
        var frame = Frame(_installedList); Grid.SetRow(frame, 2); grid.Children.Add(frame);
        var footer = Footer(_installedProgress, _installedStatus); Grid.SetRow(footer, 3); grid.Children.Add(footer);
        return grid;
    }

    private async Task RefreshRunningAsync()
    {
        if (_closed) return;
        if (!await _runningRefreshGate.WaitAsync(0)) return;
        _runningProgress.IsActive = true; _runningStatus.Text = "SCANNING ACTIVE WINDOWS...";
        try
        {
            var apps = await App.Services.Applications.GetRunningApplicationsAsync(_lifetime.Token);
            _runningRows = new ObservableCollection<RunningRow>(apps.Select(app => new RunningRow(app,
                new AsyncCommand(_ => EndTaskAsync(app)), app.CanTerminate)));
            ApplyRunningSort();
            _runningList.ItemsSource = _runningRows;
            _runningStatus.Text = $"{_runningRows.Count:N0} USER-VISIBLE APPLICATIONS // ICON STREAM ACTIVE";
            _ = LoadIconsAsync(_runningRows.Select(row => (row, row.Application.ExecutablePath)), _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _runningStatus.Text = $"RUNNING APP SCAN FAILED // {ex.Message}"; }
        finally { _runningProgress.IsActive = false; _runningRefreshGate.Release(); }
    }

    private async Task RefreshInstalledAsync()
    {
        if (_closed) return;
        if (!await _installedRefreshGate.WaitAsync(0)) return;
        _installedProgress.IsActive = true; _installedStatus.Text = "SCANNING BOTH REGISTRY VIEWS...";
        try
        {
            _installed = await App.Services.Applications.GetInstalledApplicationsAsync(_lifetime.Token);
            _installedLoaded = true;
            ApplyInstalledFilter();
            var rows = (_installedList.ItemsSource as IEnumerable<InstalledRow>)?.ToArray() ?? [];
            var removable = _installed.Count(app => app.CanUninstall);
            _installedStatus.Text = $"{_installed.Count:N0} APPLICATIONS // {removable:N0} AVAILABLE THROUGH WINDOWS UNINSTALL";
            _ = LoadIconsAsync(rows.Select(row => (row, row.Application.IconPath)), _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _installedStatus.Text = $"INVENTORY SCAN FAILED // {ex.Message}"; }
        finally { _installedProgress.IsActive = false; _installedRefreshGate.Release(); }
    }

    private void ApplyInstalledFilter()
    {
        var query = _installedSearch.Text.Trim();
        var filtered = _installed.Where(app => query.Length == 0 || app.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (app.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        filtered = SortInstalled(filtered);
        var rows = filtered.Select(app => new InstalledRow(app,
            new AsyncCommand(_ => CheckUpdateWithAiAsync(app)),
            new AsyncCommand(_ => ConfirmUninstallAsync(app)), app.CanUninstall)).ToArray();
        foreach (var row in rows)
            if (row.Application.IconPath is { } path && _iconCache.TryGetValue(path, out var icon)) row.Icon = icon;
        _installedList.ItemsSource = rows;
    }

    private void SortByHeader(bool installed, int column)
    {
        if (installed)
        {
            if (_installedSortColumn == column) _installedSortAscending = !_installedSortAscending;
            else { _installedSortColumn = column; _installedSortAscending = true; }
            ApplyInstalledFilter();
        }
        else
        {
            if (_runningSortColumn == column) _runningSortAscending = !_runningSortAscending;
            else { _runningSortColumn = column; _runningSortAscending = true; }
            ApplyRunningSort();
            _runningList.ItemsSource = _runningRows;
        }
        UpdateHeaderIndicators(installed);
    }

    private void ApplyRunningSort()
    {
        IEnumerable<RunningRow> rows = (_runningSortColumn, _runningSortAscending) switch
        {
            (0, true) => _runningRows.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase),
            (0, false) => _runningRows.OrderByDescending(row => row.Name, StringComparer.OrdinalIgnoreCase),
            (1, true) => _runningRows.OrderBy(row => row.Application.ProcessId),
            (1, false) => _runningRows.OrderByDescending(row => row.Application.ProcessId),
            (2, true) => _runningRows.OrderBy(row => row.Application.WorkingSetBytes),
            (2, false) => _runningRows.OrderByDescending(row => row.Application.WorkingSetBytes),
            (3, true) => _runningRows.OrderBy(row => row.Safety, StringComparer.OrdinalIgnoreCase),
            (3, false) => _runningRows.OrderByDescending(row => row.Safety, StringComparer.OrdinalIgnoreCase),
            (4, true) => _runningRows.OrderBy(row => row.CanTerminate),
            _ => _runningRows.OrderByDescending(row => row.CanTerminate)
        };
        _runningRows = new ObservableCollection<RunningRow>(rows);
    }

    private IEnumerable<InstalledApplicationInfo> SortInstalled(IEnumerable<InstalledApplicationInfo> apps) =>
        (_installedSortColumn, _installedSortAscending) switch
        {
            (0, true) => apps.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase),
            (0, false) => apps.OrderByDescending(app => app.Name, StringComparer.OrdinalIgnoreCase),
            (1, true) => apps.OrderBy(app => app.Version, StringComparer.OrdinalIgnoreCase),
            (1, false) => apps.OrderByDescending(app => app.Version, StringComparer.OrdinalIgnoreCase),
            (2, true) => apps.OrderBy(app => app.SizeBytes ?? long.MaxValue),
            (2, false) => apps.OrderByDescending(app => app.SizeBytes ?? long.MinValue),
            (3, true) => apps.OrderBy(app => app.LastUsedAt ?? DateTimeOffset.MaxValue),
            (3, false) => apps.OrderByDescending(app => app.LastUsedAt ?? DateTimeOffset.MinValue),
            (4, true) => apps.OrderBy(app => app.Name, StringComparer.OrdinalIgnoreCase),
            (4, false) => apps.OrderByDescending(app => app.Name, StringComparer.OrdinalIgnoreCase),
            (5, true) => apps.OrderBy(app => app.CanUninstall),
            _ => apps.OrderByDescending(app => app.CanUninstall)
        };

    private void UpdateHeaderIndicators(bool installed)
    {
        var headers = installed ? _installedHeaders : _runningHeaders;
        var selected = installed ? _installedSortColumn : _runningSortColumn;
        var ascending = installed ? _installedSortAscending : _runningSortAscending;
        for (var index = 0; index < headers.Count; index++)
            headers[index].Button.Content = headers[index].Label + (index == selected ? (ascending ? "  △" : "  ▽") : string.Empty);
    }

    private async void InstalledSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyInstalledFilter();
        var rows = (_installedList.ItemsSource as IEnumerable<InstalledRow>)?.ToArray() ?? [];
        await LoadIconsAsync(rows.Select(row => (row, row.Application.IconPath)), _lifetime.Token);
    }

    private async Task EndTaskAsync(RunningApplicationInfo application)
    {
        var row = _runningRows.FirstOrDefault(item => item.Application.ProcessId == application.ProcessId);
        if (row is not null) _runningRows.Remove(row);
        _runningStatus.Text = $"ENDING {application.Name.ToUpperInvariant()} ({application.ProcessId})...";
        var result = App.Services.Applications.Terminate(application.ProcessId);
        _runningStatus.Text = result.Message.ToUpperInvariant();
        if (!result.Succeeded) await RefreshRunningAsync();
    }

    private async Task ConfirmUninstallAsync(InstalledApplicationInfo application)
    {
        if (!application.CanUninstall) return;
        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = $"Open Windows uninstaller for {application.Name}?",
            Content = $"Windows or the application's manufacturer will handle removal and show its normal confirmation interface.\n\n{application.SafetyNote}",
            PrimaryButtonText = "OPEN UNINSTALLER", CloseButtonText = "CANCEL",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = App.Services.Applications.LaunchWindowsUninstaller(application);
        _installedStatus.Text = result.Message.ToUpperInvariant();
    }

    private async Task CheckUpdateWithAiAsync(InstalledApplicationInfo application)
    {
        _installedStatus.Text = $"AI CHECKING {application.Name.ToUpperInvariant()} {application.Version ?? "UNKNOWN VERSION"}...";
        string? temporaryThread = null;
        var previousThread = App.Services.Codex.ActiveThreadId;
        try
        {
            if (!App.Services.Codex.IsConnected) await App.Services.Codex.StartAsync();
            temporaryThread = await App.Services.Codex.StartThreadAsync(model: NullIfBlank(App.Services.Settings.CodexModel));
            var result = await App.Services.Codex.RunTurnAsync(
                $"Check whether this installed Windows application has a newer stable version: " +
                $"name={application.Name}; installed version={application.Version ?? "unknown"}; publisher={application.Publisher ?? "unknown"}. " +
                "Use available web research if possible. Do not install or change anything. Reply concisely with current/latest version, update availability, confidence, and official download source.",
                temporaryThread);
            await ShowResultAsync($"AI UPDATE CHECK // {application.Name}", result.FinalText);
            _installedStatus.Text = $"AI UPDATE CHECK COMPLETE // {application.Name.ToUpperInvariant()}";
        }
        catch (Exception ex) { _installedStatus.Text = $"AI UPDATE CHECK FAILED // {ex.Message}"; }
        finally
        {
            if (temporaryThread is not null)
            {
                try { await App.Services.Codex.DeleteThreadAsync(temporaryThread); } catch { }
            }
            if (previousThread is not null)
            {
                try { await App.Services.Codex.ResumeThreadAsync(previousThread); } catch { }
            }
        }
    }

    private async Task ShowResultAsync(string title, string message)
    {
        var scroll = new ScrollViewer
        {
            MaxHeight = 470,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
        };
        await new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot, Title = title, Content = scroll, CloseButtonText = "CLOSE"
        }.ShowAsync();
    }

    private async Task LoadIconsAsync<T>(IEnumerable<(T Row, string? Path)> targets,
        CancellationToken cancellationToken) where T : AppRow
    {
        var queue = targets.Where(target => !string.IsNullOrWhiteSpace(target.Path)).ToArray();
        foreach (var batch in queue.Chunk(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(batch.Select(async target =>
            {
                var key = target.Path!;
                if (!_iconCache.TryGetValue(key, out var icon))
                {
                    icon = await LoadIconAsync(key);
                    if (icon is not null && _iconCache.Count < 96) _iconCache[key] = icon;
                }
                if (icon is not null && !cancellationToken.IsCancellationRequested) target.Row.Icon = icon;
            }));
        }
    }

    private static async Task<ImageSource?> LoadIconAsync(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 48,
                    ThumbnailOptions.UseCurrentScale);
                if (thumbnail is not null)
                {
                    var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(thumbnail); return bitmap;
                }
            }
            catch { }
        }
        return null;
    }

    private Grid BuildTitleBar()
    {
        var bar = new Grid { Height = 38, Background = Brush(245, 3, 12, 18) };
        bar.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Cyan(110) });
        bar.Children.Add(new TextBlock
        {
            Text = "◇  DAGNUE // APPS COMMAND INTERFACE", FontSize = 9, CharacterSpacing = 110,
            Margin = new Thickness(15, 0, 145, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Cyan(200), IsHitTestVisible = false
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(CaptionButton("—", () => _presenter.Minimize(), false));
        buttons.Children.Add(CaptionButton("◇", () =>
        {
            if (_presenter.State == OverlappedPresenterState.Maximized) _presenter.Restore(); else _presenter.Maximize();
        }, false));
        buttons.Children.Add(CaptionButton("×", Close, true)); bar.Children.Add(buttons); return bar;
    }

    private void UpdateCaptionRegions()
    {
        var scale = Math.Max(1d, GetDpiForWindow(_handle) / 96d);
        var height = (int)Math.Ceiling(38 * scale); var buttons = (int)Math.Ceiling(138 * scale);
        var caption = Math.Max(1, _appWindow.Size.Width - buttons);
        _nonClient.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32(0, 0, caption, height)]);
        _nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [new RectInt32(caption, 0, buttons, height)]);
    }

    private static Border Frame(UIElement child) => new()
    {
        Child = child, BorderThickness = new Thickness(1), BorderBrush = Cyan(75), Background = Brush(140, 4, 15, 22)
    };
    private static Grid Footer(ProgressRing progress, TextBlock status)
    {
        var grid = new Grid { ColumnSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(progress); Grid.SetColumn(status, 1); grid.Children.Add(status); return grid;
    }
    private Grid ColumnHeader(string[] labels, double[] widths, int row, bool installed)
    {
        var grid = new Grid { Padding = new Thickness(12, 8, 12, 8), Background = Brush(175, 6, 22, 30) };
        var headers = installed ? _installedHeaders : _runningHeaders;
        headers.Clear();
        for (var i = 0; i < labels.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(widths[i]) });
            var column = i;
            var button = new Button
            {
                Content = labels[i], HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Height = 24, Padding = new Thickness(0),
                FontSize = 8, CharacterSpacing = 65, Foreground = Cyan(210),
                Background = Brush(0, 0, 0, 0), BorderThickness = new Thickness(0)
            };
            ToolTipService.SetToolTip(button, "Click to sort; click again to reverse");
            button.Click += (_, _) => SortByHeader(installed, column);
            headers.Add((button, labels[i]));
            Grid.SetColumn(button, i); grid.Children.Add(button);
        }
        UpdateHeaderIndicators(installed);
        Grid.SetRow(grid, row); return grid;
    }
    private static Button ActionButton(string text)
    {
        var button = new Button
        {
            Content = text, MinWidth = 180, MinHeight = 40, HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Cyan(), Background = Brush(170, 5, 25, 34), BorderBrush = Cyan(120)
        };
        if (Application.Current.Resources["AlienActionButtonStyle"] is Style style) button.Style = style;
        return button;
    }
    private static Button CaptionButton(string text, Action action, bool danger)
    {
        var button = new Button { Content = text, Width = 46, Height = 37, Padding = new Thickness(0),
            Background = Brush(0, 0, 0, 0), BorderThickness = new Thickness(0),
            Foreground = danger ? Brush(255, 255, 85, 103) : Cyan(200) };
        button.Click += (_, _) => action(); return button;
    }
    private static TextBlock StatusText(string text) => new() { Text = text, FontSize = 9, Foreground = Cyan(210) };
    private static SolidColorBrush Cyan(byte alpha = 255) => Brush(alpha, 0, 229, 255);
    private static SolidColorBrush Muted() => Brush(185, 145, 171, 181);
    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue) => new(Color.FromArgb(alpha, red, green, blue));
    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static Style AlienTabStyle() => (Style)XamlReader.Load("""
      <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
        <Setter Property="Padding" Value="20,0"/><Setter Property="BorderThickness" Value="0"/>
        <Setter Property="FontSize" Value="10"/><Setter Property="CharacterSpacing" Value="65"/>
        <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
          <Grid x:Name="Cell" RenderTransformOrigin="0.5,0.5">
            <Grid.RenderTransform><CompositeTransform x:Name="Scale"/></Grid.RenderTransform>
            <Path Data="M 11,0 L 252,0 L 270,15 L 270,35 L 260,44 L 8,44 L 0,35 L 0,10 Z"
                  Stretch="Fill" Fill="{TemplateBinding Background}" Stroke="#9900E5FF" StrokeThickness="1"/>
            <Path Data="M 3,11 L 12,3 L 55,3 M 215,41 L 258,41 L 267,33" Stretch="Fill"
                  Stroke="#E000E5FF" StrokeThickness="1.2" Margin="3"/>
            <Rectangle Width="34" Height="2" Fill="#F000E5FF" HorizontalAlignment="Left" VerticalAlignment="Bottom" Margin="16,0,0,1"/>
            <Ellipse Width="6" Height="6" Fill="#D000E5FF" HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,7,11,0"/>
            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            <VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="CommonStates">
              <VisualState x:Name="Normal"/><VisualState x:Name="PointerOver"><VisualState.Setters><Setter Target="Cell.Opacity" Value="0.84"/><Setter Target="Scale.ScaleX" Value="1.018"/><Setter Target="Scale.ScaleY" Value="1.018"/></VisualState.Setters></VisualState>
              <VisualState x:Name="Pressed"><VisualState.Setters><Setter Target="Scale.ScaleX" Value="0.98"/><Setter Target="Scale.ScaleY" Value="0.98"/></VisualState.Setters></VisualState>
            </VisualStateGroup></VisualStateManager.VisualStateGroups>
          </Grid>
        </ControlTemplate></Setter.Value></Setter>
      </Style>
      """);

    private static DataTemplate RunningTemplate() => (DataTemplate)XamlReader.Load(RowTemplate(true));
    private static DataTemplate InstalledTemplate() => (DataTemplate)XamlReader.Load(RowTemplate(false));
    private static string RowTemplate(bool running) => running ? """
      <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
        <Grid Padding="10,7" ColumnSpacing="10" MinHeight="58">
          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="85"/><ColumnDefinition Width="110"/><ColumnDefinition Width="260"/><ColumnDefinition Width="140"/></Grid.ColumnDefinitions>
          <StackPanel Orientation="Horizontal" Spacing="10"><Border Width="38" Height="38" Background="#4200BBD0" CornerRadius="5"><Image Source="{Binding Icon}" Width="30" Height="30"/></Border><StackPanel><TextBlock Text="{Binding Name}" FontSize="13" Foreground="#F0F5FCFF"/><TextBlock Text="{Binding Detail}" FontSize="9" Foreground="#A8A7BDC6" TextTrimming="CharacterEllipsis" MaxWidth="450"/></StackPanel></StackPanel>
          <TextBlock Grid.Column="1" Text="{Binding Pid}" VerticalAlignment="Center" Foreground="#D300E5FF"/><TextBlock Grid.Column="2" Text="{Binding Memory}" VerticalAlignment="Center" Foreground="#D6DCEAF0"/><TextBlock Grid.Column="3" Text="{Binding Safety}" VerticalAlignment="Center" Foreground="#AFC1D4DC"/>
          <Button Grid.Column="4" Content="END TASK" Command="{Binding EndCommand}" IsEnabled="{Binding CanTerminate}" Style="{StaticResource AlienActionButtonStyle}" Margin="4" Foreground="#FFFF6675" Background="#501D0B12" BorderBrush="#BFFF5365"/>
        </Grid>
      </DataTemplate>
      """ : """
      <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
        <Grid Padding="10,7" ColumnSpacing="10" MinHeight="62">
          <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="105"/><ColumnDefinition Width="145"/><ColumnDefinition Width="135"/><ColumnDefinition Width="140"/><ColumnDefinition Width="230"/></Grid.ColumnDefinitions>
          <StackPanel Orientation="Horizontal" Spacing="10"><Border Width="40" Height="40" Background="#4200BBD0" CornerRadius="5"><Image Source="{Binding Icon}" Width="32" Height="32"/></Border><StackPanel><TextBlock Text="{Binding Name}" FontSize="13" Foreground="#F0F5FCFF"/><TextBlock Text="{Binding Publisher}" FontSize="9" Foreground="#A8A7BDC6" TextTrimming="CharacterEllipsis" MaxWidth="390"/></StackPanel></StackPanel>
          <TextBlock Grid.Column="1" Text="{Binding Version}" VerticalAlignment="Center" Foreground="#D3DCEBF1"/><StackPanel Grid.Column="2" VerticalAlignment="Center"><TextBlock Text="{Binding Size}" Foreground="#E000E5FF"/><TextBlock Text="{Binding Volume}" FontSize="8" Foreground="#8FA4B7C0"/></StackPanel>
          <TextBlock Grid.Column="3" Text="{Binding LastUsed}" VerticalAlignment="Center" Foreground="#C4B9CED6"/>
          <Button Grid.Column="4" Content="CHECK // AI" Command="{Binding UpdateCommand}" Style="{StaticResource AlienActionButtonStyle}" Margin="4" Foreground="#E700E5FF" Background="#5006212A" BorderBrush="#A000E5FF"/>
          <StackPanel Grid.Column="5"><Button Content="UNINSTALL // WINDOWS" Command="{Binding UninstallCommand}" IsEnabled="{Binding CanUninstall}" Style="{StaticResource AlienActionButtonStyle}" Margin="4" Foreground="#FFFF7583" Background="#501D0B12" BorderBrush="#AFFF5365"/><TextBlock Text="{Binding Safety}" FontSize="8" TextAlignment="Center" Foreground="#91A9BAC1" TextWrapping="Wrap"/></StackPanel>
        </Grid>
      </DataTemplate>
      """;

    public abstract class AppRow : INotifyPropertyChanged
    {
        private ImageSource? _icon;
        public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    public sealed class RunningRow : AppRow
    {
        public RunningRow(RunningApplicationInfo application, ICommand endCommand, bool canTerminate)
        {
            Application = application; EndCommand = endCommand; CanTerminate = canTerminate;
        }
        public RunningApplicationInfo Application { get; }
        public string Name => Application.Name; public string Detail => Application.WindowTitle;
        public string Pid => Application.ProcessId.ToString(); public string Memory => FormatSize(Application.WorkingSetBytes);
        public string Safety => Application.SafetyNote; public bool CanTerminate { get; } public ICommand EndCommand { get; }
    }
    public sealed class InstalledRow : AppRow
    {
        public InstalledRow(InstalledApplicationInfo application, ICommand updateCommand, ICommand uninstallCommand, bool canUninstall)
        {
            Application = application; UpdateCommand = updateCommand; UninstallCommand = uninstallCommand; CanUninstall = canUninstall;
        }
        public InstalledApplicationInfo Application { get; }
        public string Name => Application.Name; public string Publisher => Application.Publisher ?? "UNKNOWN PUBLISHER";
        public string Version => Application.Version ?? "—"; public string Size => Application.SizeBytes is { } size ? FormatSize(size) : "UNKNOWN";
        public string Volume => Application.InstallLocation is { Length: > 1 } path ? $"VOLUME {Path.GetPathRoot(path) ?? "—"}" : "VOLUME —";
        public string LastUsed => Application.LastUsedAt?.ToLocalTime().ToString("MMM d, yyyy") ?? "UNKNOWN";
        public string Safety => Application.SafetyNote; public bool CanUninstall { get; }
        public ICommand UpdateCommand { get; } public ICommand UninstallCommand { get; }
    }
    private sealed class AsyncCommand(Func<object?, Task> execute, bool enabled = true) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => enabled;
        public async void Execute(object? parameter) => await execute(parameter);
    }
    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"]; var value = (double)Math.Max(0, bytes); var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
