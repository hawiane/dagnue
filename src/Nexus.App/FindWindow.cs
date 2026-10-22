using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Nexus.Core.Models;
using Nexus.Core.SystemServices;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace Nexus.App;

public sealed class FindWindow : Window
{
    private readonly IndexedFileSearchService _search = App.Services.FileSearch;
    private readonly OverlappedPresenter _presenter;
    private readonly InputNonClientPointerSource _nonClient;
    private readonly nint _handle;
    private readonly TextBox _query = new()
    {
        PlaceholderText = "Search files and folders...",
        FontSize = 14, MinHeight = 48, Padding = new Thickness(15, 10, 15, 10)
    };
    private readonly Button _allKinds = new() { Content = "ALL", MinWidth = 58, MinHeight = 48, FontSize = 9 };
    private readonly Button _fileKinds = new() { Content = "FILES", MinWidth = 66, MinHeight = 48, FontSize = 9 };
    private readonly Button _directoryKinds = new() { Content = "FOLDERS", MinWidth = 76, MinHeight = 48, FontSize = 9 };
    private readonly Button _refresh = new()
    {
        Content = "REFRESH", MinWidth = 96, MinHeight = 48,
        Padding = new Thickness(13, 8, 13, 8), FontSize = 10,
        BorderThickness = new Thickness(1)
    };
    private readonly ListView _results = new()
    {
        SelectionMode = ListViewSelectionMode.Single,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        IsItemClickEnabled = false
    };
    private readonly TextBlock _status = new() { Text = "CONNECTING TO PERSISTENT FILESYSTEM INDEX", FontSize = 10 };
    private readonly TextBlock _count = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly ProgressRing _progress = new() { Width = 22, Height = 22, IsActive = false };
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DelegateCommand _openFile;
    private readonly DelegateCommand _openFolder;
    private readonly Dictionary<FileSearchSort, TextBlock> _sortIndicators = [];
    private FileSearchSort _currentSort = FileSearchSort.Relevance;
    private FileSearchKind _currentKind = FileSearchKind.All;
    private CancellationTokenSource? _searchCancellation;
    private bool _closed;
    private bool _firstActivation = true;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    public FindWindow()
    {
        Title = "DAGNUE FIND // FILE AND DIRECTORY SIGNAL MATRIX";
        _handle = WindowNative.GetWindowHandle(this);
        _presenter = (OverlappedPresenter)AppWindow.Presenter;
        _nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        SystemBackdrop = new MicaBackdrop();
        _openFile = new DelegateCommand(value => OpenFile(value as SearchRow));
        _openFolder = new DelegateCommand(value => OpenFolder(value as SearchRow));
        _results.ItemTemplate = BuildRowTemplate();
        _results.DoubleTapped += Results_DoubleTapped;
        _query.TextChanged += (_, _) => QueueSearch();
        _query.KeyDown += Query_KeyDown;
        _refresh.Click += async (_, _) =>
        {
            _typingTimer.Stop();
            _status.Text = "REFRESHING COMMON FILE LOCATIONS...";
            await _search.RefreshCommonLocationsAsync();
            await RunSearchAsync();
        };
        _allKinds.Click += (_, _) => SetSearchKind(FileSearchKind.All);
        _fileKinds.Click += (_, _) => SetSearchKind(FileSearchKind.Files);
        _directoryKinds.Click += (_, _) => SetSearchKind(FileSearchKind.Directories);
        _typingTimer.Tick += async (_, _) => { _typingTimer.Stop(); await RunSearchAsync(); };
        Content = BuildLayout();
        AppWindow.Resize(new SizeInt32(1160, 780));
        UpdateCaptionRegions();
        AppWindow.Changed += Window_Changed;
        Activated += Window_Activated;
        Closed += Window_Closed;
    }

    private UIElement BuildLayout()
    {
        if (Application.Current.Resources["AlienTextBoxStyle"] is Style inputStyle) _query.Style = inputStyle;
        var root = new Grid { Background = Brush(255, 3, 9, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(38) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(BuildTitleBar());

        var body = new Grid { Padding = new Thickness(27, 21, 27, 20), RowSpacing = 13 };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel { Spacing = 3 };
        heading.Children.Add(new TextBlock
        {
            Text = "FIND // FILE + DIRECTORY SIGNAL MATRIX", FontSize = 22,
            CharacterSpacing = 55, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Cyan()
        });
        heading.Children.Add(new TextBlock
        {
            Text = "PERSISTENT ALL-DRIVE INDEX  //  MANUAL REFRESH  //  FRAGMENTS MATCH IN ANY ORDER",
            FontSize = 9, CharacterSpacing = 70, Foreground = Muted()
        });
        body.Children.Add(heading);

        var controls = new Grid { ColumnSpacing = 10 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Children.Add(new Border
        {
            Child = _query, BorderThickness = new Thickness(1), BorderBrush = Cyan(110),
            Background = Brush(210, 7, 20, 28)
        });
        var kindSelector = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var button in new[] { _allKinds, _fileKinds, _directoryKinds })
        {
            button.Style = BuildAlienButtonStyle();
            kindSelector.Children.Add(button);
        }
        UpdateKindButtons();
        Grid.SetColumn(kindSelector, 1); controls.Children.Add(kindSelector);

        _refresh.Style = BuildAlienButtonStyle();
        _refresh.Foreground = Cyan(230);
        _refresh.BorderBrush = Cyan(115);
        _refresh.Background = Brush(165, 5, 24, 32);
        ToolTipService.SetToolTip(_refresh, "Refresh indexed results manually");
        Grid.SetColumn(_refresh, 2); controls.Children.Add(_refresh);
        Grid.SetRow(controls, 1); body.Children.Add(controls);

        var columns = new Grid { Padding = new Thickness(15, 7, 15, 7), Background = Brush(170, 7, 21, 29) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(195) });
        var identityHeaders = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 19 };
        identityHeaders.Children.Add(CreateSortHeader("FILE / DIRECTORY SIGNAL", FileSearchSort.Name));
        identityHeaders.Children.Add(CreateSortHeader("LOCATION", FileSearchSort.Location));
        Grid.SetColumn(identityHeaders, 0); columns.Children.Add(identityHeaders);
        AddSortHeader(columns, "TYPE", 1, FileSearchSort.Type);
        AddSortHeader(columns, "SIZE", 2, FileSearchSort.Size);
        AddColumnLabel(columns, "ACTIONS", 3);
        Grid.SetRow(columns, 2); body.Children.Add(columns);

        var listFrame = new Border
        {
            Child = _results, BorderThickness = new Thickness(1), BorderBrush = Cyan(75),
            Background = Brush(135, 5, 15, 22)
        };
        Grid.SetRow(listFrame, 3); body.Children.Add(listFrame);

        var footer = new Grid { ColumnSpacing = 9 };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(_progress);
        _status.Foreground = Cyan(210); Grid.SetColumn(_status, 1); footer.Children.Add(_status);
        _count.Foreground = Muted(); Grid.SetColumn(_count, 2); footer.Children.Add(_count);
        Grid.SetRow(footer, 4); body.Children.Add(footer);

        Grid.SetRow(body, 1); root.Children.Add(body);
        return root;
    }

    private Grid BuildTitleBar()
    {
        var bar = new Grid { Height = 38, Background = Brush(245, 3, 12, 18) };
        bar.Children.Add(new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Bottom, Background = Cyan(110)
        });
        bar.Children.Add(new TextBlock
        {
            Text = "◇  DAGNUE // FIND COMMAND INTERFACE", FontSize = 9, CharacterSpacing = 110,
            Margin = new Thickness(15, 0, 145, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = Cyan(200), IsHitTestVisible = false
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

    private static Button CaptionButton(string label, Action callback, bool danger)
    {
        var button = new Button
        {
            Content = label, Width = 46, Height = 37, Padding = new Thickness(0),
            Background = Brush(0, 0, 0, 0), BorderThickness = new Thickness(0),
            Foreground = danger ? Brush(255, 255, 85, 103) : Cyan(200)
        };
        button.Click += (_, _) => callback();
        return button;
    }

    private static void AddColumnLabel(Grid target, string text, int column)
    {
        var label = new TextBlock { Text = text, FontSize = 8, CharacterSpacing = 80, Foreground = Cyan(210) };
        Grid.SetColumn(label, column); target.Children.Add(label);
    }

    private void AddSortHeader(Grid target, string text, int column, FileSearchSort sort)
    {
        var button = CreateSortHeader(text, sort);
        Grid.SetColumn(button, column);
        target.Children.Add(button);
    }

    private Button CreateSortHeader(string text, FileSearchSort sort)
    {
        var label = new TextBlock
        {
            Text = text, FontSize = 8, CharacterSpacing = 80,
            Foreground = Cyan(210), VerticalAlignment = VerticalAlignment.Center
        };
        var indicator = new TextBlock
        {
            Text = "◇", FontSize = 8, Foreground = Cyan(245),
            VerticalAlignment = VerticalAlignment.Center, Opacity = 0.35
        };
        _sortIndicators[sort] = indicator;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(label);
        content.Children.Add(indicator);
        var button = new Button
        {
            Content = content, MinWidth = 0, MinHeight = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = Brush(0, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left
        };
        ToolTipService.SetToolTip(button, $"Sort by {text.ToLowerInvariant()}");
        button.Click += (_, _) =>
        {
            _currentSort = _currentSort == sort ? FileSearchSort.Relevance : sort;
            foreach (var (mode, marker) in _sortIndicators)
            {
                marker.Text = _currentSort == mode ? "◆" : "◇";
                marker.Opacity = _currentSort == mode ? 1 : 0.35;
            }
            QueueSearch();
        };
        return button;
    }

    private static Style BuildAlienButtonStyle() => (Style)XamlReader.Load("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
               xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
          <Setter Property="Foreground" Value="#EEBFFBFF"/>
          <Setter Property="Background" Value="#A0071A22"/>
          <Setter Property="BorderBrush" Value="#A000E5FF"/>
          <Setter Property="Padding" Value="11,6"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
            <Grid x:Name="AlienCell" Background="Transparent">
              <Path Data="M 9,0 L 88,0 L 100,12 L 100,34 L 90,44 L 0,44 L 0,9 Z"
                    Stretch="Fill" Fill="{TemplateBinding Background}" Stroke="{TemplateBinding BorderBrush}" StrokeThickness="1"/>
              <Path Data="M 4,10 L 11,3 L 28,3 M 73,41 L 88,41 L 97,32"
                    Stretch="Fill" Stroke="#AD00E5FF" StrokeThickness="1" Margin="3" IsHitTestVisible="False"/>
              <Rectangle x:Name="SignalBar" Height="2" Width="16" Fill="#E900E5FF"
                         HorizontalAlignment="Left" VerticalAlignment="Bottom" Margin="11,0,0,1"/>
              <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"
                                Padding="{TemplateBinding Padding}"/>
              <VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="CommonStates">
                <VisualState x:Name="Normal"/>
                <VisualState x:Name="PointerOver"><VisualState.Setters>
                  <Setter Target="AlienCell.Opacity" Value="0.86"/>
                  <Setter Target="SignalBar.Width" Value="29"/>
                </VisualState.Setters></VisualState>
                <VisualState x:Name="Pressed"><VisualState.Setters>
                  <Setter Target="AlienCell.Opacity" Value="0.68"/>
                </VisualState.Setters></VisualState>
                <VisualState x:Name="Disabled"><VisualState.Setters>
                  <Setter Target="AlienCell.Opacity" Value="0.35"/>
                </VisualState.Setters></VisualState>
              </VisualStateGroup></VisualStateManager.VisualStateGroups>
            </Grid>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);

    private static DataTemplate BuildRowTemplate() => (DataTemplate)XamlReader.Load("""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Grid Padding="9,7" ColumnSpacing="9" MinHeight="53">
            <Grid.Resources>
              <Style x:Key="AlienRowAction" TargetType="Button">
                <Setter Property="Foreground" Value="#EAF3FDFF"/>
                <Setter Property="Background" Value="#4A06212A"/>
                <Setter Property="BorderBrush" Value="#A100E5FF"/>
                <Setter Property="Padding" Value="10,5"/>
                <Setter Property="FontSize" Value="9"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                  <Grid x:Name="ActionCell" Background="Transparent">
                    <Path Data="M 6,0 L 48,0 L 56,7 L 56,24 L 50,30 L 0,30 L 0,6 Z"
                          Stretch="Fill" Fill="{TemplateBinding Background}"
                          Stroke="{TemplateBinding BorderBrush}" StrokeThickness="1"/>
                    <Path Data="M 2,7 L 7,2 L 17,2 M 39,28 L 49,28 L 54,23"
                          Stretch="Fill" Stroke="#D500E5FF" StrokeThickness="0.8" Margin="2"/>
                    <Rectangle Width="9" Height="2" HorizontalAlignment="Left" VerticalAlignment="Bottom"
                               Margin="8,0,0,1" Fill="#F000E5FF"/>
                    <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"
                                      Padding="{TemplateBinding Padding}"/>
                    <VisualStateManager.VisualStateGroups><VisualStateGroup x:Name="CommonStates">
                      <VisualState x:Name="Normal"/>
                      <VisualState x:Name="PointerOver"><VisualState.Setters>
                        <Setter Target="ActionCell.Opacity" Value="0.82"/>
                      </VisualState.Setters></VisualState>
                      <VisualState x:Name="Pressed"><VisualState.Setters>
                        <Setter Target="ActionCell.Opacity" Value="0.62"/>
                      </VisualState.Setters></VisualState>
                      <VisualState x:Name="Disabled"/>
                    </VisualStateGroup></VisualStateManager.VisualStateGroups>
                  </Grid>
                </ControlTemplate></Setter.Value></Setter>
              </Style>
            </Grid.Resources>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="*"/><ColumnDefinition Width="85"/>
              <ColumnDefinition Width="98"/><ColumnDefinition Width="185"/>
            </Grid.ColumnDefinitions>
            <StackPanel Spacing="2">
              <TextBlock Text="{Binding Name}" FontSize="12" Foreground="#EDF0FBFF" TextTrimming="CharacterEllipsis"/>
              <TextBlock Text="{Binding Location}" FontSize="9" Foreground="#9EA5BAC5" TextTrimming="CharacterEllipsis"/>
            </StackPanel>
            <TextBlock Grid.Column="1" Text="{Binding Type}" FontSize="9" Foreground="#E500E5FF" VerticalAlignment="Center"/>
            <TextBlock Grid.Column="2" Text="{Binding Size}" FontSize="9" Foreground="#CAC9DCE3" VerticalAlignment="Center"/>
            <StackPanel Grid.Column="3" Orientation="Horizontal" Spacing="5" VerticalAlignment="Center">
              <Button Content="OPEN" Command="{Binding OpenFileCommand}" CommandParameter="{Binding}"
                      Style="{StaticResource AlienRowAction}"/>
              <Button Content="FOLDER" Command="{Binding OpenFolderCommand}" CommandParameter="{Binding}"
                      Style="{StaticResource AlienRowAction}"/>
            </StackPanel>
          </Grid>
        </DataTemplate>
        """);

    private void Query_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is VirtualKey.Enter or VirtualKey.F5)
        {
            _typingTimer.Stop(); _ = RunSearchAsync(); args.Handled = true;
        }
        else if (args.Key == VirtualKey.Escape) { _query.Text = string.Empty; args.Handled = true; }
    }

    private void QueueSearch()
    {
        if (_closed) return;
        _searchCancellation?.Cancel();
        _typingTimer.Stop();
        _typingTimer.Start();
    }

    private void SetSearchKind(FileSearchKind kind)
    {
        if (_currentKind == kind) return;
        _currentKind = kind;
        UpdateKindButtons();
        QueueSearch();
    }

    private void UpdateKindButtons()
    {
        foreach (var (button, kind) in new[]
                 {
                     (_allKinds, FileSearchKind.All),
                     (_fileKinds, FileSearchKind.Files),
                     (_directoryKinds, FileSearchKind.Directories)
                 })
        {
            var selected = _currentKind == kind;
            button.Foreground = selected ? Brush(255, 230, 252, 255) : Muted();
            button.Background = selected ? Brush(225, 0, 66, 82) : Brush(150, 5, 24, 32);
            button.BorderBrush = selected ? Cyan(245) : Cyan(95);
        }
    }

    private async Task RunSearchAsync()
    {
        if (_closed) return;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        _progress.IsActive = true;
        _status.Text = _search.CurrentSnapshot.Nodes.Count == 0
            ? "WAITING FOR THE PERSISTENT FILESYSTEM INDEX..."
            : "SEARCHING INDEXED SIGNALS...";
        try
        {
            if (_search.CurrentSnapshot.Nodes.Count == 0)
            {
                _status.Text = "FILESYSTEM INDEX NOT READY // SELECT REFRESH WHEN AVAILABLE";
                return;
            }
            var result = await _search.SearchAsync(_query.Text.Trim(), _currentSort, 300, token, _currentKind);
            token.ThrowIfCancellationRequested();
            _results.ItemsSource = result.Items.Select(node => new SearchRow(node, _openFile, _openFolder)).ToArray();
            _status.Text = result.IsLimited
                ? $"SHOWING FIRST {result.Items.Count:N0} OF {result.TotalMatches:N0} MATCHES // REFINE TO NARROW"
                : $"{result.TotalMatches:N0} MATCHING {_currentKind.ToString().ToUpperInvariant()} // DOUBLE-CLICK TO OPEN";
            _count.Text = $"{result.IndexedFiles:N0} FILES  //  {result.IndexedDirectories:N0} FOLDERS  //  MANUAL";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = $"SEARCH ERROR // {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) _progress.IsActive = false;
        }
    }

    private void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (_results.SelectedItem is SearchRow row) OpenFile(row);
    }

    private void OpenFile(SearchRow? row)
    {
        if (row is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
            App.Services.FileActivity.TrackApplicationAccess(row.Path, row.IsDirectory, "DAGNUE FIND");
        }
        catch (Exception ex) { _status.Text = $"OPEN FAILED // {ex.Message}"; }
    }

    private void OpenFolder(SearchRow? row)
    {
        if (row is null) return;
        try
        {
            if (row.IsDirectory)
                Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    ArgumentList = { "/select,", row.Path }, UseShellExecute = false
                });
            App.Services.FileActivity.TrackApplicationAccess(Path.GetDirectoryName(row.Path) ?? row.Path,
                isDirectory: true, source: "DAGNUE FIND");
        }
        catch (Exception ex) { _status.Text = $"OPEN FOLDER FAILED // {ex.Message}"; }
    }

    private async void Window_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_firstActivation) return;
        _firstActivation = false;
        _query.Focus(FocusState.Programmatic);
        await RunSearchAsync();
    }

    private void Window_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange) UpdateCaptionRegions();
    }

    private void UpdateCaptionRegions()
    {
        var scale = Math.Max(1d, GetDpiForWindow(_handle) / 96d);
        var height = (int)Math.Ceiling(38 * scale);
        var buttons = (int)Math.Ceiling(138 * scale);
        var caption = Math.Max(1, AppWindow.Size.Width - buttons);
        _nonClient.SetRegionRects(NonClientRegionKind.Caption, [new RectInt32(0, 0, caption, height)]);
        _nonClient.SetRegionRects(NonClientRegionKind.Passthrough, [new RectInt32(caption, 0, buttons, height)]);
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _typingTimer.Stop();
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        AppWindow.Changed -= Window_Changed;
    }

    private static SolidColorBrush Cyan(byte alpha = 255) => Brush(alpha, 0, 229, 255);
    private static SolidColorBrush Muted() => Brush(185, 145, 171, 181);
    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));

    public sealed class SearchRow(FileGraphNode node, ICommand openFileCommand, ICommand openFolderCommand)
    {
        public string Name => node.Name;
        public string Path => node.Path;
        public string Location => System.IO.Path.GetDirectoryName(node.Path) ?? node.Path;
        public bool IsDirectory => node.IsDirectory;
        public string Type => node.IsDirectory ? "FOLDER" :
            System.IO.Path.GetExtension(node.Name).TrimStart('.').ToUpperInvariant() is { Length: > 0 } value ? value : "FILE";
        public string Size => node.IsDirectory ? "—" : FormatSize(node.SizeBytes);
        public ICommand OpenFileCommand => openFileCommand;
        public ICommand OpenFolderCommand => openFolderCommand;

        private static string FormatSize(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB", "TB"];
            var value = (double)Math.Max(0, bytes); var unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
        }
    }

    private sealed class DelegateCommand(Action<object?> execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute(parameter);
    }
}
