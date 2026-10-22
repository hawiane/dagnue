using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexus.Core.Models;
using Nexus.Core.SystemServices;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed partial class MeshBrainControl : UserControl
{
    private const int VisualIndexNodeLimit = 100_000;
    private static readonly object TextFormatGate = new();
    private static readonly Dictionary<(int SizeTenths, CanvasHorizontalAlignment Alignment), CanvasTextFormat> TextFormats = [];
    private readonly object _graphGate = new();
    private readonly SemaphoreSlim _refreshSignal = new(0, 1);
    private readonly ConcurrentQueue<FileActivity> _pendingIndexChanges = new();
    private List<VisualNode> _nodes = [];
    private List<int> _renderOrder = [];
    private Dictionary<string, int> _nodePathIndex = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<int, List<int>> _childrenByParent = [];
    private List<ActiveActivity> _activities = [];
    private FileGraphSnapshot _snapshot = FileGraphSnapshot.Empty;
    private FileGraphProgress _indexProgress = FileGraphProgress.Empty;
    private CancellationTokenSource? _indexCancellation;
    private int _indexGeneration;
    private FileActivityService? _fileActivityService;
    private BrainState _state = BrainState.Offline;
    private float _cpu;
    private float _memory;
    private float _disk;
    private bool _network;
    private bool _indexing = true;
    private bool _orbitDragging;
    private uint _dragPointerId;
    private Vector2 _lastDragPosition;
    private float _dragTravel;
    private Quaternion _orbitOrientation = Quaternion.Identity;
    private bool _manualOrbitActivated;
    private float _zoom = 1f;
    private float _targetZoom = 1f;
    private float _animationTime;
    private Vector3 _focusPivot;
    private float _navigationCenterRatio = 0.5f;
    private int _selectedNodeIndex = -1;
    private int _inspectorNodeIndex = -1;
    private HashSet<int> _expandedInspectorNodes = [];
    private string _inspectorFilterQuery = string.Empty;
    private HashSet<int> _inspectorFilterMatchIndices = [];
    private HashSet<string> _expandedLiveInspectorPaths = new(StringComparer.OrdinalIgnoreCase);
    private LiveInspectorEntry? _liveInspectorSelection;
    private string _activeSearchQuery = string.Empty;
    private HashSet<int> _searchMatchIndices = [];
    private bool _searchResultsRequested;
    private bool _searchDockExpanded;
    private bool _meshSettingsRequested;
    private bool _showChildrenTab;
    private List<HitNode> _hitNodes = [];
    private List<AggregateHit> _aggregateHits = [];
    private Dictionary<int, int> _aggregateExpansion = [];
    private bool _showMajorDirectories = true;
    private bool _showDeepDirectories = true;
    private bool _showAggregateNodes = true;
    private bool _showIndividualFiles = true;
    private bool _showSystemNodes = true;
    private bool _showActiveOperations = true;
    private bool _showDirectoryConnections = true;
    private bool _colorCodeByDrive = true;
    private readonly Dictionary<string, string> _driveColors = new(StringComparer.OrdinalIgnoreCase);
    private bool _appearanceReady;
    private float _generalNodeScale = 1f;
    private float _fileNodeScale = 1f;
    private float _directoryNodeScale = 1f;
    private Color _directoryColor = Color.FromArgb(255, 92, 150, 255);
    private Color _fileColor = Color.FromArgb(255, 61, 218, 255);
    private Color _selectionColor = Color.FromArgb(255, 54, 255, 137);
    private Color _accessColor = Color.FromArgb(255, 0, 229, 255);
    private Color _writeColor = Color.FromArgb(255, 255, 176, 32);
    private Color _createColor = Color.FromArgb(255, 64, 238, 135);
    private Color _moveColor = Color.FromArgb(255, 182, 92, 255);
    private Color _deleteColor = Color.FromArgb(255, 255, 48, 58);
    private ActivityVisualizationMode _activityVisualizationMode = ActivityVisualizationMode.NodeGlowOnly;
    private bool _transferStyleExplicitlyChosen;
    private bool _autoOrbit = true;
    private bool _reverseOrbit;
    private float _orbitSpeed = 1f;
    private float _orbitPhaseRadians;
    private float _orbitTiltRadians = MathF.PI / 18f;
    private CanvasRenderTarget? _fileNodeSprite;
    private CanvasRenderTarget? _directoryNodeSprite;
    private CanvasRenderTarget? _neuronFiberSprite;
    private HashSet<int> _visibleNodeCache = [];
    private MeshVisibilityOptions? _visibleNodeCacheOptions;
    private int _visibleNodeCacheSourceCount = -1;
    private int _visibleNodeCacheBudget = -1;
    private readonly HashSet<int> _drawableNodesScratch = [];

    private readonly record struct VisualNode(
        Vector3 Position,
        Vector3 BrainPosition,
        float Phase,
        float Radius,
        bool IsDirectory,
        int ChildCount,
        int Depth,
        int ParentIndex,
        bool IsRoot,
        string Name,
        string Path,
        long SizeBytes,
        int SubtreeCount);

    private readonly record struct ProjectedNode(Vector2 Position, float Depth, float Scale);
    private readonly record struct HitNode(int Index, Vector2 Position, float Radius, float Depth);
    private readonly record struct AggregateHit(int ParentIndex, Vector2 Position, Vector2 HalfSize);
    private readonly record struct ActiveActivity(FileActivity Activity, DateTimeOffset ExpiresAt);
    private enum ActivityVisualizationMode { TravellingNeurons, NodeGlowOnly }
    private readonly record struct MeshVisibilityOptions(
        bool ShowMajorDirectories,
        bool ShowDeepDirectories,
        bool ShowAggregateNodes,
        bool ShowIndividualFiles,
        bool ShowSystemNodes,
        bool ShowActiveOperations,
        bool ShowDirectoryConnections);
    private sealed record MeshAppearanceSettings(
        double GeneralScale = 100,
        double FileScale = 100,
        double DirectoryScale = 100,
        string DirectoryColor = "#5C96FF",
        string FileColor = "#3DDAFF",
        string SelectionColor = "#36FF89",
        string AccessColor = "#00E5FF",
        string WriteColor = "#FFB020",
        string CreateColor = "#40EE87",
        string MoveColor = "#B65CFF",
        string DeleteColor = "#FF303A",
        string TransferStyle = "Glow",
        bool? TransferStyleExplicitlyChosen = null,
        bool? ShowMajorDirectories = null,
        bool? ShowDeepDirectories = null,
        bool? ShowAggregateNodes = null,
        bool? ShowIndividualFiles = null,
        bool? ShowSystemNodes = null,
        bool? ShowActiveOperations = null,
        bool? ShowDirectoryConnections = null,
        bool? ColorCodeByDrive = null,
        Dictionary<string, string>? DriveColors = null,
        bool? AutoOrbit = null,
        bool? ReverseOrbit = null,
        double OrbitSpeed = 100,
        double OrbitPhase = 0,
        double OrbitTilt = 10);
    private sealed record LiveInspectorEntry(string Name, string Path, bool IsDirectory, long SizeBytes);
    private readonly record struct InspectorLaunchTarget(string Path, bool IsDirectory);
    private readonly record struct LabelBounds(float Left, float Top, float Right, float Bottom)
    {
        public bool Intersects(LabelBounds other) =>
            Left < other.Right && Right > other.Left && Top < other.Bottom && Bottom > other.Top;
    }

    public MeshBrainControl()
    {
        InitializeComponent();
        // Keep the control state and the visible checkbox state synchronized even
        // when an incremental XAML build restores an older generated checkbox value.
        ShowMajorDirectoriesCheckBox.IsChecked = true;
        ShowDeepDirectoriesCheckBox.IsChecked = true;
        ShowAggregateNodesCheckBox.IsChecked = true;
        ShowIndividualFilesCheckBox.IsChecked = true;
        ShowSystemNodesCheckBox.IsChecked = false;
        ShowActiveOperationsCheckBox.IsChecked = true;
        ShowDirectoryConnectionsCheckBox.IsChecked = true;
        ColorCodeByDriveCheckBox.IsChecked = true;
        LoadMeshAppearance();
        _appearanceReady = true;
        ApplyMeshAppearance(save: false);
        ApplyMeshVisibilityOptions();
        RefreshDetectedDriveControls();
        // The filesystem field can contain thousands of projected nodes. A 15 FPS
        // ambient cadence prevents it from monopolizing the compositor while still
        // providing responsive orbit and zoom feedback.
        MeshCanvas.TargetElapsedTime = TimeSpan.FromMilliseconds(66.667);
        MeshCanvas.AddHandler(PointerPressedEvent,
            new PointerEventHandler(InteractionSurface_PointerPressed), true);
        MeshCanvas.AddHandler(PointerMovedEvent,
            new PointerEventHandler(InteractionSurface_PointerMoved), true);
        MeshCanvas.AddHandler(PointerReleasedEvent,
            new PointerEventHandler(InteractionSurface_PointerReleased), true);
        MeshCanvas.AddHandler(PointerCanceledEvent,
            new PointerEventHandler(InteractionSurface_PointerCanceled), true);
        MeshCanvas.AddHandler(PointerCaptureLostEvent,
            new PointerEventHandler(InteractionSurface_PointerCaptureLost), true);
        MeshCanvas.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler(InteractionSurface_PointerWheelChanged), true);
        MeshCanvas.AddHandler(DoubleTappedEvent,
            new DoubleTappedEventHandler(InteractionSurface_DoubleTapped), true);
        SearchResultsHideStoryboard.Completed += (_, _) =>
        {
            if (!_searchResultsRequested)
            {
                SearchResultsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
                SearchResultsPanel.IsHitTestVisible = false;
            }
        };
        SearchDockExpandStoryboard.Completed += (_, _) =>
        {
            if (!_searchDockExpanded) return;
            MeshSearchDock.Width = 366;
            MeshSearchInputLayer.Opacity = 1;
            MeshSearchInputLayer.IsHitTestVisible = true;
        };
        SearchDockCollapseStoryboard.Completed += (_, _) =>
        {
            if (_searchDockExpanded) return;
            MeshSearchDock.Width = 54;
            MeshSearchInputLayer.Opacity = 0;
            MeshSearchInputLayer.IsHitTestVisible = false;
        };
        MeshSettingsHideStoryboard.Completed += (_, _) =>
        {
            if (_meshSettingsRequested) return;
            MeshVisibilityPanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            MeshVisibilityPanel.IsHitTestVisible = false;
        };
        AlienIconPulseStoryboard.Begin();
        Unloaded += (_, _) =>
        {
            DetachFileActivity();
            StopFilesystemIndex();
            _fileNodeSprite?.Dispose();
            _directoryNodeSprite?.Dispose();
            _neuronFiberSprite?.Dispose();
            MeshCanvas.RemoveFromVisualTree();
        };
    }

    private void MeshCanvas_CreateResources(CanvasAnimatedControl sender, CanvasCreateResourcesEventArgs args)
    {
        _fileNodeSprite?.Dispose();
        _directoryNodeSprite?.Dispose();
        _neuronFiberSprite?.Dispose();

        _fileNodeSprite = new CanvasRenderTarget(sender, 32, 32, sender.Dpi);
        using (var spriteSession = _fileNodeSprite.CreateDrawingSession())
        {
            spriteSession.Clear(Color.FromArgb(0, 0, 0, 0));
            spriteSession.FillCircle(16, 16, 14, Color.FromArgb(255, 255, 255, 255));
        }

        _directoryNodeSprite = new CanvasRenderTarget(sender, 32, 32, sender.Dpi);
        using var ringSession = _directoryNodeSprite.CreateDrawingSession();
        ringSession.Clear(Color.FromArgb(0, 0, 0, 0));
        ringSession.DrawCircle(16, 16, 13, Color.FromArgb(255, 255, 255, 255), 2.4f);

        _neuronFiberSprite = new CanvasRenderTarget(sender, 64, 12, sender.Dpi);
        using var fiberSession = _neuronFiberSprite.CreateDrawingSession();
        fiberSession.Clear(Color.FromArgb(0, 0, 0, 0));
        using var fiberPath = new CanvasPathBuilder(sender);
        fiberPath.BeginFigure(new Vector2(1, 6));
        fiberPath.AddCubicBezier(new Vector2(18, 1.5f), new Vector2(45, 10.5f), new Vector2(63, 6));
        fiberPath.EndFigure(CanvasFigureLoop.Open);
        using var fiberGeometry = CanvasGeometry.CreatePath(fiberPath);
        fiberSession.DrawGeometry(fiberGeometry, Color.FromArgb(255, 255, 255, 255), 1f);
    }

    private void MeshVisibilityButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _meshSettingsRequested = MeshVisibilityPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible ||
                                 MeshVisibilityPanel.Opacity < 0.5;
        if (_meshSettingsRequested)
        {
            RefreshDetectedDriveControls();
            MeshVisibilityPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            MeshVisibilityPanel.IsHitTestVisible = true;
            MeshSettingsHideStoryboard.Stop();
            MeshSettingsShowStoryboard.Begin();
            return;
        }

        MeshVisibilityPanel.IsHitTestVisible = false;
        MeshSettingsShowStoryboard.Stop();
        MeshSettingsHideStoryboard.Begin();
    }

    private void MeshVisibilityOption_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ApplyMeshVisibilityOptions();
    }

    private static string MeshAppearancePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXUS", "mesh-appearance.json");

    private void MeshAppearance_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_appearanceReady) ApplyMeshAppearance(save: true);
    }

    private void OrbitSetting_Changed(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ApplyOrbitSettings(save: true, resetManualOrbit: true);

    private void OrbitSetting_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_appearanceReady) ApplyOrbitSettings(save: true, resetManualOrbit: true);
    }

    private void ApplyOrbitSettings(bool save, bool resetManualOrbit)
    {
        _autoOrbit = AutoOrbitCheckBox.IsChecked == true;
        _reverseOrbit = ReverseOrbitCheckBox.IsChecked == true;
        _orbitSpeed = (float)(OrbitSpeedSlider.Value / 100d);
        _orbitPhaseRadians = MathF.PI / 180f * (float)OrbitPhaseSlider.Value;
        _orbitTiltRadians = MathF.PI / 180f * (float)OrbitTiltSlider.Value;
        OrbitSpeedValueText.Text = $"{OrbitSpeedSlider.Value:0}%";
        OrbitPhaseValueText.Text = $"{OrbitPhaseSlider.Value:0}°";
        OrbitTiltValueText.Text = $"{OrbitTiltSlider.Value:0}°";
        if (resetManualOrbit)
        {
            _manualOrbitActivated = false;
            _orbitOrientation = Quaternion.Identity;
            _animationTime = 0;
        }
        if (save && _appearanceReady) SaveMeshAppearance();
        MeshCanvas?.Invalidate();
    }

    private void TransferStyleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        _transferStyleExplicitlyChosen = true;
        ApplyMeshAppearance(save: true);
    }

    private void ApplyMeshColors_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) =>
        ApplyMeshAppearance(save: true);

    private void MeshColorPicker_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string target) return;
        var textBox = target switch
        {
            "Directory" => DirectoryColorTextBox,
            "File" => FileColorTextBox,
            "Selection" => SelectionColorTextBox,
            "Access" => AccessColorTextBox,
            "Write" => WriteColorTextBox,
            "Create" => CreateColorTextBox,
            "Move" => MoveColorTextBox,
            "Delete" => DeleteColorTextBox,
            _ => null
        };
        if (textBox is null) return;
        var initial = ParseColor(textBox.Text, Color.FromArgb(255, 255, 255, 255));
        ShowColorPicker(button, initial, color =>
        {
            textBox.Text = ColorText(color);
            ApplyMeshAppearance(save: true);
        });
    }

    private static void ShowColorPicker(Button anchor, Color initial, Action<Color> changed)
    {
        var picker = new ColorPicker
        {
            Color = initial,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsColorChannelTextInputVisible = true,
            IsHexInputVisible = true,
            IsMoreButtonVisible = true,
            MinWidth = 286
        };
        picker.ColorChanged += (_, args) => changed(args.NewColor);
        var flyout = new Flyout { Content = picker };
        flyout.ShowAt(anchor);
    }

    private void RefreshDetectedDriveControls()
    {
        DetectedDrivesPanel.Children.Clear();
        try
        {
            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady &&
                         drive.DriveType is DriveType.Fixed or DriveType.Removable))
            {
                var root = drive.RootDirectory.FullName;
                var color = DriveColor(root);
                var row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1,
                    Microsoft.UI.Xaml.GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(100) });
                var label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? root.ToUpperInvariant()
                        : $"{root.ToUpperInvariant()}  {drive.VolumeLabel.ToUpperInvariant()}",
                    FontSize = 7,
                    Foreground = new SolidColorBrush(Color.FromArgb(210, 255, 209, 112)),
                    VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                    TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis
                };
                var pick = new Button
                {
                    Content = ColorText(color),
                    Tag = root,
                    Height = 27,
                    FontSize = 7,
                    Background = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B)),
                    BorderBrush = new SolidColorBrush(color),
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 250, 252))
                };
                pick.Click += DetectedDriveColor_Click;
                Grid.SetColumn(pick, 1);
                row.Children.Add(label);
                row.Children.Add(pick);
                DetectedDrivesPanel.Children.Add(row);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void DetectedDriveColor_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string root) return;
        ShowColorPicker(button, DriveColor(root), color =>
        {
            _driveColors[root] = ColorText(color);
            button.Content = ColorText(color);
            button.Background = new SolidColorBrush(Color.FromArgb(90, color.R, color.G, color.B));
            button.BorderBrush = new SolidColorBrush(color);
            SaveMeshAppearance();
            MeshCanvas.Invalidate();
        });
    }

    private void ApplyMeshAppearance(bool save)
    {
        _generalNodeScale = (float)(GeneralNodeScaleSlider.Value / 100d);
        _fileNodeScale = (float)(FileNodeScaleSlider.Value / 100d);
        _directoryNodeScale = (float)(DirectoryNodeScaleSlider.Value / 100d);
        _directoryColor = ParseColor(DirectoryColorTextBox.Text, _directoryColor);
        _fileColor = ParseColor(FileColorTextBox.Text, _fileColor);
        _selectionColor = ParseColor(SelectionColorTextBox.Text, _selectionColor);
        _accessColor = ParseColor(AccessColorTextBox.Text, _accessColor);
        _writeColor = ParseColor(WriteColorTextBox.Text, _writeColor);
        _createColor = ParseColor(CreateColorTextBox.Text, _createColor);
        _moveColor = ParseColor(MoveColorTextBox.Text, _moveColor);
        _deleteColor = ParseColor(DeleteColorTextBox.Text, _deleteColor);
        _activityVisualizationMode = TransferStyleComboBox.SelectedIndex == 1
            ? ActivityVisualizationMode.NodeGlowOnly
            : ActivityVisualizationMode.TravellingNeurons;
        ApplyOrbitSettings(save: false, resetManualOrbit: false);
        if (save) SaveMeshAppearance();
        MeshCanvas?.Invalidate();
    }

    private void LoadMeshAppearance()
    {
        try
        {
            if (!File.Exists(MeshAppearancePath)) return;
            var settings = JsonSerializer.Deserialize<MeshAppearanceSettings>(File.ReadAllText(MeshAppearancePath));
            if (settings is null) return;
            GeneralNodeScaleSlider.Value = settings.GeneralScale;
            FileNodeScaleSlider.Value = settings.FileScale;
            DirectoryNodeScaleSlider.Value = settings.DirectoryScale;
            DirectoryColorTextBox.Text = settings.DirectoryColor;
            FileColorTextBox.Text = settings.FileColor;
            SelectionColorTextBox.Text = settings.SelectionColor;
            AccessColorTextBox.Text = settings.AccessColor;
            WriteColorTextBox.Text = settings.WriteColor;
            CreateColorTextBox.Text = settings.CreateColor;
            MoveColorTextBox.Text = settings.MoveColor;
            DeleteColorTextBox.Text = settings.DeleteColor;
            _transferStyleExplicitlyChosen = settings.TransferStyleExplicitlyChosen == true;
            TransferStyleComboBox.SelectedIndex = _transferStyleExplicitlyChosen &&
                                                   string.Equals(settings.TransferStyle, "Travel",
                                                       StringComparison.OrdinalIgnoreCase)
                ? 0 : 1;
            ShowMajorDirectoriesCheckBox.IsChecked = settings.ShowMajorDirectories ?? true;
            ShowDeepDirectoriesCheckBox.IsChecked = settings.ShowDeepDirectories ?? true;
            ShowAggregateNodesCheckBox.IsChecked = settings.ShowAggregateNodes ?? true;
            ShowIndividualFilesCheckBox.IsChecked = settings.ShowIndividualFiles ?? true;
            ShowSystemNodesCheckBox.IsChecked = settings.ShowSystemNodes ?? false;
            ShowActiveOperationsCheckBox.IsChecked = settings.ShowActiveOperations ?? true;
            ShowDirectoryConnectionsCheckBox.IsChecked = settings.ShowDirectoryConnections ?? true;
            ColorCodeByDriveCheckBox.IsChecked = settings.ColorCodeByDrive ?? true;
            AutoOrbitCheckBox.IsChecked = settings.AutoOrbit ?? true;
            ReverseOrbitCheckBox.IsChecked = settings.ReverseOrbit ?? false;
            OrbitSpeedSlider.Value = Math.Clamp(settings.OrbitSpeed, 0, 300);
            OrbitPhaseSlider.Value = Math.Clamp(settings.OrbitPhase, 0, 360);
            OrbitTiltSlider.Value = Math.Clamp(settings.OrbitTilt, -45, 45);
            _driveColors.Clear();
            if (settings.DriveColors is not null)
                foreach (var (root, color) in settings.DriveColors)
                    _driveColors[root] = color;
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void SaveMeshAppearance()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MeshAppearancePath)!);
            var settings = new MeshAppearanceSettings(
                GeneralNodeScaleSlider.Value, FileNodeScaleSlider.Value, DirectoryNodeScaleSlider.Value,
                ColorText(_directoryColor), ColorText(_fileColor), ColorText(_selectionColor),
                ColorText(_accessColor), ColorText(_writeColor), ColorText(_createColor),
                ColorText(_moveColor), ColorText(_deleteColor),
                _activityVisualizationMode == ActivityVisualizationMode.NodeGlowOnly ? "Glow" : "Travel",
                _transferStyleExplicitlyChosen,
                ShowMajorDirectoriesCheckBox.IsChecked == true,
                ShowDeepDirectoriesCheckBox.IsChecked == true,
                ShowAggregateNodesCheckBox.IsChecked == true,
                ShowIndividualFilesCheckBox.IsChecked == true,
                ShowSystemNodesCheckBox.IsChecked == true,
                ShowActiveOperationsCheckBox.IsChecked == true,
                ShowDirectoryConnectionsCheckBox.IsChecked == true,
                ColorCodeByDriveCheckBox.IsChecked == true,
                new Dictionary<string, string>(_driveColors, StringComparer.OrdinalIgnoreCase),
                AutoOrbitCheckBox.IsChecked == true,
                ReverseOrbitCheckBox.IsChecked == true,
                OrbitSpeedSlider.Value,
                OrbitPhaseSlider.Value,
                OrbitTiltSlider.Value);
            File.WriteAllText(MeshAppearancePath, JsonSerializer.Serialize(settings));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static Color ParseColor(string? text, Color fallback)
    {
        var value = text?.Trim().TrimStart('#');
        if (value?.Length != 6 || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb)) return fallback;
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private static string ColorText(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void ResetMeshVisibility_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        ShowMajorDirectoriesCheckBox.IsChecked = true;
        ShowDeepDirectoriesCheckBox.IsChecked = true;
        ShowAggregateNodesCheckBox.IsChecked = true;
        ShowIndividualFilesCheckBox.IsChecked = true;
        ShowSystemNodesCheckBox.IsChecked = false;
        ShowActiveOperationsCheckBox.IsChecked = true;
        ShowDirectoryConnectionsCheckBox.IsChecked = true;
        ColorCodeByDriveCheckBox.IsChecked = true;
        _driveColors.Clear();
        _appearanceReady = false;
        GeneralNodeScaleSlider.Value = 100;
        FileNodeScaleSlider.Value = 100;
        DirectoryNodeScaleSlider.Value = 100;
        DirectoryColorTextBox.Text = "#5C96FF";
        FileColorTextBox.Text = "#3DDAFF";
        SelectionColorTextBox.Text = "#36FF89";
        AccessColorTextBox.Text = "#00E5FF";
        WriteColorTextBox.Text = "#FFB020";
        CreateColorTextBox.Text = "#40EE87";
        MoveColorTextBox.Text = "#B65CFF";
        DeleteColorTextBox.Text = "#FF303A";
        _transferStyleExplicitlyChosen = false;
        TransferStyleComboBox.SelectedIndex = 1;
        AutoOrbitCheckBox.IsChecked = true;
        ReverseOrbitCheckBox.IsChecked = false;
        OrbitSpeedSlider.Value = 100;
        OrbitPhaseSlider.Value = 0;
        OrbitTiltSlider.Value = 10;
        _appearanceReady = true;
        ApplyMeshAppearance(save: true);
        ApplyMeshVisibilityOptions();
    }

    private void ApplyMeshVisibilityOptions()
    {
        lock (_graphGate)
        {
            _showMajorDirectories = ShowMajorDirectoriesCheckBox.IsChecked == true;
            _showDeepDirectories = ShowDeepDirectoriesCheckBox.IsChecked == true;
            _showAggregateNodes = ShowAggregateNodesCheckBox.IsChecked == true;
            _showIndividualFiles = ShowIndividualFilesCheckBox.IsChecked == true;
            _showSystemNodes = ShowSystemNodesCheckBox.IsChecked == true;
            _showActiveOperations = ShowActiveOperationsCheckBox.IsChecked == true;
            _showDirectoryConnections = ShowDirectoryConnectionsCheckBox.IsChecked == true;
            _colorCodeByDrive = ColorCodeByDriveCheckBox.IsChecked == true;
            _hitNodes = [];
            _aggregateHits = [];
            _visibleNodeCache = [];
            _visibleNodeCacheOptions = null;
            _visibleNodeCacheSourceCount = -1;
            _visibleNodeCacheBudget = -1;
        }

        var layers = 1 + (_showMajorDirectories ? 1 : 0) + (_showDeepDirectories ? 1 : 0) +
                     (_showAggregateNodes ? 1 : 0) + (_showIndividualFiles ? 1 : 0) +
                     (_showSystemNodes ? 1 : 0) + (_showActiveOperations ? 1 : 0) +
                     (_showDirectoryConnections ? 1 : 0);
        MeshVisibilitySummaryText.Text =
            $"{layers} LAYERS // FILE PARTICLES {(_showIndividualFiles ? "ON" : "OFF")}";
        if (_appearanceReady) SaveMeshAppearance();
        MeshCanvas.Invalidate();
    }

    public BrainState State
    {
        get => _state;
        set
        {
            _state = value;
            MeshStateText.Text = value switch
            {
                BrainState.AwaitingApproval => "APPROVAL REQUIRED",
                BrainState.Understanding => "UNDERSTANDING",
                _ => value.ToString().ToUpperInvariant()
            };
            MeshStateText.Foreground = new SolidColorBrush(StateAccent(245));
        }
    }

    public void UpdateTelemetry(SystemSnapshot snapshot)
    {
        _cpu = (float)snapshot.CpuPercent;
        _memory = (float)snapshot.MemoryPercent;
        _disk = (float)snapshot.DiskPercent;
        _network = snapshot.NetworkAvailable;
    }

    public void BeginFilesystemIndex(FileGraphService service)
    {
        StopFilesystemIndex();
        while (_refreshSignal.Wait(0)) { }
        _indexCancellation = new CancellationTokenSource();
        _ = IndexFilesystemAsync(service, _indexCancellation.Token);
    }

    public void AttachFileActivity(FileActivityService service)
    {
        DetachFileActivity();
        _fileActivityService = service;
        service.ActivityDetected += FileActivity_ActivityDetected;
    }

    public void DetachFileActivity()
    {
        if (_fileActivityService is not null)
            _fileActivityService.ActivityDetected -= FileActivity_ActivityDetected;
        _fileActivityService = null;
        lock (_graphGate) _activities = [];
    }

    public void StopFilesystemIndex()
    {
        _indexCancellation?.Cancel();
        _indexCancellation?.Dispose();
        _indexCancellation = null;
    }

    private void FileActivity_ActivityDetected(object? sender, FileActivityEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() => RegisterActivity(e.Activity));
    }

    private void RegisterActivity(FileActivity activity)
    {
        if (activity.Source is "DRIVE_ATTACH" or "DRIVE_DETACH")
            RefreshDetectedDriveControls();
        var lifetime = activity.Kind switch
        {
            FileActivityKind.Accessed => TimeSpan.FromSeconds(2.2),
            FileActivityKind.Modified => TimeSpan.FromSeconds(2.8),
            FileActivityKind.Created => TimeSpan.FromSeconds(4),
            FileActivityKind.Deleted => TimeSpan.FromSeconds(4),
            FileActivityKind.Moved => TimeSpan.FromSeconds(5),
            _ => TimeSpan.FromSeconds(3)
        };
        lock (_graphGate)
        {
            _activities.RemoveAll(item => item.Activity.Path.Equals(activity.Path, StringComparison.OrdinalIgnoreCase));
            _activities.Add(new ActiveActivity(activity, DateTimeOffset.UtcNow + lifetime));
            if (_activities.Count > 10) _activities.RemoveRange(0, _activities.Count - 10);
        }

        var isIndexCacheWrite = Path.GetFileName(activity.Path)
            .StartsWith("filesystem-index-v1.bin", StringComparison.OrdinalIgnoreCase);
        var nexusDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXUS") +
            Path.DirectorySeparatorChar;
        var isInternalDataWrite = activity.Path.StartsWith(nexusDataDirectory, StringComparison.OrdinalIgnoreCase);
        if (!isIndexCacheWrite && !isInternalDataWrite &&
            activity.Kind is (FileActivityKind.Moved or FileActivityKind.Created or FileActivityKind.Deleted))
        {
            _pendingIndexChanges.Enqueue(activity);
            try { _refreshSignal.Release(); } catch (SemaphoreFullException) { }
        }
        MeshCanvas.Invalidate();
    }

    public void SetAnimationPaused(bool paused) => MeshCanvas.Paused = paused;

    private void InteractionSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is Microsoft.UI.Xaml.DependencyObject source &&
            IsDescendantOf(source, SelectedNodePanel)) return;
        var point = e.GetCurrentPoint(MeshCanvas);
        if (!point.Properties.IsLeftButtonPressed) return;
        MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        _orbitDragging = true;
        _dragPointerId = e.Pointer.PointerId;
        _lastDragPosition = new Vector2((float)point.Position.X, (float)point.Position.Y);
        _dragTravel = 0;
        if (!_manualOrbitActivated)
        {
            // Preserve the current ambient pose when manual globe control takes over.
            _orbitOrientation = Quaternion.CreateFromRotationMatrix(CreateAmbientRotation(_animationTime));
            _manualOrbitActivated = true;
        }
        MeshCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void InteractionSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_orbitDragging || e.Pointer.PointerId != _dragPointerId) return;
        var point = e.GetCurrentPoint(MeshCanvas);
        var position = new Vector2((float)point.Position.X, (float)point.Position.Y);
        var delta = position - _lastDragPosition;
        _lastDragPosition = position;
        _dragTravel += delta.Length();

        // Screen-space rotation keeps the same radians-per-pixel response at every
        // zoom level and at every pointer position. The former arcball multiplied
        // its radius by zoom, which made close-up navigation feel progressively stuck.
        if (delta.LengthSquared() > 0.000001f)
        {
            const float radiansPerPixel = 0.0062f;
            var dragRotation = Quaternion.CreateFromYawPitchRoll(
                -delta.X * radiansPerPixel, -delta.Y * radiansPerPixel, 0f);
            _orbitOrientation = Quaternion.Normalize(
                Quaternion.Concatenate(_orbitOrientation, dragRotation));
        }
        MeshCanvas.Invalidate();
        e.Handled = true;
    }

    private void InteractionSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndOrbitDrag(e);
    }

    private void InteractionSurface_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndOrbitDrag(e);
    }

    private void InteractionSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId == _dragPointerId)
        {
            _orbitDragging = false;
        }
    }

    private void EndOrbitDrag(PointerRoutedEventArgs e)
    {
        if (!_orbitDragging || e.Pointer.PointerId != _dragPointerId) return;
        var position = e.GetCurrentPoint(MeshCanvas).Position;
        _orbitDragging = false;
        MeshCanvas.ReleasePointerCapture(e.Pointer);
        if (_dragTravel <= 7f)
            SelectNodeAt(new Vector2((float)position.X, (float)position.Y));
        e.Handled = true;
    }

    private void InteractionSurface_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(MeshCanvas).Properties.MouseWheelDelta;
        // Wheel-up always moves inward; wheel-down moves outward.
        var factor = delta > 0 ? 1.28f : 1f / 1.28f;
        _targetZoom = Math.Clamp(_targetZoom * factor, 0.10f, 4096f);
        ZoomReadout.Text = $"{_targetZoom * 100:0}%";
        MeshCanvas.Invalidate();
        e.Handled = true;
    }

    private void InteractionSurface_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _orbitOrientation = Quaternion.Identity;
        _manualOrbitActivated = false;
        _animationTime = 0f;
        _targetZoom = 1f;
        ClearSelection();
        ZoomReadout.Text = "100%";
        MeshCanvas.Invalidate();
        e.Handled = true;
    }

    private void ReleaseSelection_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => ClearSelection();

    private void SelectNodeAt(Vector2 pointer)
    {
        List<HitNode> hits;
        List<AggregateHit> aggregateHits;
        lock (_graphGate)
        {
            hits = _hitNodes;
            aggregateHits = _aggregateHits;
        }
        var aggregate = aggregateHits
            .Where(hit => MathF.Abs(hit.Position.X - pointer.X) <= hit.HalfSize.X &&
                          MathF.Abs(hit.Position.Y - pointer.Y) <= hit.HalfSize.Y)
            .OrderBy(hit => Vector2.DistanceSquared(hit.Position, pointer))
            .FirstOrDefault(new AggregateHit(-1, default, default));
        if (aggregate.ParentIndex >= 0)
        {
            ExpandAggregate(aggregate.ParentIndex);
            return;
        }
        var selected = hits
            .Where(hit => Vector2.DistanceSquared(hit.Position, pointer) <= hit.Radius * hit.Radius)
            .OrderByDescending(hit => hit.Depth)
            .ThenBy(hit => Vector2.DistanceSquared(hit.Position, pointer))
            .FirstOrDefault(new HitNode(-1, default, 0, 0));

        if (selected.Index < 0)
        {
            ClearSelection();
            return;
        }

        VisualNode node;
        lock (_graphGate)
        {
            if (selected.Index >= _nodes.Count) return;
            _selectedNodeIndex = selected.Index;
            _inspectorNodeIndex = -1;
            _expandedInspectorNodes.Clear();
            _expandedLiveInspectorPaths.Clear();
            _liveInspectorSelection = null;
            _inspectorFilterQuery = string.Empty;
            _inspectorFilterMatchIndices.Clear();
            node = _nodes[selected.Index];
        }
        if (!string.IsNullOrEmpty(NodeChildFilterBox.Text)) NodeChildFilterBox.Text = string.Empty;
        ShowSelectedNode(node);
        MeshCanvas.Invalidate();
    }

    private void ExpandAggregate(int parentIndex)
    {
        VisualNode node;
        lock (_graphGate)
        {
            if (parentIndex < 0 || parentIndex >= _nodes.Count) return;
            _aggregateExpansion[parentIndex] = Math.Min(2048,
                _aggregateExpansion.GetValueOrDefault(parentIndex) + 128);
            _selectedNodeIndex = parentIndex;
            node = _nodes[parentIndex];
        }
        _targetZoom = Math.Max(_targetZoom, 2.2f);
        ZoomReadout.Text = $"{_targetZoom * 100:0}%";
        ShowSelectedNode(node);
        MeshCanvas.Invalidate();
    }

    private void ShowSelectedNode(VisualNode node)
    {
        List<VisualNode> nodes;
        int selectedIndex;
        lock (_graphGate)
        {
            nodes = _nodes;
            selectedIndex = _selectedNodeIndex;
        }
        if (selectedIndex < 0 || selectedIndex >= nodes.Count ||
            !nodes[selectedIndex].Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase))
            selectedIndex = nodes.FindIndex(candidate => candidate.Path.Equals(node.Path, StringComparison.OrdinalIgnoreCase));

        List<int> children = selectedIndex < 0
            ? []
            : Enumerable.Range(0, nodes.Count).Where(index => nodes[index].ParentIndex == selectedIndex).ToList();
        List<int> descendants = selectedIndex < 0
            ? []
            : Enumerable.Range(0, nodes.Count).Where(index => IsNodeDescendantOf(nodes, index, selectedIndex)).ToList();
        var subtreeFiles = descendants.Count(index => !nodes[index].IsDirectory);
        var subtreeDirectories = descendants.Count(index => nodes[index].IsDirectory);
        var indexedSize = node.IsDirectory
            ? descendants.Where(index => !nodes[index].IsDirectory).Sum(index => nodes[index].SizeBytes)
            : node.SizeBytes;

        SelectedNodeTypeText.Text = node.IsRoot
            ? "ROOT DIRECTORY // INDEX ORIGIN"
            : node.IsDirectory ? "DIRECTORY HUB // STRUCTURAL LINK" : "FILE SIGNAL // DATA PARTICLE";
        SelectedNodeNameText.Text = node.Name.ToUpperInvariant();
        SelectedNodePathText.Text = node.Path;
        SelectedNodeClassValue.Text = node.IsDirectory
            ? node.IsRoot ? "ROOT DIRECTORY" : "DIRECTORY"
            : $"FILE  /  {(string.IsNullOrWhiteSpace(Path.GetExtension(node.Name)) ? "NO EXTENSION" : Path.GetExtension(node.Name).ToUpperInvariant())}";
        SelectedNodeDepthValue.Text = node.Depth.ToString("00");
        SelectedNodeSizeValue.Text = FormatBytes(indexedSize);
        SelectedNodeChildrenValue.Text = node.IsDirectory
            ? $"{children.Count:000} INDEXED  /  {node.ChildCount:000} DISCOVERED"
            : "NONE";
        SelectedNodeSubtreeValue.Text = node.IsDirectory
            ? $"{subtreeFiles:000} FILE  /  {subtreeDirectories:000} DIR"
            : "ATOMIC FILE SIGNAL";
        SelectedNodeModifiedValue.Text = GetLastModifiedText(node);
        SelectedNodeChildRomanText.Text = ToRomanNumeral(children.Count);
        SelectedNodeChildrenSummaryText.Text = node.IsDirectory
            ? $"{children.Count:000} INDEXED CHILD SIGNALS  /  SELECT TO INSPECT"
            : "ATOMIC FILE SIGNAL  /  NO CHILDREN";
        SelectedNodeDetailsText.Text = File.Exists(node.Path) || Directory.Exists(node.Path)
            ? "NODE ONLINE"
            : "INDEXED / CURRENTLY UNAVAILABLE";
        RefreshInspectorFilterMatches(nodes, selectedIndex);
        PopulateSelectedNodeChildren(nodes, children);
        ApplyNodeInspectorTabState();
        UpdateNodeLaunchButtons();
        SelectedNodePanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private static bool IsNodeDescendantOf(IReadOnlyList<VisualNode> nodes, int candidateIndex, int ancestorIndex)
    {
        if (candidateIndex == ancestorIndex) return false;
        var parent = nodes[candidateIndex].ParentIndex;
        var guard = 0;
        while (parent >= 0 && parent < nodes.Count && guard++ < nodes.Count)
        {
            if (parent == ancestorIndex) return true;
            parent = nodes[parent].ParentIndex;
        }
        return false;
    }

    private static string GetLastModifiedText(VisualNode node)
    {
        try
        {
            var modified = node.IsDirectory
                ? Directory.GetLastWriteTime(node.Path)
                : File.GetLastWriteTime(node.Path);
            return modified.Year <= 1601 ? "UNKNOWN" : modified.ToString("yyyy-MM-dd  HH:mm");
        }
        catch
        {
            return "UNAVAILABLE";
        }
    }

    private void PopulateSelectedNodeChildren(IReadOnlyList<VisualNode> nodes, IReadOnlyList<int> childIndices)
    {
        SelectedNodeChildrenList.Children.Clear();
        string filterQuery;
        HashSet<int> filterMatches;
        int primaryIndex;
        int inspectorIndex;
        lock (_graphGate)
        {
            filterQuery = _inspectorFilterQuery;
            filterMatches = new HashSet<int>(_inspectorFilterMatchIndices);
            primaryIndex = _selectedNodeIndex;
            inspectorIndex = _inspectorNodeIndex;
        }
        if (!string.IsNullOrWhiteSpace(filterQuery))
        {
            NodeFilterResultCountText.Text = ToRomanNumeral(filterMatches.Count);
            if (filterMatches.Count == 0)
            {
                SelectedNodeChildrenList.Children.Add(new TextBlock
                {
                    Text = "NO DESCENDANT SIGNALS MATCH THIS FILTER.",
                    FontSize = 7,
                    CharacterSpacing = 65,
                    Foreground = new SolidColorBrush(Color.FromArgb(185, 91, 148, 255)),
                    Margin = new Microsoft.UI.Xaml.Thickness(10, 18, 0, 0)
                });
                return;
            }

            var noExpansion = new HashSet<int>();
            var filteredVisited = new HashSet<int>();
            var primaryDepth = primaryIndex >= 0 && primaryIndex < nodes.Count ? nodes[primaryIndex].Depth : 0;
            foreach (var index in filterMatches
                .OrderBy(index => nodes[index].Depth)
                .ThenByDescending(index => nodes[index].IsDirectory)
                .ThenBy(index => nodes[index].Name, StringComparer.OrdinalIgnoreCase))
            {
                var relativeDepth = Math.Clamp(nodes[index].Depth - primaryDepth - 1, 0, 4);
                AddInspectorNodeRow(nodes, index, relativeDepth, inspectorIndex, noExpansion,
                    filteredVisited, filterMatches);
            }
            return;
        }
        NodeFilterResultCountText.Text = "—";
        if (childIndices.Count == 0)
        {
            SelectedNodeChildrenList.Children.Add(new TextBlock
            {
                Text = "NO INDEXED CHILD SIGNALS.",
                FontSize = 7,
                CharacterSpacing = 70,
                Foreground = new SolidColorBrush(Color.FromArgb(155, 133, 174, 145)),
                Margin = new Microsoft.UI.Xaml.Thickness(8, 18, 0, 0)
            });
            return;
        }

        HashSet<int> expandedNodes;
        lock (_graphGate)
        {
            expandedNodes = new HashSet<int>(_expandedInspectorNodes);
        }

        var visited = new HashSet<int>();
        foreach (var index in SortInspectorChildren(nodes, childIndices))
            AddInspectorNodeRow(nodes, index, 0, inspectorIndex, expandedNodes, visited, null);
    }

    private static IEnumerable<int> SortInspectorChildren(IReadOnlyList<VisualNode> nodes, IEnumerable<int> indices) =>
        indices.OrderByDescending(index => nodes[index].IsDirectory)
            .ThenBy(index => nodes[index].Name, StringComparer.OrdinalIgnoreCase);

    private void AddInspectorNodeRow(IReadOnlyList<VisualNode> nodes, int index, int depth,
        int inspectorIndex, IReadOnlySet<int> expandedNodes, ISet<int> visited,
        IReadOnlySet<int>? filterMatches)
    {
        if (index < 0 || index >= nodes.Count || depth > 10 || !visited.Add(index)) return;
        var child = nodes[index];
        var isInspectorSelection = index == inspectorIndex;
        var signalGreen = Color.FromArgb(255, 54, 255, 137);
        var filterBlue = Color.FromArgb(255, 61, 140, 255);
        var normalAmber = Color.FromArgb(230, 255, 176, 32);
        var rowAccent = isInspectorSelection
            ? signalGreen
            : filterMatches?.Contains(index) == true ? filterBlue : normalAmber;

        var button = new Button
        {
            Style = (Microsoft.UI.Xaml.Style)Resources["MeshSearchResultButtonStyle"],
            Tag = index,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            Margin = new Microsoft.UI.Xaml.Thickness(depth * 18, 0, 0, 4)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
            $"Inspect child {(child.IsDirectory ? "directory" : "file")} {child.Name} without changing primary focus");
        ToolTipService.SetToolTip(button, child.Path);
        button.Click += SelectedNodeChild_Click;

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(30) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        if (isInspectorSelection)
        {
            var selectionPlate = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, signalGreen.R, signalGreen.G, signalGreen.B)),
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(2),
                Margin = new Microsoft.UI.Xaml.Thickness(-8, 2, -6, 2)
            };
            Grid.SetColumnSpan(selectionPlate, 3);
            content.Children.Add(selectionPlate);
            var selectionBar = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = 3,
                Height = 31,
                Fill = new SolidColorBrush(signalGreen),
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(-8, 0, 0, 0)
            };
            Grid.SetColumnSpan(selectionBar, 3);
            content.Children.Add(selectionBar);
        }

        var marker = new Grid { Width = 18, Height = 18 };
        marker.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = child.IsDirectory ? 12 : 8,
            Height = child.IsDirectory ? 12 : 8,
            Stroke = new SolidColorBrush(rowAccent),
            StrokeThickness = isInspectorSelection ? 1.4 : 1,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = child.IsDirectory ? 45 : 0 }
        });
        marker.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = isInspectorSelection ? 4 : 3,
            Height = isInspectorSelection ? 4 : 3,
            Fill = new SolidColorBrush(rowAccent)
        });
        content.Children.Add(marker);

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = child.Name.ToUpperInvariant(),
            FontSize = 7.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 30,
            Foreground = new SolidColorBrush(isInspectorSelection
                ? signalGreen : Color.FromArgb(245, 232, 255, 239)),
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        labels.Children.Add(new TextBlock
        {
            Text = child.IsDirectory
                ? $"{child.ChildCount:000} DISCOVERED CHILDREN  /  {(expandedNodes.Contains(index) ? "EXPANDED" : "SELECT TO EXPAND")}" 
                : FormatBytes(child.SizeBytes),
            FontSize = 7,
            CharacterSpacing = 30,
            Foreground = new SolidColorBrush(isInspectorSelection
                ? Color.FromArgb(190, signalGreen.R, signalGreen.G, signalGreen.B)
                : Color.FromArgb(155, 132, 175, 145))
        });
        Grid.SetColumn(labels, 1);
        content.Children.Add(labels);

        var type = new TextBlock
        {
            Text = child.IsDirectory ? (expandedNodes.Contains(index) ? "DIR  −" : "DIR  +") : "FILE",
            FontSize = 7,
            CharacterSpacing = 70,
            Foreground = new SolidColorBrush(rowAccent),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(8, 0, 4, 0)
        };
        Grid.SetColumn(type, 2);
        content.Children.Add(type);
        button.Content = content;
        SelectedNodeChildrenList.Children.Add(button);

        if (!child.IsDirectory || !expandedNodes.Contains(index)) return;
        var nestedChildren = Enumerable.Range(0, nodes.Count).Where(candidate => nodes[candidate].ParentIndex == index).ToList();
        if (nestedChildren.Count == 0)
        {
            var liveChildren = ReadLiveInspectorEntries(child.Path);
            if (liveChildren.Count == 0)
            {
                SelectedNodeChildrenList.Children.Add(new TextBlock
                {
                    Text = "DIRECTORY IS EMPTY OR ACCESS IS UNAVAILABLE",
                    FontSize = 7,
                    CharacterSpacing = 45,
                    Foreground = new SolidColorBrush(Color.FromArgb(145, 169, 143, 83)),
                    Margin = new Microsoft.UI.Xaml.Thickness((depth + 1) * 18 + 13, 2, 0, 7)
                });
                return;
            }
            var liveVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var liveChild in liveChildren)
                AddLiveInspectorNodeRow(nodes, liveChild, depth + 1, liveVisited);
            return;
        }

        foreach (var nestedIndex in SortInspectorChildren(nodes, nestedChildren))
            AddInspectorNodeRow(nodes, nestedIndex, depth + 1, inspectorIndex, expandedNodes, visited, filterMatches);
    }

    private static List<LiveInspectorEntry> ReadLiveInspectorEntries(string directoryPath)
    {
        var entries = new List<LiveInspectorEntry>();
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directoryPath).Take(180))
            {
                try
                {
                    var attributes = File.GetAttributes(path);
                    if (attributes.HasFlag(FileAttributes.System) || attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var size = isDirectory ? 0 : new FileInfo(path).Length;
                    entries.Add(new LiveInspectorEntry(Path.GetFileName(path), path, isDirectory, size));
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }
        catch (IOException) { }

        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private void AddLiveInspectorNodeRow(IReadOnlyList<VisualNode> nodes, LiveInspectorEntry entry,
        int depth, ISet<string> visited)
    {
        if (depth > 10 || !visited.Add(entry.Path)) return;
        bool selected;
        bool expanded;
        lock (_graphGate)
        {
            selected = _liveInspectorSelection?.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase) == true;
            expanded = _expandedLiveInspectorPaths.Contains(entry.Path);
        }
        var green = Color.FromArgb(255, 54, 255, 137);
        var liveAmber = Color.FromArgb(235, 255, 196, 70);
        var accent = selected ? green : liveAmber;
        var button = new Button
        {
            Style = (Microsoft.UI.Xaml.Style)Resources["MeshSearchResultButtonStyle"],
            Tag = entry,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            Margin = new Microsoft.UI.Xaml.Thickness(depth * 18, 0, 0, 4)
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
            $"Inspect live {(entry.IsDirectory ? "directory" : "file")} {entry.Name}");
        ToolTipService.SetToolTip(button, entry.Path);
        button.Click += SelectedLiveInspectorChild_Click;

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(30) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });
        if (selected)
        {
            var plate = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, green.R, green.G, green.B)),
                CornerRadius = new Microsoft.UI.Xaml.CornerRadius(2),
                Margin = new Microsoft.UI.Xaml.Thickness(-8, 2, -6, 2)
            };
            Grid.SetColumnSpan(plate, 3);
            content.Children.Add(plate);
        }
        var marker = new Grid { Width = 18, Height = 18 };
        marker.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = entry.IsDirectory ? 12 : 8,
            Height = entry.IsDirectory ? 12 : 8,
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = selected ? 1.4 : 1,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = entry.IsDirectory ? 45 : 0 }
        });
        marker.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = selected ? 4 : 3,
            Height = selected ? 4 : 3,
            Fill = new SolidColorBrush(accent)
        });
        content.Children.Add(marker);

        var labels = new StackPanel { Spacing = 2, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
        labels.Children.Add(new TextBlock
        {
            Text = entry.Name.ToUpperInvariant(),
            FontSize = 7.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 30,
            Foreground = new SolidColorBrush(selected ? green : Color.FromArgb(245, 241, 250, 235)),
            TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
            MaxLines = 1
        });
        labels.Children.Add(new TextBlock
        {
            Text = entry.IsDirectory ? "LIVE DIRECTORY  /  ON-DEMAND CONTENT" : $"LIVE FILE  /  {FormatBytes(entry.SizeBytes)}",
            FontSize = 7,
            CharacterSpacing = 30,
            Foreground = new SolidColorBrush(Color.FromArgb(165, accent.R, accent.G, accent.B))
        });
        Grid.SetColumn(labels, 1);
        content.Children.Add(labels);

        var type = new TextBlock
        {
            Text = entry.IsDirectory ? (expanded ? "DIR  -" : "DIR  +") : "FILE",
            FontSize = 7,
            CharacterSpacing = 70,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            Margin = new Microsoft.UI.Xaml.Thickness(8, 0, 4, 0)
        };
        Grid.SetColumn(type, 2);
        content.Children.Add(type);
        button.Content = content;
        SelectedNodeChildrenList.Children.Add(button);

        if (!entry.IsDirectory || !expanded) return;
        var children = ReadLiveInspectorEntries(entry.Path);
        if (children.Count == 0)
        {
            SelectedNodeChildrenList.Children.Add(new TextBlock
            {
                Text = "DIRECTORY IS EMPTY OR ACCESS IS UNAVAILABLE",
                FontSize = 7,
                CharacterSpacing = 45,
                Foreground = new SolidColorBrush(Color.FromArgb(145, 169, 143, 83)),
                Margin = new Microsoft.UI.Xaml.Thickness((depth + 1) * 18 + 13, 2, 0, 7)
            });
            return;
        }
        foreach (var child in children) AddLiveInspectorNodeRow(nodes, child, depth + 1, visited);
    }

    private void SelectedLiveInspectorChild_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LiveInspectorEntry entry }) return;
        List<VisualNode> nodes;
        List<int> primaryChildren;
        lock (_graphGate)
        {
            if (_selectedNodeIndex < 0 || _selectedNodeIndex >= _nodes.Count) return;
            _liveInspectorSelection = entry;
            _inspectorNodeIndex = _nodes.FindIndex(node => node.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase));
            if (entry.IsDirectory)
            {
                if (!_expandedLiveInspectorPaths.Add(entry.Path)) _expandedLiveInspectorPaths.Remove(entry.Path);
            }
            nodes = _nodes;
            primaryChildren = Enumerable.Range(0, nodes.Count)
                .Where(index => nodes[index].ParentIndex == _selectedNodeIndex).ToList();
        }
        SelectedNodeChildrenSummaryText.Text = $"PRIMARY FOCUS LOCKED  /  LIVE BROWSE {entry.Name.ToUpperInvariant()}";
        SelectedNodeDetailsText.Text = entry.IsDirectory
            ? "LIVE DIRECTORY SIGNAL  /  ON-DEMAND CONTENT"
            : $"LIVE FILE SIGNAL  /  {FormatBytes(entry.SizeBytes)}";
        UpdateNodeLaunchButtons();
        PopulateSelectedNodeChildren(nodes, primaryChildren);
        MeshCanvas.Invalidate();
    }

    private void NodeChildFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        List<VisualNode> nodes;
        int selectedIndex;
        List<int> primaryChildren;
        lock (_graphGate)
        {
            _inspectorFilterQuery = NodeChildFilterBox.Text.Trim();
            nodes = _nodes;
            selectedIndex = _selectedNodeIndex;
            primaryChildren = selectedIndex < 0
                ? []
                : Enumerable.Range(0, nodes.Count).Where(index => nodes[index].ParentIndex == selectedIndex).ToList();
        }
        RefreshInspectorFilterMatches(nodes, selectedIndex);
        PopulateSelectedNodeChildren(nodes, primaryChildren);
        int matchCount;
        string query;
        lock (_graphGate)
        {
            matchCount = _inspectorFilterMatchIndices.Count;
            query = _inspectorFilterQuery;
        }
        SelectedNodeChildrenSummaryText.Text = string.IsNullOrWhiteSpace(query)
            ? $"{primaryChildren.Count:000} INDEXED CHILD SIGNALS  /  SELECT TO INSPECT"
            : $"{matchCount:000} FILTERED DESCENDANT SIGNALS  /  BLUE MESH MARKERS";
        MeshCanvas.Invalidate();
    }

    private void RefreshInspectorFilterMatches(IReadOnlyList<VisualNode> nodes, int selectedIndex)
    {
        string query;
        lock (_graphGate) query = _inspectorFilterQuery;
        HashSet<int> matches = string.IsNullOrWhiteSpace(query) || selectedIndex < 0 || selectedIndex >= nodes.Count
            ? []
            : Enumerable.Range(0, nodes.Count)
                .Where(index => IsNodeDescendantOf(nodes, index, selectedIndex))
                .Select(index => (Index: index, Score: ScoreSearchMatch(nodes[index], query)))
                .Where(match => match.Score < int.MaxValue)
                .OrderBy(match => match.Score)
                .Take(240)
                .Select(match => match.Index)
                .ToHashSet();
        lock (_graphGate) _inspectorFilterMatchIndices = matches;
    }

    private void SelectedNodeChild_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        List<VisualNode> nodes;
        List<int> primaryChildren;
        VisualNode browsedNode;
        lock (_graphGate)
        {
            if (index < 0 || index >= _nodes.Count) return;
            if (_selectedNodeIndex < 0 || _selectedNodeIndex >= _nodes.Count) return;
            _inspectorNodeIndex = index;
            _liveInspectorSelection = null;
            browsedNode = _nodes[index];
            if (browsedNode.IsDirectory)
            {
                if (!_expandedInspectorNodes.Add(index)) _expandedInspectorNodes.Remove(index);
            }
            nodes = _nodes;
            primaryChildren = Enumerable.Range(0, nodes.Count)
                .Where(candidate => nodes[candidate].ParentIndex == _selectedNodeIndex).ToList();
        }
        SelectedNodeChildrenSummaryText.Text = $"PRIMARY FOCUS LOCKED  /  BROWSING {browsedNode.Name.ToUpperInvariant()}";
        SelectedNodeDetailsText.Text = browsedNode.IsDirectory
            ? "SECONDARY DIRECTORY SIGNAL  /  INLINE CONTENT"
            : $"SECONDARY FILE SIGNAL  /  {FormatBytes(browsedNode.SizeBytes)}";
        UpdateNodeLaunchButtons();
        PopulateSelectedNodeChildren(nodes, primaryChildren);
        MeshCanvas.Invalidate();
    }

    private void NodeOverviewTab_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _showChildrenTab = false;
        ApplyNodeInspectorTabState();
    }

    private void NodeChildrenTab_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        _showChildrenTab = true;
        ApplyNodeInspectorTabState();
    }

    private void ApplyNodeInspectorTabState()
    {
        NodeOverviewView.Visibility = _showChildrenTab
            ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
        NodeChildrenView.Visibility = _showChildrenTab
            ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        NodeOverviewTabButton.Background = new SolidColorBrush(Color.FromArgb(
            _showChildrenTab ? (byte)20 : (byte)76, 255, 176, 32));
        NodeChildrenTabButton.Background = new SolidColorBrush(Color.FromArgb(
            _showChildrenTab ? (byte)76 : (byte)20, 255, 176, 32));
        NodeOverviewTabButton.Opacity = _showChildrenTab ? 0.7 : 1;
        NodeChildrenTabButton.Opacity = _showChildrenTab ? 1 : 0.7;
    }

    private void OpenNodeDirectory_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!TryGetInspectorLaunchTarget(out var target)) return;
        var directoryPath = target.IsDirectory ? target.Path : Path.GetDirectoryName(target.Path);
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            ReportNodeLaunchFailure("DIRECTORY SIGNAL UNAVAILABLE");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(directoryPath) { UseShellExecute = true });
        }
        catch
        {
            ReportNodeLaunchFailure("WINDOWS DIRECTORY LINK FAILED");
        }
    }

    private void OpenNodeFile_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!TryGetInspectorLaunchTarget(out var target) || target.IsDirectory || !File.Exists(target.Path))
        {
            ReportNodeLaunchFailure("FILE SIGNAL UNAVAILABLE");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target.Path) { UseShellExecute = true });
        }
        catch
        {
            ReportNodeLaunchFailure("WINDOWS FILE LINK FAILED");
        }
    }

    private bool TryGetInspectorLaunchTarget(out InspectorLaunchTarget target)
    {
        lock (_graphGate)
        {
            if (_liveInspectorSelection is { } live)
            {
                target = new InspectorLaunchTarget(live.Path, live.IsDirectory);
                return true;
            }
            if (_inspectorNodeIndex >= 0 && _inspectorNodeIndex < _nodes.Count)
            {
                var inspected = _nodes[_inspectorNodeIndex];
                target = new InspectorLaunchTarget(inspected.Path, inspected.IsDirectory);
                return true;
            }
            if (_selectedNodeIndex >= 0 && _selectedNodeIndex < _nodes.Count)
            {
                var selected = _nodes[_selectedNodeIndex];
                target = new InspectorLaunchTarget(selected.Path, selected.IsDirectory);
                return true;
            }
        }
        target = default;
        return false;
    }

    private void UpdateNodeLaunchButtons()
    {
        if (!TryGetInspectorLaunchTarget(out var target))
        {
            OpenNodeDirectoryButton.IsEnabled = false;
            OpenNodeFileButton.IsEnabled = false;
            return;
        }
        var directoryPath = target.IsDirectory ? target.Path : Path.GetDirectoryName(target.Path);
        OpenNodeDirectoryButton.IsEnabled = !string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath);
        OpenNodeFileButton.IsEnabled = !target.IsDirectory && File.Exists(target.Path);
    }

    private void ReportNodeLaunchFailure(string message)
    {
        SelectedNodeDetailsText.Text = message;
        UpdateNodeLaunchButtons();
    }

    private void ClearSelection()
    {
        lock (_graphGate)
        {
            _selectedNodeIndex = -1;
            _inspectorNodeIndex = -1;
            _expandedInspectorNodes.Clear();
            _expandedLiveInspectorPaths.Clear();
            _liveInspectorSelection = null;
            _inspectorFilterQuery = string.Empty;
            _inspectorFilterMatchIndices.Clear();
        }
        if (!string.IsNullOrEmpty(NodeChildFilterBox.Text)) NodeChildFilterBox.Text = string.Empty;
        SelectedNodePanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        MeshCanvas.Invalidate();
    }

    public bool HandleEscape()
    {
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Microsoft.UI.Xaml.DependencyObject;
        if (_searchDockExpanded || SearchResultsPanel.Visibility == Microsoft.UI.Xaml.Visibility.Visible)
        {
            MeshSearchBox.Text = string.Empty;
            ClearMeshSearch();
            CollapseSearchDock();
            HideSearchResults();
            MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            return true;
        }

        if (focused is not null && IsDescendantOf(focused, NodeChildFilterBox))
        {
            if (!string.IsNullOrEmpty(NodeChildFilterBox.Text)) NodeChildFilterBox.Text = string.Empty;
            MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            return true;
        }

        List<VisualNode> nodes;
        List<int> primaryChildren;
        VisualNode? primaryNode = null;
        lock (_graphGate)
        {
            if (_inspectorNodeIndex >= 0 || _liveInspectorSelection is not null)
            {
                _inspectorNodeIndex = -1;
                _liveInspectorSelection = null;
                nodes = _nodes;
                primaryChildren = _selectedNodeIndex < 0
                    ? []
                    : Enumerable.Range(0, nodes.Count)
                        .Where(index => nodes[index].ParentIndex == _selectedNodeIndex).ToList();
                if (_selectedNodeIndex >= 0 && _selectedNodeIndex < nodes.Count)
                    primaryNode = nodes[_selectedNodeIndex];
            }
            else
            {
                nodes = [];
                primaryChildren = [];
            }
        }
        if (primaryNode is { } selected)
        {
            SelectedNodeChildrenSummaryText.Text = $"{primaryChildren.Count:000} INDEXED CHILD SIGNALS  /  SELECT TO INSPECT";
            SelectedNodeDetailsText.Text = File.Exists(selected.Path) || Directory.Exists(selected.Path)
                ? "NODE ONLINE"
                : "INDEXED / CURRENTLY UNAVAILABLE";
            UpdateNodeLaunchButtons();
            PopulateSelectedNodeChildren(nodes, primaryChildren);
            MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            MeshCanvas.Invalidate();
            return true;
        }

        bool hasPrimarySelection;
        lock (_graphGate) hasPrimarySelection = _selectedNodeIndex >= 0;
        if (!hasPrimarySelection) return false;
        ClearSelection();
        MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
        return true;
    }

    private void MeshSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ExecuteMeshSearch(revealResults: true);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            MeshSearchBox.Text = string.Empty;
            ClearMeshSearch();
            CollapseSearchDock();
            HideSearchResults();
            MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void MeshCanvas_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || _searchDockExpanded) return;
        ExpandSearchDock();
        e.Handled = true;
    }

    private void MeshSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(MeshSearchBox.Text)) ClearMeshSearch();
    }

    private void SearchResultsPanel_LostFocus(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(XamlRoot) as Microsoft.UI.Xaml.DependencyObject;
            if (focused is not null &&
                (IsDescendantOf(focused, SearchResultsPanel) || IsDescendantOf(focused, MeshSearchDock))) return;
            HideSearchResults();
        });
    }

    private void MeshSearchButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_searchDockExpanded)
        {
            ExpandSearchDock();
            return;
        }

        CollapseSearchDock();
        HideSearchResults();
        MeshCanvas.Focus(Microsoft.UI.Xaml.FocusState.Programmatic);
    }

    private void ExpandSearchDock()
    {
        if (_searchDockExpanded) return;
        _searchDockExpanded = true;
        SearchDockCollapseStoryboard.Stop();
        MeshSearchInputLayer.IsHitTestVisible = true;
        SearchDockExpandStoryboard.Begin();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MeshSearchButton, "Minimize mesh search");
        ToolTipService.SetToolTip(MeshSearchButton, "Minimize search");
        DispatcherQueue.TryEnqueue(() => MeshSearchBox.Focus(Microsoft.UI.Xaml.FocusState.Programmatic));
    }

    private void CollapseSearchDock()
    {
        if (!_searchDockExpanded) return;
        _searchDockExpanded = false;
        SearchDockExpandStoryboard.Stop();
        SearchDockCollapseStoryboard.Begin();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MeshSearchButton, "Open mesh search");
        ToolTipService.SetToolTip(MeshSearchButton, "Open search");
    }

    private void ExecuteMeshSearch(bool revealResults)
    {
        var query = MeshSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ClearMeshSearch();
            return;
        }

        List<(int Index, int Score, VisualNode Node)> matches;
        lock (_graphGate)
        {
            matches = _nodes
                .Select((node, index) => (Index: index, Score: ScoreSearchMatch(node, query), Node: node))
                .Where(match => match.Score < int.MaxValue)
                .OrderBy(match => match.Score)
                .ThenByDescending(match => match.Node.IsRoot)
                .ThenBy(match => match.Node.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _activeSearchQuery = query;
            _searchMatchIndices = matches.Select(match => match.Index).ToHashSet();
        }

        PopulateSearchResults(matches, query);
        if (revealResults || SearchResultsPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible)
            RevealSearchResults();
        MeshCanvas.Invalidate();
    }

    private void PopulateSearchResults(IReadOnlyList<(int Index, int Score, VisualNode Node)> matches, string query)
    {
        SearchResultsList.Children.Clear();
        SearchResultCountText.Text = ToRomanNumeral(matches.Count);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            SearchResultCountText, $"{matches.Count} search results");
        SearchResultSummaryText.Text = matches.Count == 0
            ? $"NO SIGNALS MATCH  //  {query.ToUpperInvariant()}"
            : $"{matches.Count:000} SIGNALS RESOLVED  //  {query.ToUpperInvariant()}";

        if (matches.Count == 0)
        {
            SearchResultsList.Children.Add(new TextBlock
            {
                Text = "NO FILE OR DIRECTORY SIGNALS FOUND.",
                FontSize = 7,
                CharacterSpacing = 70,
                Foreground = new SolidColorBrush(Color.FromArgb(180, 143, 171, 180)),
                Margin = new Microsoft.UI.Xaml.Thickness(10, 18, 0, 0)
            });
            return;
        }

        foreach (var match in matches)
        {
            var button = new Button
            {
                Style = (Microsoft.UI.Xaml.Style)Resources["MeshSearchResultButtonStyle"],
                Tag = match.Index,
                HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
                $"Focus {(match.Node.IsDirectory ? "directory" : "file")} {match.Node.Name}");
            ToolTipService.SetToolTip(button, match.Node.Path);
            button.Click += SearchResult_Click;

            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(30) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = Microsoft.UI.Xaml.GridLength.Auto });

            var marker = new Grid { Width = 20, Height = 20, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
            marker.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = match.Node.IsDirectory ? 13 : 9,
                Height = match.Node.IsDirectory ? 13 : 9,
                Stroke = new SolidColorBrush(Color.FromArgb(225, 0, 229, 255)),
                StrokeThickness = 1,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = match.Node.IsDirectory ? 45 : 0 }
            });
            marker.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 3,
                Height = 3,
                Fill = new SolidColorBrush(Color.FromArgb(255, 0, 244, 255))
            });
            content.Children.Add(marker);

            var labels = new StackPanel { Spacing = 2, VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center };
            labels.Children.Add(new TextBlock
            {
                Text = match.Node.Name.ToUpperInvariant(),
                FontSize = 8,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CharacterSpacing = 35,
                Foreground = new SolidColorBrush(Color.FromArgb(245, 235, 250, 252)),
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            labels.Children.Add(new TextBlock
            {
                Text = match.Node.Path,
                FontSize = 7,
                CharacterSpacing = 18,
                Foreground = new SolidColorBrush(Color.FromArgb(150, 134, 164, 173)),
                TextTrimming = Microsoft.UI.Xaml.TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);

            var type = new TextBlock
            {
                Text = match.Node.IsRoot ? "ROOT" : match.Node.IsDirectory ? "DIR" : "FILE",
                FontSize = 7,
                CharacterSpacing = 75,
                Foreground = new SolidColorBrush(Color.FromArgb(210, 0, 229, 255)),
                VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(8, 0, 3, 0)
            };
            Grid.SetColumn(type, 2);
            content.Children.Add(type);
            button.Content = content;
            SearchResultsList.Children.Add(button);
        }
    }

    private void SearchResult_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        VisualNode node;
        lock (_graphGate)
        {
            if (index < 0 || index >= _nodes.Count) return;
            _selectedNodeIndex = index;
            _inspectorNodeIndex = -1;
            _expandedInspectorNodes.Clear();
            _expandedLiveInspectorPaths.Clear();
            _liveInspectorSelection = null;
            _inspectorFilterQuery = string.Empty;
            _inspectorFilterMatchIndices.Clear();
            node = _nodes[index];
        }
        if (!string.IsNullOrEmpty(NodeChildFilterBox.Text)) NodeChildFilterBox.Text = string.Empty;

        // Search navigation is intentionally immediate: the resolved signal becomes
        // the orbital origin and is brought close enough to inspect in one action.
        _focusPivot = node.BrainPosition;
        _targetZoom = Math.Max(_targetZoom, 2.7f);
        _zoom = _targetZoom;
        ZoomReadout.Text = $"{_targetZoom * 100:0}%";
        ShowSelectedNode(node);
        MeshCanvas.Invalidate();
    }

    private void RevealSearchResults()
    {
        _searchResultsRequested = true;
        SearchResultsHideStoryboard.Stop();
        SearchResultsPanel.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        SearchResultsPanel.IsHitTestVisible = true;
        SearchResultsPanel.Opacity = 0;
        SearchResultsTransform.TranslateY = 28;
        SearchResultsTransform.ScaleY = 0.94;
        SearchResultsRevealStoryboard.Begin();
    }

    private void HideSearchResults()
    {
        if (SearchResultsPanel.Visibility != Microsoft.UI.Xaml.Visibility.Visible) return;
        _searchResultsRequested = false;
        SearchResultsRevealStoryboard.Stop();
        SearchResultsHideStoryboard.Begin();
    }

    private void ClearMeshSearch()
    {
        lock (_graphGate)
        {
            _activeSearchQuery = string.Empty;
            _searchMatchIndices.Clear();
        }
        SearchResultsList.Children.Clear();
        SearchResultCountText.Text = "—";
        SearchResultSummaryText.Text = "INDEX READY";
        HideSearchResults();
        MeshCanvas.Invalidate();
    }

    private static int ScoreSearchMatch(VisualNode node, string query)
    {
        var terms = SearchWords(query);
        if (terms.Count == 0) return int.MaxValue;
        var nameWords = SearchWords(node.Name);
        var explicitPathSearch = query.IndexOfAny(['\\', '/', ':']) >= 0;
        var pathWords = explicitPathSearch ? SearchWords(node.Path) : [];
        var total = node.IsDirectory ? 2 : 0;

        foreach (var term in terms)
        {
            var nameScore = nameWords.Count == 0 ? int.MaxValue : nameWords.Min(word => ScoreSearchTerm(term, word));
            var pathScore = !explicitPathSearch || pathWords.Count == 0
                ? int.MaxValue
                : pathWords.Min(word => ScoreSearchTerm(term, word));
            if (pathScore < int.MaxValue - 60) pathScore += 60;
            var best = Math.Min(nameScore, pathScore);
            if (best == int.MaxValue) return int.MaxValue;
            total += best;
        }
        return total;
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

    private static List<string> SearchWords(string value)
    {
        var normalized = new string(value.Select(character =>
            char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : ' ').ToArray());
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static int ScoreSearchTerm(string term, string candidate)
    {
        if (candidate.Equals(term, StringComparison.Ordinal)) return 0;
        if (candidate.StartsWith(term, StringComparison.Ordinal)) return 6 + candidate.Length - term.Length;
        var containedAt = candidate.IndexOf(term, StringComparison.Ordinal);
        if (containedAt >= 0) return 15 + containedAt + candidate.Length - term.Length;
        return int.MaxValue;
    }

    private async Task IndexFilesystemAsync(FileGraphService service, CancellationToken token)
    {
        try
        {
            _indexing = true;
            IndexStatusText.Text = "LOADING SAVED FILESYSTEM CONSTELLATION";
            var cached = await service.LoadBaselineAsync(VisualIndexNodeLimit, token);
            if (cached is not null && cached.Nodes.Count > 0)
            {
                await ApplyCompletedSnapshotAsync(cached, token);
                _indexing = false;
                IndexStatusText.Text = cached.IsTruncated
                    ? $"BOUNDED MAP LOADED  /  {cached.Nodes.Count:N0} NODES  /  FULL INDEX PRESERVED"
                    : $"SAVED MAP LOADED  /  {cached.FileCount:N0} FILES  /  DELTA WATCH ACTIVE";

                if (service.CurrentIsBoundedProjection)
                {
                    IndexStatusText.Text =
                        $"BOUNDED MAP ONLINE  /  {cached.Nodes.Count:N0} NODES  /  LIVE CHANGES ACTIVE";
                }
                else
                {
                    // Only a complete baseline can safely fold its historical
                    // journal back into persistent storage.
                    await Task.Yield();
                    IndexStatusText.Text = "SAVED MAP ONLINE  /  APPLYING PENDING DELTAS IN BACKGROUND";
                    var pending = await service.ApplyPendingDeltasAsync(cancellationToken: token);
                    if (pending.DeltaCount > 0)
                    {
                        await ApplyCompletedSnapshotAsync(pending.Snapshot, token);
                        IndexStatusText.Text =
                            $"SAVED MAP CURRENT  /  {pending.Snapshot.FileCount:N0} FILES  /  " +
                            $"{pending.DeltaCount:N0} DELTAS COMPACTED";
                    }
                    else
                    {
                        IndexStatusText.Text =
                            $"SAVED MAP CURRENT  /  {cached.FileCount:N0} FILES  /  DELTA WATCH ACTIVE";
                    }
                }
            }
            else
            {
                var generation = ++_indexGeneration;
                bool showPartialGraph;
                lock (_graphGate)
                {
                    showPartialGraph = _nodes.Count == 0;
                    _indexProgress = FileGraphProgress.Empty;
                    if (showPartialGraph)
                    {
                        _snapshot = FileGraphSnapshot.Empty;
                        _nodes = [];
                        _renderOrder = [];
                        _nodePathIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        _childrenByParent = [];
                        _aggregateExpansion = [];
                    }
                }
                IndexStatusText.Text = "FIRST MAP  /  SAVING PERSISTENT FILESYSTEM CONSTELLATION";
                var progressReporter = new Progress<FileGraphProgress>(progress =>
                    ApplyIndexProgress(progress, generation, showPartialGraph));
                var snapshot = await service.BuildAsync(maxNodes: VisualIndexNodeLimit,
                    progress: progressReporter, cancellationToken: token, persistLimitedSnapshot: true);
                await ApplyCompletedSnapshotAsync(snapshot, token);
                _indexing = false;
                IndexStatusText.Text =
                    $"BOUNDED MAP SAVED  /  {snapshot.Nodes.Count:N0} NODES  /  DELTA WATCH ACTIVE";
            }

            while (!token.IsCancellationRequested)
            {
                await _refreshSignal.WaitAsync(token);
                await Task.Delay(TimeSpan.FromSeconds(5), token);
                while (_refreshSignal.Wait(0)) { }
                var changes = new List<FileActivity>();
                while (_pendingIndexChanges.TryDequeue(out var change)) changes.Add(change);
                if (changes.Count == 0) continue;

                IndexStatusText.Text = $"APPLYING {changes.Count:N0} FILESYSTEM DELTAS";
                var updated = await service.ApplyChangesAsync(changes, token);
                await ApplyCompletedSnapshotAsync(updated, token);
                IndexStatusText.Text =
                    $"SAVED MAP CURRENT  /  {updated.FileCount:N0} FILES  /  {changes.Count:N0} DELTAS APPLIED";
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            _indexing = false;
            IndexStatusText.Text = "FILESYSTEM CACHE LIMITED  /  SAFE VISUAL MODE";
        }
    }

    private async Task ApplyCompletedSnapshotAsync(FileGraphSnapshot snapshot, CancellationToken token)
    {
        var (nodes, renderOrder, pathIndex, childrenByParent) = await Task.Run(
            () => BuildVisualGraph(snapshot), token);
        VisualNode? retainedSelection = null;
        lock (_graphGate)
        {
            var selectedPath = _selectedNodeIndex >= 0 && _selectedNodeIndex < _nodes.Count
                ? _nodes[_selectedNodeIndex].Path
                : null;
            var inspectorPath = _inspectorNodeIndex >= 0 && _inspectorNodeIndex < _nodes.Count
                ? _nodes[_inspectorNodeIndex].Path
                : null;
            var expandedPaths = _expandedInspectorNodes
                .Where(index => index >= 0 && index < _nodes.Count)
                .Select(index => _nodes[index].Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _snapshot = snapshot;
            _indexProgress = new FileGraphProgress([], snapshot.FileCount, snapshot.DirectoryCount,
                snapshot.RootCount, snapshot.DirectoryCount, 0, 1, string.Empty, true);
            _nodes = nodes;
            _renderOrder = renderOrder;
            _nodePathIndex = pathIndex;
            _childrenByParent = childrenByParent;
            _aggregateExpansion = [];
            _visibleNodeCache = [];
            _visibleNodeCacheOptions = null;
            _visibleNodeCacheSourceCount = -1;
            _selectedNodeIndex = selectedPath is null
                ? -1
                : nodes.FindIndex(node => node.Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
            _inspectorNodeIndex = inspectorPath is null
                ? -1
                : nodes.FindIndex(node => node.Path.Equals(inspectorPath, StringComparison.OrdinalIgnoreCase));
            _expandedInspectorNodes = Enumerable.Range(0, nodes.Count)
                .Where(index => expandedPaths.Contains(nodes[index].Path)).ToHashSet();
            if (_selectedNodeIndex >= 0) retainedSelection = nodes[_selectedNodeIndex];
        }

        if (retainedSelection is { } selectedNode) ShowSelectedNode(selectedNode);
        else SelectedNodePanel.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(_activeSearchQuery)) ExecuteMeshSearch(revealResults: false);
        MeshCanvas.Invalidate();
    }

    private void ApplyIndexProgress(FileGraphProgress progress, int generation, bool showPartialGraph)
    {
        if (generation != _indexGeneration || !_indexing) return;
        lock (_graphGate)
        {
            _indexProgress = progress;
            if (showPartialGraph)
            {
                foreach (var source in progress.AddedNodes)
                {
                    if (source.Id < _nodes.Count) continue;
                    if (source.Id != _nodes.Count) break;
                    var index = _nodes.Count;
                    var visual = CreateVisualNode(source, _nodes, Math.Max(1, source.ChildCount + 1),
                        source.ParentId is null ? source.Id : 0, Math.Max(1, progress.RootCount));
                    _nodes.Add(visual);
                    _renderOrder.Add(index);
                    _nodePathIndex[source.Path] = index;
                    if (visual.ParentIndex >= 0)
                    {
                        if (!_childrenByParent.TryGetValue(visual.ParentIndex, out var children))
                            _childrenByParent[visual.ParentIndex] = children = [];
                        children.Add(index);
                    }
                }
            }
        }

        var percent = Math.Clamp((int)Math.Round(progress.EstimatedCompletion * 100), 0, 100);
        IndexStatusText.Text = progress.IsComplete
            ? $"MAPPING 100%  /  OPTIMIZING {progress.MappedNodeCount:N0} SIGNAL LINKS"
            : $"MAPPING EST {percent:00}%  /  {progress.MappedFileCount:N0} FILES  /  {progress.PendingDirectoryCount:N0} FRONTIER";
        MeshCanvas.Invalidate();
    }

    private static (List<VisualNode> Nodes, List<int> RenderOrder,
        Dictionary<string, int> PathIndex, Dictionary<int, List<int>> ChildrenByParent) BuildVisualGraph(
        FileGraphSnapshot snapshot)
    {
        var nodes = new List<VisualNode>(snapshot.Nodes.Count);
        var subtreeCounts = Enumerable.Repeat(1, snapshot.Nodes.Count).ToArray();
        var subtreeSizes = snapshot.Nodes
            .Select(node => node.IsDirectory ? 0L : Math.Max(0L, node.SizeBytes))
            .ToArray();
        for (var index = snapshot.Nodes.Count - 1; index >= 0; index--)
        {
            var parentId = snapshot.Nodes[index].ParentId;
            if (parentId is null || parentId.Value < 0 || parentId.Value >= subtreeCounts.Length) continue;
            subtreeCounts[parentId.Value] = (int)Math.Min(int.MaxValue,
                (long)subtreeCounts[parentId.Value] + subtreeCounts[index]);
            subtreeSizes[parentId.Value] = subtreeSizes[parentId.Value] > long.MaxValue - subtreeSizes[index]
                ? long.MaxValue
                : subtreeSizes[parentId.Value] + subtreeSizes[index];
        }

        var roots = snapshot.Nodes.Where(node => node.ParentId is null).Select(node => node.Id).ToArray();
        var rootOrdinals = roots.Select((id, ordinal) => (id, ordinal)).ToDictionary(item => item.id, item => item.ordinal);
        var hasDirectoryChildren = new bool[snapshot.Nodes.Count];
        foreach (var node in snapshot.Nodes)
            if (node.IsDirectory && node.ParentId is { } parentId && parentId >= 0 &&
                parentId < hasDirectoryChildren.Length)
                hasDirectoryChildren[parentId] = true;
        var maximumDirectoryLogSize = snapshot.Nodes
            .Where(node => node.IsDirectory && node.Id >= 0 && node.Id < subtreeSizes.Length)
            .Select(node => Math.Log2(subtreeSizes[node.Id] + 1d))
            .DefaultIfEmpty(1d)
            .Max();

        foreach (var source in snapshot.Nodes)
            nodes.Add(CreateVisualNode(source, nodes, subtreeCounts[source.Id],
                rootOrdinals.GetValueOrDefault(source.Id), Math.Max(1, roots.Length),
                Vector3.Zero,
                subtreeSizes[source.Id], maximumDirectoryLogSize,
                source.IsDirectory && !hasDirectoryChildren[source.Id]));

        var childrenByParent = new Dictionary<int, List<int>>();
        for (var index = 0; index < nodes.Count; index++)
        {
            var parent = nodes[index].ParentIndex;
            if (parent < 0) continue;
            if (!childrenByParent.TryGetValue(parent, out var children))
                childrenByParent[parent] = children = [];
            children.Add(index);
        }
        // Every node is rendered, so the previous O(n log n) LOD sort and sibling
        // sorts only delayed startup without changing the picture.
        var renderOrder = Enumerable.Range(0, nodes.Count).ToList();
        var pathIndex = new Dictionary<string, int>(nodes.Count, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < nodes.Count; index++)
            pathIndex.TryAdd(nodes[index].Path, index);
        return (nodes, renderOrder, pathIndex, childrenByParent);
    }

    private static VisualNode CreateVisualNode(FileGraphNode source, IReadOnlyList<VisualNode> existingNodes,
        int subtreeCount = 1, int rootOrdinal = 0, int rootCount = 1, Vector3? positionOverride = null,
        long subtreeSizeBytes = 0, double maximumDirectoryLogSize = 1d, bool terminalDirectory = false)
    {
        var placement = ComputeBrainPlacement(source.Path, source.ParentId is null,
            source.IsDirectory, source.Depth);
        var phase = placement.Phase;
        var visualParentIndex = -1;
        Vector3 position;
        if (positionOverride is { } hierarchicalPosition)
        {
            position = hierarchicalPosition;
            if (source.ParentId is { } parentId && parentId >= 0 && parentId < existingNodes.Count)
                visualParentIndex = parentId;
        }
        else if (source.ParentId is null || source.ParentId.Value < 0 || source.ParentId.Value >= existingNodes.Count)
        {
            var rootAngle = rootCount <= 1 ? 0f : -MathF.PI * 0.5f + rootOrdinal * MathF.Tau / rootCount;
            position = rootCount <= 1
                ? Vector3.Zero
                : new Vector3(MathF.Cos(rootAngle) * 0.34f, MathF.Sin(rootAngle) * 0.27f,
                    MathF.Sin(rootAngle * 1.5f) * 0.08f);
        }
        else
        {
            visualParentIndex = source.ParentId.Value;
            var parent = existingNodes[visualParentIndex];
            if (source.IsDirectory)
            {
                var parentAngle = parent.Position.LengthSquared() < 0.002f
                    ? parent.Phase
                    : MathF.Atan2(parent.Position.Y, parent.Position.X);
                var sectorSpread = source.Depth <= 2 ? 1.45f : 0.92f;
                var localAngle = parentAngle + (HashUnit(source.Path + "|SECTOR") - 0.5f) * sectorSpread;
                var step = MathF.Max(0.022f, 0.205f * MathF.Pow(0.72f, Math.Min(10, source.Depth - 1)));
                var distance = step * (0.68f + HashUnit(source.Path + "|R") * 0.32f);
                var z = (HashUnit(source.Path + "|Z") - 0.5f) * step * 0.65f;
                position = parent.Position + new Vector3(MathF.Cos(localAngle) * distance,
                    MathF.Sin(localAngle) * distance, z);
            }
            else
            {
                var orbit = 0.014f + MathF.Min(0.038f, MathF.Log2(parent.ChildCount + 1) * 0.0032f);
                var distance = orbit * (0.32f + MathF.Sqrt(HashUnit(source.Path + "|R")) * 0.68f);
                var z = (HashUnit(source.Path + "|Z") - 0.5f) * orbit * 0.9f;
                position = parent.Position + new Vector3(MathF.Cos(phase) * distance,
                    MathF.Sin(phase) * distance, z);
            }

            var length = position.Length();
            if (length > 0.92f) position *= 0.92f / length;
        }

        var aggregateSize = source.IsDirectory ? Math.Max(0L, subtreeSizeBytes) : source.SizeBytes;
        var directorySizeRatio = source.IsDirectory
            ? (float)(Math.Log2(aggregateSize + 1d) / Math.Max(1d, maximumDirectoryLogSize))
            : 0f;
        var radius = source.IsDirectory
            ? 1.15f + directorySizeRatio * 7.85f
            : 0.8f + MathF.Min(1.35f, MathF.Log10(source.SizeBytes + 10) * 0.17f);
        var brainPosition = placement.Position;
        if (visualParentIndex >= 0 && visualParentIndex < existingNodes.Count)
        {
            var parentBrainPosition = existingNodes[visualParentIndex].BrainPosition;
            if (source.IsDirectory)
            {
                var inheritance = source.Depth switch
                {
                    <= 1 => 0.94f,
                    2 => 0.82f,
                    3 => 0.72f,
                    4 => 0.64f,
                    5 => 0.58f,
                    _ => 0.52f
                };
                if (terminalDirectory) inheritance = MathF.Max(inheritance, 0.88f);
                brainPosition = Vector3.Lerp(parentBrainPosition, brainPosition, inheritance);
                if (terminalDirectory) brainPosition *= 1.10f;
            }
            else
            {
                var direction = brainPosition - parentBrainPosition;
                if (direction.LengthSquared() < 0.000001f) direction = Vector3.UnitX;
                direction = Vector3.Normalize(direction);
                var parentTerritory = 0.012f + MathF.Min(0.095f,
                    MathF.Log2(existingNodes[visualParentIndex].SubtreeCount + 1f) * 0.0065f);
                var radialDistribution = 0.18f + MathF.Sqrt(placement.FileRadiusUnit) * 0.82f;
                var localDistance = parentTerritory * radialDistribution;
                brainPosition = parentBrainPosition + direction * localDistance;
            }
        }
        return new VisualNode(position, brainPosition, phase, radius, source.IsDirectory, source.ChildCount, source.Depth,
            visualParentIndex, source.ParentId is null, source.Name, source.Path, aggregateSize,
            Math.Max(1, subtreeCount));
    }

    private void MeshCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        try
        {
            DrawMeshFrame(sender, args);
        }
        catch (Exception exception)
        {
            // Win2D forwards an exception escaping Draw through the compositor as a
            // native CoreMessaging failure. Preserve the rest of the application and
            // record the actual managed stack instead of terminating the process.
            MeshCanvas.Paused = true;
            App.Services?.LogCrash(new InvalidOperationException("Filesystem mesh draw failed.", exception));
        }
    }

    private void DrawMeshFrame(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var width = (float)sender.Size.Width;
        var height = (float)sender.Size.Height;
        if (width < 100 || height < 100) return;

        List<VisualNode> nodes;
        List<int> renderOrder;
        Dictionary<string, int> pathIndex;
        Dictionary<int, List<int>> childrenByParent;
        List<ActiveActivity> activities;
        FileGraphSnapshot snapshot;
        FileGraphProgress indexProgress;
        int selectedNodeIndex;
        int inspectorNodeIndex;
        HashSet<int> searchMatchIndices;
        HashSet<int> inspectorFilterMatchIndices;
        Dictionary<int, int> aggregateExpansion;
        MeshVisibilityOptions visibilityOptions;
        lock (_graphGate)
        {
            var now = DateTimeOffset.UtcNow;
            _activities.RemoveAll(activity => activity.ExpiresAt <= now);
            nodes = _nodes;
            renderOrder = _renderOrder;
            pathIndex = _nodePathIndex;
            childrenByParent = _childrenByParent;
            activities = [.. _activities];
            snapshot = _snapshot;
            indexProgress = _indexProgress;
            selectedNodeIndex = _selectedNodeIndex;
            inspectorNodeIndex = _inspectorNodeIndex;
            searchMatchIndices = new HashSet<int>(_searchMatchIndices);
            inspectorFilterMatchIndices = new HashSet<int>(_inspectorFilterMatchIndices);
            aggregateExpansion = new Dictionary<int, int>(_aggregateExpansion);
            visibilityOptions = new MeshVisibilityOptions(_showMajorDirectories, _showDeepDirectories,
                _showAggregateNodes, _showIndividualFiles, _showSystemNodes, _showActiveOperations,
                _showDirectoryConnections);
        }

        var liveTime = (float)args.Timing.TotalTime.TotalSeconds;
        if (selectedNodeIndex < 0 && !_orbitDragging)
            _animationTime += MathF.Min(0.1f, (float)args.Timing.ElapsedTime.TotalSeconds);
        var time = _animationTime;
        _zoom += (_targetZoom - _zoom) * 0.16f;
        if (MathF.Abs(_targetZoom - _zoom) < 0.001f) _zoom = _targetZoom;
        var targetFocusPivot = selectedNodeIndex >= 0 && selectedNodeIndex < nodes.Count
            ? GetBrainPosition(nodes[selectedNodeIndex])
            : Vector3.Zero;
        var focusBlend = 1f - MathF.Exp(-MathF.Min(0.1f, (float)args.Timing.ElapsedTime.TotalSeconds) * 9f);
        _focusPivot = Vector3.Lerp(_focusPivot, targetFocusPivot, focusBlend);
        if (Vector3.DistanceSquared(_focusPivot, targetFocusPivot) < 0.000001f)
            _focusPivot = targetFocusPivot;
        var targetCenterRatio = selectedNodeIndex >= 0 ? 0.40f : 0.50f;
        _navigationCenterRatio += (targetCenterRatio - _navigationCenterRatio) * focusBlend;
        if (MathF.Abs(targetCenterRatio - _navigationCenterRatio) < 0.0005f)
            _navigationCenterRatio = targetCenterRatio;
        var center = new Vector2(width * _navigationCenterRatio, height * 0.47f);
        // Semantic zoom reveals more detail without inflating the whole construct
        // beyond the viewport. The JARVIS-style cognition core stays coherent.
        var physicalZoom = MathF.Pow(_zoom, 0.50f);
        var scale = MathF.Min(width * 0.245f, height * 0.36f) * physicalZoom;
        var amber = Color.FromArgb(255, 255, 169, 31);
        var gold = Color.FromArgb(255, 255, 205, 92);
        var pale = Color.FromArgb(245, 255, 244, 209);
        var muted = Color.FromArgb(155, 164, 128, 72);

        if (nodes.Count > 0)
        {
            // Progressive indexing appends to the shared graph collections. Keep one
            // frame atomic with respect to those batches so render traversal never
            // observes a List or child map while its backing storage is changing.
            lock (_graphGate)
                DrawFilesystemGraph(ds, nodes, renderOrder, pathIndex, childrenByParent, center, scale, time,
                    _orbitOrientation, _manualOrbitActivated, _focusPivot, searchMatchIndices,
                    inspectorFilterMatchIndices, aggregateExpansion, width, height, _zoom,
                    selectedNodeIndex, inspectorNodeIndex, activities, visibilityOptions,
                    liveTime, amber, gold, pale);
        }
        else
            DrawIndexingField(ds, center, scale, time, amber, gold);

        if (_indexing)
            DrawMappingProgress(ds, center, scale, time, indexProgress, amber, gold, pale);

        DrawNucleus(ds, center, pale, StateAccent(255));
        DrawTelemetry(ds, width, height, snapshot, indexProgress, amber, pale, muted);
        if (visibilityOptions.ShowActiveOperations)
            DrawActivityReadout(ds, width, height, activities, amber, pale);
    }

    private void DrawFilesystemGraph(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<int> renderOrder, IReadOnlyDictionary<string, int> pathIndex,
        IReadOnlyDictionary<int, List<int>> childrenByParent,
        Vector2 center, float scale, float time,
        Quaternion orbitOrientation, bool manualOrbitActivated, Vector3 focusPivot,
        IReadOnlySet<int> searchMatchIndices, IReadOnlySet<int> inspectorFilterMatchIndices,
        IReadOnlyDictionary<int, int> aggregateExpansion,
        float width, float height, float zoom, int selectedNodeIndex, int inspectorNodeIndex,
        IReadOnlyList<ActiveActivity> activities,
        MeshVisibilityOptions visibilityOptions,
        float liveTime, Color amber, Color gold, Color pale)
    {
        var rotation = manualOrbitActivated
            ? Matrix4x4.CreateFromQuaternion(orbitOrientation)
            : CreateAmbientRotation(time);
        var visibleNodes = BuildVisibleNodeSet(nodes, renderOrder, childrenByParent, zoom, selectedNodeIndex,
            inspectorNodeIndex, searchMatchIndices, inspectorFilterMatchIndices, activities, pathIndex,
            aggregateExpansion, visibilityOptions);
        var projected = ArrayPool<ProjectedNode>.Shared.Rent(nodes.Count);
        try
        {
            foreach (var index in visibleNodes)
            {
                // Translate into focus space before rotation. A selected node therefore
                // stays at the navigator origin while every other node orbits around it.
                var rotated = Vector3.Transform(GetBrainPosition(nodes[index]) - focusPivot, rotation);
                var perspective = 0.82f + (rotated.Z + 1f) * 0.13f;
                projected[index] = new ProjectedNode(
                    center + new Vector2(rotated.X, -rotated.Y) * scale * perspective,
                    rotated.Z,
                    perspective);
            }

            var drawableNodes = _drawableNodesScratch;
            drawableNodes.Clear();
            foreach (var index in visibleNodes)
            {
                var position = projected[index].Position;
                if (position.X >= -96 && position.X <= width + 96 &&
                    position.Y >= -96 && position.Y <= height + 96)
                    drawableNodes.Add(index);
            }

            if (visibilityOptions.ShowDirectoryConnections)
                DrawAllNeuronFibers(ds, nodes, projected, drawableNodes, selectedNodeIndex);

            using (ds.CreateLayer(selectedNodeIndex >= 0 ? 0.30f : 1f))
            {
                if (_fileNodeSprite is not null && _directoryNodeSprite is not null)
                {
                    using var sprites = ds.CreateSpriteBatch(CanvasSpriteSortMode.None);
                    foreach (var item in drawableNodes)
                    {
                        var node = nodes[item];
                        var projection = projected[item];
                        var depth = Math.Clamp((projection.Depth + 1f) * 0.5f, 0.18f, 1f);
                        var radius = GetNodeVisualRadius(node, projection.Scale);
                        // Every file remains present, but a translucent particle field
                        // leaves the cortical shell and neuron scaffold legible.
                        var alpha = (node.IsDirectory ? 62f + depth * 118f : 34f + depth * 92f) / 255f;
                        var nodeColor = NodeColor(node);
                        var tint = new Vector4(nodeColor.R / 255f, nodeColor.G / 255f,
                            nodeColor.B / 255f, alpha);
                        sprites.Draw(node.IsDirectory ? _directoryNodeSprite : _fileNodeSprite,
                            new Windows.Foundation.Rect(projection.Position.X - radius,
                                projection.Position.Y - radius, radius * 2f, radius * 2f), tint);
                    }
                }

                foreach (var item in searchMatchIndices)
                {
                    if (!drawableNodes.Contains(item)) continue;
                    var node = nodes[item];
                    var projection = projected[item];
                    var pulse = 0.5f + 0.5f * MathF.Sin(time * 1.8f + node.Phase);
                    var radius = GetNodeVisualRadius(node, projection.Scale);
                    var teal = _accessColor;
                    var matchRadius = MathF.Max(node.IsDirectory ? 9f : 5.5f,
                        radius + (node.IsDirectory ? 6f : 3f));
                    ds.FillCircle(projection.Position, matchRadius * (1.45f + pulse * 0.18f),
                        Color.FromArgb((byte)(14 + pulse * 15), teal.R, teal.G, teal.B));
                    ds.DrawCircle(projection.Position, matchRadius,
                        Color.FromArgb((byte)(205 + pulse * 45), teal.R, teal.G, teal.B), 1.35f);
                    if (node.IsDirectory) ds.DrawCircle(projection.Position, radius, teal, 1.5f);
                    else ds.FillCircle(projection.Position, radius, teal);
                }

                DrawSemanticLabels(ds, nodes, projected, width, height, zoom, selectedNodeIndex, amber, pale,
                    drawableNodes);
            }

            var aggregateHits = new List<AggregateHit>();
            if (visibilityOptions.ShowAggregateNodes)
            {
                using var aggregateLayer = ds.CreateLayer(selectedNodeIndex >= 0 ? 0.30f : 1f);
                DrawClusterAggregates(ds, nodes, projected, drawableNodes, childrenByParent, zoom,
                    amber, pale, aggregateHits);
            }

            var focusedNodes = BuildFocusedSubtree(nodes, childrenByParent, selectedNodeIndex, drawableNodes);
            var nearbySiblings = BuildNearbySiblingSet(nodes, childrenByParent, selectedNodeIndex, drawableNodes);
            if (visibilityOptions.ShowDirectoryConnections && selectedNodeIndex >= 0 &&
                selectedNodeIndex < nodes.Count && nodes[selectedNodeIndex].IsDirectory)
                DrawSelectedNeuronConnections(ds, nodes, projected, childrenByParent, drawableNodes,
                    selectedNodeIndex, time);
            if (nearbySiblings.Count > 0)
            {
                using var siblingLayer = ds.CreateLayer(0.25f);
                DrawFocusedSubtree(ds, nodes, projected, nearbySiblings, searchMatchIndices,
                    time, zoom, amber, gold, pale);
                if (visibilityOptions.ShowAggregateNodes)
                    DrawClusterAggregates(ds, nodes, projected, nearbySiblings, childrenByParent, zoom,
                        amber, pale, aggregateHits);
            }
            if (focusedNodes.Count > 0)
            {
            DrawFocusedSubtree(ds, nodes, projected, focusedNodes, searchMatchIndices,
                time, zoom, amber, gold, pale);
            if (visibilityOptions.ShowAggregateNodes)
                DrawClusterAggregates(ds, nodes, projected, focusedNodes, childrenByParent, zoom,
                    amber, pale, aggregateHits);
            var focusedDescendants = new HashSet<int>(focusedNodes);
            focusedDescendants.Remove(selectedNodeIndex);
            DrawSemanticLabels(ds, nodes, projected, width, height, zoom, -1, amber, pale, focusedDescendants);
            }

            if (visibilityOptions.ShowActiveOperations)
                DrawActiveBranches(ds, nodes, projected, center, activities, liveTime, amber, gold, pale, pathIndex);
            DrawInspectorFilterMatches(ds, nodes, projected, inspectorFilterMatchIndices, time, drawableNodes);
            DrawInspectorBrowseSelection(ds, nodes, projected, selectedNodeIndex, inspectorNodeIndex);
            DrawFocusedSelection(ds, nodes, projected, selectedNodeIndex, width, height, zoom, gold, pale);

            _hitNodes.Clear();
            if (_hitNodes.Capacity < drawableNodes.Count) _hitNodes.Capacity = drawableNodes.Count;
            foreach (var index in drawableNodes)
            {
                var node = nodes[index];
                var radius = GetNodeVisualRadius(node, projected[index].Scale);
                _hitNodes.Add(new HitNode(index, projected[index].Position,
                    MathF.Max(node.IsDirectory ? 8f : 6f, radius + 2f), projected[index].Depth));
            }
            lock (_graphGate)
            {
                _aggregateHits = aggregateHits;
            }
        }
        finally
        {
            ArrayPool<ProjectedNode>.Shared.Return(projected);
        }
    }

    private HashSet<int> BuildVisibleNodeSet(IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<int> renderOrder, IReadOnlyDictionary<int, List<int>> childrenByParent,
        float zoom, int selectedNodeIndex, int inspectorNodeIndex,
        IReadOnlySet<int> searchMatchIndices, IReadOnlySet<int> inspectorFilterMatchIndices,
        IReadOnlyList<ActiveActivity> activities, IReadOnlyDictionary<string, int> pathIndex,
        IReadOnlyDictionary<int, int> aggregateExpansion, MeshVisibilityOptions visibilityOptions)
    {
        var budget = GetRenderBudget(zoom, nodes.Count);
        if (_visibleNodeCacheSourceCount != nodes.Count || _visibleNodeCacheOptions != visibilityOptions ||
            _visibleNodeCacheBudget != budget)
        {
            var candidates = new List<int>(Math.Min(nodes.Count, budget * 2));
            foreach (var index in renderOrder)
            {
                if (index < 0 || index >= nodes.Count) continue;
                var node = nodes[index];
                if (!visibilityOptions.ShowSystemNodes && IsSystemOrHiddenNode(node)) continue;
                if (node.IsDirectory)
                {
                    if (!node.IsRoot && !visibilityOptions.ShowMajorDirectories) continue;
                    if (!visibilityOptions.ShowDeepDirectories && node.Depth > 2) continue;
                }
                else if (!visibilityOptions.ShowIndividualFiles) continue;
                candidates.Add(index);
            }

            var visible = new HashSet<int>(Math.Min(candidates.Count, budget));
            // Preserve the filesystem scaffold first, then distribute the remaining
            // particle budget evenly through the complete bounded projection.
            foreach (var index in candidates)
            {
                var node = nodes[index];
                if ((node.IsRoot || node.IsDirectory && node.Depth <= 2) && visible.Count < budget)
                    visible.Add(index);
            }
            if (candidates.Count <= budget)
            {
                foreach (var index in candidates) visible.Add(index);
            }
            else if (visible.Count < budget)
            {
                var remaining = budget - visible.Count;
                var step = (double)candidates.Count / remaining;
                for (var sample = 0; sample < remaining; sample++)
                {
                    var candidateIndex = candidates[Math.Min(candidates.Count - 1, (int)(sample * step))];
                    visible.Add(candidateIndex);
                }
                // Duplicate preferred samples can leave a few slots. Fill them in
                // stable index order without introducing per-frame randomness.
                if (visible.Count < budget)
                    foreach (var index in candidates)
                        if (visible.Add(index) && visible.Count >= budget) break;
            }
            _visibleNodeCache = visible;
            _visibleNodeCacheOptions = visibilityOptions;
            _visibleNodeCacheSourceCount = nodes.Count;
            _visibleNodeCacheBudget = budget;
        }

        if (selectedNodeIndex < 0 && inspectorNodeIndex < 0 && searchMatchIndices.Count == 0 &&
            inspectorFilterMatchIndices.Count == 0 && activities.Count == 0 && aggregateExpansion.Count == 0)
            return _visibleNodeCache;

        var enriched = new HashSet<int>(_visibleNodeCache);
        void AddWithAncestors(int index)
        {
            while (index >= 0 && index < nodes.Count && enriched.Add(index))
                index = nodes[index].ParentIndex;
        }
        AddWithAncestors(selectedNodeIndex);
        AddWithAncestors(inspectorNodeIndex);
        foreach (var index in searchMatchIndices) AddWithAncestors(index);
        foreach (var index in inspectorFilterMatchIndices) AddWithAncestors(index);
        foreach (var index in aggregateExpansion.Keys) AddWithAncestors(index);
        foreach (var activity in activities)
            if (pathIndex.TryGetValue(activity.Activity.Path, out var index)) AddWithAncestors(index);
        return enriched;
    }

    private static bool IsSystemOrHiddenNode(VisualNode node)
    {
        var name = node.Name.Trim();
        if (name.StartsWith('.') || name.StartsWith('$')) return true;
        if (!node.IsDirectory && name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;

        var systemNames = new[]
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "Recovery",
            "System Volume Information", "PerfLogs", "$Recycle.Bin", "MSOCache"
        };
        if (systemNames.Any(value => name.Equals(value, StringComparison.OrdinalIgnoreCase))) return true;

        var root = Path.GetPathRoot(node.Path) ?? string.Empty;
        var relative = node.Path[root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        var topLevel = separator >= 0 ? relative[..separator] : relative;
        return systemNames.Any(value => topLevel.Equals(value, StringComparison.OrdinalIgnoreCase));
    }

    private static int GetDirectoryFanoutLimit(float zoom, bool focused) => (zoom, focused) switch
    {
        (< 0.75f, false) => 8,
        (< 1.30f, false) => 14,
        (< 2.20f, false) => 24,
        (< 3.50f, false) => 40,
        (_, false) => 72,
        (< 1.20f, true) => 16,
        (< 2.20f, true) => 36,
        (< 3.60f, true) => 72,
        (_, true) => 140
    };

    private static int GetFileFanoutLimit(float zoom, bool focused) => (zoom, focused) switch
    {
        (< 2.60f, false) => 0,
        (< 3.80f, false) => 5,
        (< 5.20f, false) => 12,
        (_, false) => 24,
        (< 1.15f, true) => 0,
        (< 1.80f, true) => 12,
        (< 2.80f, true) => 30,
        (< 4.20f, true) => 72,
        (< 5.80f, true) => 160,
        (_, true) => 320
    };

    private static int GetRenderBudget(float zoom, int totalNodes) => Math.Min(totalNodes, zoom switch
    {
        < 1.45f => 4_000,
        < 2.75f => 6_500,
        < 4.50f => 10_000,
        _ => 15_000
    });

    private static HashSet<int> BuildFocusedSubtree(IReadOnlyList<VisualNode> nodes,
        IReadOnlyDictionary<int, List<int>> childrenByParent, int selectedNodeIndex,
        IReadOnlySet<int> visibleNodes)
    {
        var focused = new HashSet<int>();
        if (selectedNodeIndex < 0 || selectedNodeIndex >= nodes.Count) return focused;
        var pending = new Queue<int>();
        pending.Enqueue(selectedNodeIndex);
        while (pending.Count > 0)
        {
            var index = pending.Dequeue();
            if (visibleNodes.Contains(index)) focused.Add(index);
            if (!childrenByParent.TryGetValue(index, out var children)) continue;
            foreach (var child in children) pending.Enqueue(child);
        }
        return focused;
    }

    private static HashSet<int> BuildNearbySiblingSet(IReadOnlyList<VisualNode> nodes,
        IReadOnlyDictionary<int, List<int>> childrenByParent, int selectedNodeIndex,
        IReadOnlySet<int> visibleNodes)
    {
        var nearby = new HashSet<int>();
        if (selectedNodeIndex < 0 || selectedNodeIndex >= nodes.Count) return nearby;
        var parentIndex = nodes[selectedNodeIndex].ParentIndex;
        if (parentIndex < 0 || !childrenByParent.TryGetValue(parentIndex, out var siblings)) return nearby;
        foreach (var index in siblings)
        {
            if (index != selectedNodeIndex && visibleNodes.Contains(index)) nearby.Add(index);
        }
        if (nearby.Count > 0) nearby.Add(parentIndex);
        return nearby;
    }

    private static void DrawBundledHierarchyLinks(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, IReadOnlySet<int> visibleNodes, float zoom, Color amber,
        bool focused)
    {
        var maximumBranches = focused
            ? zoom < 2f ? 28 : zoom < 4f ? 64 : 140
            : zoom < 1.2f ? 8 : zoom < 2.8f ? 16 : zoom < 4.8f ? 32 : 56;
        var groups = visibleNodes
            .Where(index => nodes[index].ParentIndex >= 0 && visibleNodes.Contains(nodes[index].ParentIndex))
            .GroupBy(index => nodes[index].ParentIndex);

        foreach (var group in groups)
        {
            var parentIndex = group.Key;
            var children = group
                .OrderByDescending(index => nodes[index].IsDirectory)
                .ThenByDescending(index => nodes[index].SubtreeCount)
                .Take(maximumBranches)
                .ToArray();
            if (children.Length == 0) continue;

            var parentPosition = projected[parentIndex].Position;
            var centroid = Vector2.Zero;
            foreach (var child in children) centroid += projected[child].Position;
            centroid /= children.Length;
            var bundled = children.Length >= 5;
            var junction = bundled ? Vector2.Lerp(parentPosition, centroid, 0.32f) : parentPosition;

            if (bundled)
            {
                ds.DrawLine(parentPosition, junction,
                    Color.FromArgb(focused ? (byte)185 : (byte)82, amber.R, amber.G, amber.B),
                    focused ? 1.45f : 0.85f);
                ds.FillCircle(junction, focused ? 1.5f : 1f,
                    Color.FromArgb(focused ? (byte)215 : (byte)110, amber.R, amber.G, amber.B));
            }

            foreach (var childIndex in children)
            {
                var directoryLink = nodes[childIndex].IsDirectory;
                var alpha = focused
                    ? directoryLink ? (byte)205 : (byte)125
                    : directoryLink ? (byte)76 : (byte)30;
                var width = focused
                    ? directoryLink ? 1.1f : 0.62f
                    : directoryLink ? 0.72f : 0.38f;
                ds.DrawLine(junction, projected[childIndex].Position,
                    Color.FromArgb(alpha, amber.R, amber.G, amber.B), width);
            }
        }
    }

    private void DrawAllNeuronFibers(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, IReadOnlySet<int> drawableNodes, int selectedNodeIndex)
    {
        if (_neuronFiberSprite is null) return;
        var somaPositions = new Dictionary<(int Parent, int Sector), Vector2>();
        var sectorTails = new Dictionary<(int Parent, int Sector), Vector2>();

        void AddFiber(CanvasSpriteBatch batch, Vector2 from, Vector2 to, Vector4 tint)
        {
            var delta = to - from;
            var distance = delta.Length();
            if (distance < 0.35f || distance > 240f) return;
            var angle = MathF.Atan2(delta.Y, delta.X);
            var transform = Matrix3x2.CreateTranslation(0, -6f) *
                            Matrix3x2.CreateScale(distance / 64f, 0.72f) *
                            Matrix3x2.CreateRotation(angle) *
                            Matrix3x2.CreateTranslation(from);
            batch.Draw(_neuronFiberSprite, transform, tint);
        }

        using (var fibers = ds.CreateSpriteBatch(CanvasSpriteSortMode.None))
        {
            foreach (var childIndex in drawableNodes)
            {
                var parentIndex = nodes[childIndex].ParentIndex;
                if (parentIndex < 0 || !drawableNodes.Contains(parentIndex)) continue;
                // File particles form the cortical volume and glow on activity;
                // permanent parent-to-file spokes create the dandelion starbursts.
                // Keep the ambient neural mesh structural: directory to directory.
                if (!nodes[childIndex].IsDirectory) continue;
                var parentPosition = projected[parentIndex].Position;
                var childPosition = projected[childIndex].Position;
                var fiberOrigin = parentPosition;

                // Large directories become a small network of neuron somas. Children
                // connect to their angular sector soma instead of creating one giant
                // radial dandelion from the directory ring.
                if (nodes[parentIndex].ChildCount >= 6)
                {
                    var somaCount = Math.Clamp((nodes[parentIndex].ChildCount + 23) / 24, 2, 6);
                    var childDelta = childPosition - parentPosition;
                    var childAngle = MathF.Atan2(childDelta.Y, childDelta.X);
                    var normalizedAngle = childAngle < 0f ? childAngle + MathF.Tau : childAngle;
                    var sector = Math.Clamp((int)(normalizedAngle / MathF.Tau * somaCount), 0, somaCount - 1);
                    var sectorAngle = (sector + 0.5f) * MathF.Tau / somaCount;
                    var somaRadius = Math.Clamp(8f + MathF.Log2(nodes[parentIndex].ChildCount + 1f) * 1.35f,
                        11f, 25f);
                    var somaPosition = parentPosition + new Vector2(MathF.Cos(sectorAngle), MathF.Sin(sectorAngle)) *
                        somaRadius;
                    var sectorKey = (parentIndex, sector);
                    somaPositions.TryAdd(sectorKey, somaPosition);
                    // Continue a dendrite through nearby siblings instead of
                    // radiating every edge from the same hub.
                    fiberOrigin = sectorTails.GetValueOrDefault(sectorKey, somaPosition);
                    sectorTails[sectorKey] = childPosition;
                }

                var isDirectory = nodes[childIndex].IsDirectory;
                var alpha = selectedNodeIndex >= 0 ? 0.05f : isDirectory ? 0.16f : 0.09f;
                var fiberColor = NodeColor(nodes[childIndex]);
                var tint = new Vector4(fiberColor.R / 255f, fiberColor.G / 255f,
                    fiberColor.B / 255f, alpha);
                AddFiber(fibers, fiberOrigin, childPosition, tint);
            }

            foreach (var (key, somaPosition) in somaPositions)
            {
                var parentPosition = projected[key.Parent].Position;
                var trunk = NodeColor(nodes[key.Parent]);
                AddFiber(fibers, parentPosition, somaPosition,
                    new Vector4(trunk.R / 255f, trunk.G / 255f, trunk.B / 255f,
                        selectedNodeIndex >= 0 ? 0.08f : 0.20f));
            }
        }

        foreach (var somaPosition in somaPositions.Values)
        {
            ds.FillCircle(somaPosition, 4.2f, Color.FromArgb(18, 53, 214, 255));
            ds.FillCircle(somaPosition, 1.15f, Color.FromArgb(190, 72, 224, 255));
        }
    }

    private void DrawSelectedNeuronConnections(CanvasDrawingSession ds,
        IReadOnlyList<VisualNode> nodes, IReadOnlyList<ProjectedNode> projected,
        IReadOnlyDictionary<int, List<int>> childrenByParent, IReadOnlySet<int> drawableNodes,
        int selectedNodeIndex, float time)
    {
        const int maximumConnections = 900;
        const int maximumDepth = 8;
        var amberPulse = _selectionColor;
        var pending = new Queue<(int Index, int Depth)>();
        pending.Enqueue((selectedNodeIndex, 0));
        var connectionCount = 0;

        void DrawNeuronSegment(int parentIndex, int childIndex, int depth)
        {
            if (!drawableNodes.Contains(parentIndex) || !drawableNodes.Contains(childIndex)) return;
            var from = projected[parentIndex].Position;
            var to = projected[childIndex].Position;
            var delta = to - from;
            var distance = MathF.Max(1f, delta.Length());
            var normal = new Vector2(-delta.Y, delta.X) / distance;
            var direction = HashUnit(nodes[childIndex].Path + "|NEURON") < 0.5f ? -1f : 1f;
            var control = Vector2.Lerp(from, to, 0.47f) + normal * direction *
                          Math.Clamp(distance * 0.065f, 1.5f, 24f);
            var branchColor = NodeColor(nodes[childIndex]);
            const int curveSegments = 7;
            for (var segment = 0; segment < curveSegments; segment++)
            {
                Vector2 Point(float amount)
                {
                    var inverse = 1f - amount;
                    return inverse * inverse * from + 2f * inverse * amount * control + amount * amount * to;
                }
                var a = Point(segment / (float)curveSegments);
                var b = Point((segment + 1) / (float)curveSegments);
                var taper = 1f - (segment / (float)curveSegments) * 0.62f;
                var width = MathF.Max(0.34f, (1.25f - depth * 0.10f) * taper);
                ds.DrawLine(a, b, Color.FromArgb(24, branchColor.R, branchColor.G, branchColor.B), width * 3.4f);
                ds.DrawLine(a, b, Color.FromArgb(185, branchColor.R, branchColor.G, branchColor.B), width);
            }

            if (nodes[childIndex].IsDirectory)
            {
                var pulse = 0.5f + 0.5f * MathF.Sin(time * 2.4f + nodes[childIndex].Phase);
                ds.FillCircle(to, 1.1f + pulse * 0.8f,
                    Color.FromArgb((byte)(185 + pulse * 70), amberPulse.R, amberPulse.G, amberPulse.B));
            }
        }

        var parentIndex = nodes[selectedNodeIndex].ParentIndex;
        if (parentIndex >= 0)
            DrawNeuronSegment(parentIndex, selectedNodeIndex, 0);

        while (pending.Count > 0 && connectionCount < maximumConnections)
        {
            var (parent, depth) = pending.Dequeue();
            if (depth >= maximumDepth || !childrenByParent.TryGetValue(parent, out var children)) continue;
            var perDirectoryLimit = nodes[parent].ChildCount >= 24
                ? depth == 0 ? 18 : Math.Max(10, 28 - depth * 3)
                : depth == 0 ? 80 : Math.Max(14, 52 - depth * 6);
            foreach (var child in children.Take(perDirectoryLimit))
            {
                if (!nodes[child].IsDirectory) continue;
                DrawNeuronSegment(parent, child, depth);
                connectionCount++;
                if (nodes[child].IsDirectory) pending.Enqueue((child, depth + 1));
                if (connectionCount >= maximumConnections) break;
            }
        }
    }

    private static void DrawClusterAggregates(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, IReadOnlySet<int> visibleNodes,
        IReadOnlyDictionary<int, List<int>> childrenByParent, float zoom, Color amber, Color pale,
        ICollection<AggregateHit> aggregateHits)
    {
        var showText = zoom >= 1.75f;
        var maximumLabels = showText ? zoom < 3f ? 6 : 12 : 8;
        var aggregates = visibleNodes
            .Where(index => nodes[index].IsDirectory && nodes[index].ChildCount > 0 &&
                            childrenByParent.ContainsKey(index))
            .Select(index =>
            {
                var visibleChildCount = 0;
                var categories = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var child in childrenByParent[index])
                {
                    if (visibleNodes.Contains(child))
                    {
                        visibleChildCount++;
                        continue;
                    }
                    var category = AggregateCategory(nodes[child]);
                    categories[category] = categories.GetValueOrDefault(category) + 1;
                }
                var dominant = categories
                    .Select(category => (Label: category.Key, Count: category.Value))
                    .OrderByDescending(category => category.Count)
                    .ThenBy(category => category.Label, StringComparer.Ordinal)
                    .FirstOrDefault();
                var hidden = Math.Max(0, nodes[index].ChildCount - visibleChildCount);
                return (Index: index, Hidden: hidden,
                    Label: string.IsNullOrEmpty(dominant.Label) ? "FILES" : dominant.Label,
                    CategoryCount: dominant.Count);
            })
            .Where(item => item.Hidden > 0)
            .OrderByDescending(item => item.Hidden)
            .Take(maximumLabels);

        foreach (var aggregate in aggregates)
        {
            var node = nodes[aggregate.Index];
            var position = projected[aggregate.Index].Position;
            var radius = Math.Clamp(4.5f + MathF.Log2(node.ChildCount + 1) * 0.42f, 5f, 9.5f);
            ds.FillCircle(position, radius + 2f, Color.FromArgb(13, amber.R, amber.G, amber.B));
            DrawArc(ds, position, radius, node.Phase * 57.2958f, 115,
                Color.FromArgb(165, amber.R, amber.G, amber.B), 0.95f);
            DrawArc(ds, position, radius + 2.5f, node.Phase * 41f + 180f, 46,
                Color.FromArgb(105, pale.R, pale.G, pale.B), 0.7f);
            if (!showText)
            {
                aggregateHits.Add(new AggregateHit(aggregate.Index, position,
                    new Vector2(radius + 5f, radius + 5f)));
                continue;
            }
            var badgeWidth = 92f;
            var badgeCenter = new Vector2(position.X + radius + badgeWidth * 0.5f - 2f, position.Y - 0.5f);
            ds.FillRectangle(position.X + radius - 2f, position.Y - 8f, badgeWidth, 15f,
                Color.FromArgb(205, 8, 10, 8));
            var count = aggregate.CategoryCount > 0 ? aggregate.CategoryCount : aggregate.Hidden;
            DrawHudText(ds, $"{aggregate.Label} \u00D7 {count:N0}", position.X + radius + 2f, position.Y - 8f,
                badgeWidth - 4f, 15f, Color.FromArgb(225, pale.R, pale.G, pale.B), 7f,
                CanvasHorizontalAlignment.Left);
            aggregateHits.Add(new AggregateHit(aggregate.Index, badgeCenter, new Vector2(badgeWidth * 0.5f, 8f)));
        }
    }

    private static string AggregateCategory(VisualNode node)
    {
        if (node.IsDirectory) return "FOLDERS";
        var extension = Path.GetExtension(node.Name).ToUpperInvariant();
        return extension switch
        {
            ".PDF" => "PDF",
            ".JPG" or ".JPEG" or ".PNG" or ".GIF" or ".WEBP" or ".BMP" or ".SVG" => "IMAGES",
            ".MP4" or ".MOV" or ".AVI" or ".MKV" or ".WEBM" => "VIDEO",
            ".MP3" or ".WAV" or ".FLAC" or ".AAC" or ".M4A" => "AUDIO",
            ".DOC" or ".DOCX" or ".TXT" or ".RTF" or ".ODT" => "DOCUMENTS",
            ".ZIP" or ".7Z" or ".RAR" or ".TAR" or ".GZ" => "ARCHIVES",
            _ => "FILES"
        };
    }

    private Color NodeColor(VisualNode node)
    {
        if (!_colorCodeByDrive) return node.IsDirectory ? _directoryColor : _fileColor;
        return DriveColor(Path.GetPathRoot(node.Path) ?? node.Path);
    }

    private Color DriveColor(string root)
    {
        ReadOnlySpan<uint> palette =
        [
            0x3DDAFF, // C: cyan
            0xA45CFF, // D: violet
            0x40EE87, // E: green
            0xFFB020, // F: amber
            0xFF5CAD, // G: magenta
            0x5C96FF, // H: blue
            0x35E0C1, // I: teal
            0xFF704D  // J: coral, then repeat
        ];
        int paletteIndex;
        if (root.Length >= 2 && root[1] == ':' && char.IsAsciiLetter(root[0]))
            paletteIndex = Math.Max(0, char.ToUpperInvariant(root[0]) - 'C') % palette.Length;
        else
            paletteIndex = (int)(HashUnit(root + "|DRIVE-COLOR") * palette.Length) % palette.Length;
        var rgb = palette[paletteIndex];
        var automatic = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return _driveColors.TryGetValue(root, out var custom) ? ParseColor(custom, automatic) : automatic;
    }

    private float GetNodeVisualRadius(VisualNode node, float projectionScale)
    {
        var appearanceScale = _generalNodeScale * (node.IsDirectory ? _directoryNodeScale : _fileNodeScale);
        if (node.IsDirectory)
        {
            return Math.Clamp(node.Radius * projectionScale * appearanceScale, 0.45f, 24f);
        }

        // The logarithmic file-size radius is precomputed when the visual graph is
        // assembled; a frame only applies perspective scaling.
        return Math.Clamp(node.Radius * projectionScale * appearanceScale, 0.25f, 14f);
    }

    private void DrawFocusedSubtree(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, IReadOnlySet<int> focusedNodes,
        IReadOnlySet<int> searchMatchIndices, float time, float zoom, Color amber, Color gold, Color pale)
    {
        if (_fileNodeSprite is not null && _directoryNodeSprite is not null)
        {
            using var sprites = ds.CreateSpriteBatch(CanvasSpriteSortMode.None);
            foreach (var index in focusedNodes)
            {
                var node = nodes[index];
                var projection = projected[index];
                var depth = Math.Clamp((projection.Depth + 1f) * 0.5f, 0.18f, 1f);
                var radius = GetNodeVisualRadius(node, projection.Scale);
                var alpha = (node.IsDirectory ? 145f + depth * 100f : 85f + depth * 165f) / 255f;
                var nodeColor = NodeColor(node);
                sprites.Draw(node.IsDirectory ? _directoryNodeSprite : _fileNodeSprite,
                    new Windows.Foundation.Rect(projection.Position.X - radius,
                        projection.Position.Y - radius, radius * 2f, radius * 2f),
                    new Vector4(nodeColor.R / 255f, nodeColor.G / 255f, nodeColor.B / 255f, alpha));
            }
        }

        foreach (var index in searchMatchIndices)
        {
            if (!focusedNodes.Contains(index)) continue;
            var node = nodes[index];
            var projection = projected[index];
            var pulse = 0.5f + 0.5f * MathF.Sin(time * 1.8f + node.Phase);
            var radius = GetNodeVisualRadius(node, projection.Scale);
            var teal = _accessColor;
            var matchRadius = MathF.Max(node.IsDirectory ? 9f : 5.5f, radius + (node.IsDirectory ? 6f : 3f));
            ds.DrawCircle(projection.Position, matchRadius,
                Color.FromArgb((byte)(205 + pulse * 45), teal.R, teal.G, teal.B), 1.35f);
            if (node.IsDirectory)
                ds.DrawCircle(projection.Position, radius, teal, 1.5f);
            else
                ds.FillCircle(projection.Position, radius, teal);
        }
    }

    private void DrawFocusedSelection(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, int selectedNodeIndex,
        float width, float height, float zoom, Color gold, Color pale)
    {
        if (selectedNodeIndex < 0 || selectedNodeIndex >= nodes.Count) return;
        var selectedGreen = _selectionColor;
        var projection = projected[selectedNodeIndex];
        var node = nodes[selectedNodeIndex];

        var radius = GetNodeVisualRadius(node, projection.Scale);
        var selectionRadius = MathF.Max(12f, radius + (node.IsDirectory ? 16f : 9f));
        ds.FillCircle(projection.Position, selectionRadius * 1.7f,
            Color.FromArgb(38, selectedGreen.R, selectedGreen.G, selectedGreen.B));
        ds.DrawCircle(projection.Position, selectionRadius, selectedGreen, 2f);
        ds.DrawCircle(projection.Position, selectionRadius + 5f,
            Color.FromArgb(205, selectedGreen.R, selectedGreen.G, selectedGreen.B), 1.1f);
        if (node.IsDirectory)
            ds.DrawCircle(projection.Position, radius, selectedGreen, 1.6f);
        else
            ds.FillCircle(projection.Position, radius, selectedGreen);
        ds.DrawLine(projection.Position + new Vector2(-selectionRadius - 11, 0),
            projection.Position + new Vector2(-selectionRadius - 3, 0), selectedGreen, 1.35f);
        ds.DrawLine(projection.Position + new Vector2(selectionRadius + 3, 0),
            projection.Position + new Vector2(selectionRadius + 11, 0), selectedGreen, 1.35f);
        ds.DrawLine(projection.Position + new Vector2(0, -selectionRadius - 11),
            projection.Position + new Vector2(0, -selectionRadius - 3), selectedGreen, 1.35f);
        ds.DrawLine(projection.Position + new Vector2(0, selectionRadius + 3),
            projection.Position + new Vector2(0, selectionRadius + 11), selectedGreen, 1.35f);

        var prefix = node.IsRoot ? "ROOT // " : node.IsDirectory ? "DIR // " : "FILE // ";
        var label = prefix + TruncateLabel(node.Name.ToUpperInvariant(), 34);
        const float labelWidth = 226f;
        const float labelHeight = 20f;
        var x = projection.Position.X + selectionRadius + 15f;
        if (x + labelWidth > width - 12f) x = projection.Position.X - selectionRadius - labelWidth - 15f;
        var y = Math.Clamp(projection.Position.Y - labelHeight * 0.5f, 36f, height - 36f);
        var lineEnd = new Vector2(x > projection.Position.X ? x - 3f : x + labelWidth + 3f, projection.Position.Y);
        ds.DrawLine(projection.Position, lineEnd, gold, 1.15f);
        var format = GetTextFormat(zoom >= 4f ? 7.2f : 7f, CanvasHorizontalAlignment.Left);
        ds.DrawText(label, x, y, labelWidth, labelHeight, pale, format);
    }

    private void DrawInspectorFilterMatches(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, IReadOnlySet<int> matchIndices, float time,
        IReadOnlySet<int> visibleNodes)
    {
        if (matchIndices.Count == 0) return;
        var filterBlue = Color.FromArgb(255, 61, 140, 255);
        foreach (var index in matchIndices)
        {
            if (index < 0 || index >= nodes.Count || !visibleNodes.Contains(index)) continue;
            var node = nodes[index];
            var projection = projected[index];
            var radius = GetNodeVisualRadius(node, projection.Scale);
            var pulse = 0.5f + 0.5f * MathF.Sin(time * 1.8f + node.Phase);
            var markerRadius = MathF.Max(node.IsDirectory ? 8f : 5f,
                radius + (node.IsDirectory ? 5f : 2.5f));
            ds.FillCircle(projection.Position, markerRadius * 1.45f,
                Color.FromArgb((byte)(10 + pulse * 12), filterBlue.R, filterBlue.G, filterBlue.B));
            ds.DrawCircle(projection.Position, markerRadius,
                Color.FromArgb(235, filterBlue.R, filterBlue.G, filterBlue.B), 1.45f);
            if (node.IsDirectory)
                ds.DrawCircle(projection.Position, radius, filterBlue, 1.4f);
            else
                ds.FillCircle(projection.Position, radius, filterBlue);
        }
    }

    private void DrawInspectorBrowseSelection(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, int selectedNodeIndex, int inspectorNodeIndex)
    {
        if (selectedNodeIndex < 0 || selectedNodeIndex >= nodes.Count ||
            inspectorNodeIndex < 0 || inspectorNodeIndex >= nodes.Count ||
            inspectorNodeIndex == selectedNodeIndex) return;

        var branch = new List<(int Parent, int Child)>();
        var cursor = inspectorNodeIndex;
        var guard = 0;
        while (cursor != selectedNodeIndex && cursor >= 0 && cursor < nodes.Count && guard++ < nodes.Count)
        {
            var parent = nodes[cursor].ParentIndex;
            if (parent < 0 || parent >= nodes.Count) return;
            branch.Add((parent, cursor));
            cursor = parent;
        }
        if (cursor != selectedNodeIndex) return;

        var secondaryGreen = _selectionColor;
        var projection = projected[inspectorNodeIndex];
        var node = nodes[inspectorNodeIndex];
        var nodeRadius = GetNodeVisualRadius(node, projection.Scale);
        var selectionRadius = MathF.Max(node.IsDirectory ? 7f : 4.5f,
            nodeRadius + (node.IsDirectory ? 3.5f : 2f));
        ds.DrawCircle(projection.Position, selectionRadius, secondaryGreen, 1.35f);
        if (node.IsDirectory)
            ds.DrawCircle(projection.Position, nodeRadius, secondaryGreen, 1.35f);
        else
            ds.FillCircle(projection.Position, nodeRadius, secondaryGreen);
    }

    private static Vector3 GetBrainPosition(VisualNode node) => node.BrainPosition;

    private static (Vector3 Position, float Phase, float FileRadiusUnit) ComputeBrainPlacement(
        string path, bool isRoot, bool isDirectory, int depth)
    {
        // Compute every placement channel in one pass over the path. On a saved map
        // with hundreds of thousands of long paths this replaces five full string
        // scans and avoids temporary "|FILE-NEURON" allocations per file.
        var phaseHash = 2166136261u;
        var longitudeHash = 2166136261u ^ 0x9E3779B9u;
        var verticalHash = 2166136261u ^ 0x85EBCA6Bu;
        var jitterHash = 2166136261u ^ 0xC2B2AE35u;
        var fileRadiusHash = 2166136261u ^ 0x27D4EB2Fu;
        unchecked
        {
            foreach (var character in path)
            {
                var normalized = char.ToUpperInvariant(character);
                phaseHash = (phaseHash ^ normalized) * 16777619u;
                longitudeHash = (longitudeHash ^ normalized) * 16777619u;
                verticalHash = (verticalHash ^ normalized) * 16777619u;
                jitterHash = (jitterHash ^ normalized) * 16777619u;
                fileRadiusHash = (fileRadiusHash ^ normalized) * 16777619u;
            }
        }

        static float MixedUnit(uint hash)
        {
            unchecked
            {
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                hash *= 0x846CA68Bu;
                hash ^= hash >> 16;
                return hash / (float)uint.MaxValue;
            }
        }

        var longitude = MixedUnit(longitudeHash) * MathF.Tau;
        var vertical = MixedUnit(verticalHash) * 2f - 1f;
        var horizontal = MathF.Sqrt(MathF.Max(0f, 1f - vertical * vertical));
        var direction = new Vector3(horizontal * MathF.Cos(longitude), vertical,
            horizontal * MathF.Sin(longitude));
        var jitter = (MixedUnit(jitterHash) - 0.5f) * 0.035f;
        var radius = isRoot ? 0.10f + jitter * 0.25f : isDirectory
            ? depth switch
            {
                <= 1 => 0.30f + jitter,
                2 => 0.44f + jitter,
                3 => 0.57f + jitter,
                4 => 0.68f + jitter,
                5 => 0.76f + jitter,
                _ => 0.83f + jitter
            }
            : 0.93f + jitter;
        var phase = (phaseHash & 0x00FFFFFF) / 16777215f * MathF.Tau;
        return (WarpBrainPoint(direction * radius), phase, MixedUnit(fileRadiusHash));
    }

    private static Vector3 WarpBrainPoint(Vector3 point)
    {
        var radius = point.Length();
        if (radius < 0.0001f) return point;
        var unit = point / radius;

        // Keep drive roots in a compact shared nucleus.
        if (radius < 0.20f)
            return new Vector3(point.X * 1.15f, point.Y * 0.78f, point.Z * 0.68f);

        // Lateral anatomical profile: broad frontal lobe on the left, tapered
        // occipital lobe on the right, temporal undercut, cerebellum and brainstem.
        var frontalBulge = 1f + MathF.Max(0f, -unit.X) * 0.12f;
        var posteriorTaper = 1f - MathF.Max(0f, unit.X - 0.38f) * 0.13f;
        var superiorBulge = 1f + MathF.Max(0f, unit.Y) * 0.10f;
        var inferiorTaper = 1f - MathF.Max(0f, -unit.Y - 0.18f) * 0.22f;
        var fold = 1f + 0.025f * MathF.Sin(unit.Y * 19f + unit.Z * 13f) +
                   0.016f * MathF.Sin(unit.X * 17f - unit.Z * 23f);
        var x = unit.X * 0.94f * radius * frontalBulge * posteriorTaper * fold;
        var y = unit.Y * 0.70f * radius * superiorBulge * inferiorTaper;
        var z = unit.Z * 0.55f * radius * fold;

        // Pull the posterior-inferior sector into a distinct cerebellar bulb.
        if (unit.X > 0.10f && unit.Y < -0.38f)
        {
            x = (0.43f + unit.X * 0.36f) * radius;
            y = (-0.43f + (unit.Y + 0.38f) * 0.38f) * radius;
            z *= 0.76f;
        }
        // Narrow inferior-medial points into a visible descending brainstem.
        else if (unit.Y < -0.70f && MathF.Abs(unit.X) < 0.28f)
        {
            x = (0.12f + unit.X * 0.24f) * radius;
            y = (-0.62f + (unit.Y + 0.70f) * 0.72f) * radius;
            z *= 0.45f;
        }
        return new Vector3(x, y, z);
    }

    private static void DrawBrainCircuitShell(CanvasDrawingSession ds, Vector2 center, float scale,
        Matrix4x4 rotation, Vector3 focusPivot, float time, Color amber, Color gold, Color pale,
        float opacity, bool showNeurons)
    {
        if (opacity <= 0.001f) return;

        (Vector2 Position, float Depth) Project(Vector3 point)
        {
            var rotated = Vector3.Transform(WarpBrainPoint(point) - focusPivot, rotation);
            var perspective = 0.82f + (rotated.Z + 1f) * 0.13f;
            return (center + new Vector2(rotated.X, -rotated.Y) * scale * perspective, rotated.Z);
        }

        void Segment(Vector3 from, Vector3 to, int baseAlpha, float width, bool bright = false)
        {
            var a = Project(from);
            var b = Project(to);
            var depth = Math.Clamp(((a.Depth + b.Depth) * 0.25f) + 0.55f, 0.18f, 1f);
            var alpha = (byte)Math.Clamp(baseAlpha * depth * opacity, 0f, 255f);
            var cyan = Color.FromArgb(alpha, 36, 199, 255);
            var violet = Color.FromArgb(alpha, 129, 72, 255);
            var hot = Color.FromArgb(alpha, 255, 105, 190);
            var color = bright ? hot : ((from.Y + to.Y + from.Z) > 0.22f ? cyan : violet);
            ds.DrawLine(a.Position, b.Position,
                Color.FromArgb((byte)(alpha * 0.16f), color.R, color.G, color.B), width * 4.5f);
            ds.DrawLine(a.Position, b.Position, color, width);
        }

        const int longitudeSamples = 72;
        for (var longitude = 0; longitude < 18; longitude++)
        {
            var angle = longitude * MathF.Tau / 18f + MathF.Sin(longitude * 1.7f) * 0.025f;
            for (var sample = 0; sample < longitudeSamples; sample++)
            {
                // Broken traces create circuitry, not a wireframe cage.
                if ((sample + longitude * 5) % 13 is 8 or 9 or 10) continue;
                var polarA = -MathF.PI * 0.5f + sample * MathF.PI / longitudeSamples;
                var polarB = -MathF.PI * 0.5f + (sample + 1) * MathF.PI / longitudeSamples;
                Vector3 Point(float polar) => new(
                    MathF.Cos(polar) * MathF.Cos(angle), MathF.Sin(polar),
                    MathF.Cos(polar) * MathF.Sin(angle));
                Segment(Point(polarA), Point(polarB), 82, 0.68f,
                    (sample + longitude * 11) % 47 == 0);
            }
        }

        const int latitudeSamples = 96;
        for (var latitude = -7; latitude <= 7; latitude++)
        {
            var vertical = latitude / 8f;
            var ringRadius = MathF.Sqrt(MathF.Max(0f, 1f - vertical * vertical));
            var phase = latitude * 0.29f + time * (latitude % 3 == 0 ? 0.006f : -0.004f);
            for (var sample = 0; sample < latitudeSamples; sample++)
            {
                if ((sample * 3 + latitude * 7 + 200) % 19 is 13 or 14 or 15 or 16) continue;
                var angleA = phase + sample * MathF.Tau / latitudeSamples;
                var angleB = phase + (sample + 1) * MathF.Tau / latitudeSamples;
                Vector3 Point(float angle) => new(ringRadius * MathF.Cos(angle), vertical,
                    ringRadius * MathF.Sin(angle));
                Segment(Point(angleA), Point(angleB), latitude == 0 ? 118 : 72,
                    latitude == 0 ? 1f : 0.66f, (sample + latitude * 9 + 100) % 53 == 0);
            }
        }

        // Nested asynchronous shells give the construct its dense cognitive volume.
        // They are independent decorative circuits, never filesystem relationship lines.
        var innerShells = new[] { 0.42f, 0.61f, 0.79f };
        for (var shellIndex = 0; shellIndex < innerShells.Length; shellIndex++)
        {
            var shellRadius = innerShells[shellIndex];
            for (var band = -3; band <= 3; band++)
            {
                var vertical = band / 4.2f * shellRadius;
                var ringRadius = MathF.Sqrt(MathF.Max(0f, shellRadius * shellRadius - vertical * vertical));
                var phase = shellIndex * 0.71f + band * 0.17f + time * (0.008f + shellIndex * 0.003f);
                const int samples = 64;
                for (var sample = 0; sample < samples; sample++)
                {
                    if ((sample + band * 3 + shellIndex * 7 + 40) % 11 is 7 or 8 or 9) continue;
                    var angleA = phase + sample * MathF.Tau / samples;
                    var angleB = phase + (sample + 1) * MathF.Tau / samples;
                    Vector3 Point(float angle) => new(ringRadius * MathF.Cos(angle), vertical,
                        ringRadius * MathF.Sin(angle));
                    Segment(Point(angleA), Point(angleB), 54 + shellIndex * 10, 0.58f,
                        (sample + band * 13 + shellIndex) % 41 == 0);
                }
            }

            for (var meridian = 0; meridian < 7; meridian++)
            {
                var angle = meridian * MathF.Tau / 7f + shellIndex * 0.37f;
                const int samples = 42;
                for (var sample = 0; sample < samples; sample++)
                {
                    if ((sample + meridian * 2 + shellIndex) % 10 is 6 or 7) continue;
                    var polarA = -MathF.PI * 0.5f + sample * MathF.PI / samples;
                    var polarB = -MathF.PI * 0.5f + (sample + 1) * MathF.PI / samples;
                    Vector3 Point(float polar) => new(
                        shellRadius * MathF.Cos(polar) * MathF.Cos(angle), shellRadius * MathF.Sin(polar),
                        shellRadius * MathF.Cos(polar) * MathF.Sin(angle));
                    Segment(Point(polarA), Point(polarB), 48 + shellIndex * 9, 0.55f);
                }
            }
        }

        // Winding cortical tracts replace the visual rhythm of latitude rings with
        // recognizable gyri. Each hemisphere gets its own irregular folded paths.
        for (var hemisphere = -1; hemisphere <= 1; hemisphere += 2)
        {
            for (var tract = 0; tract < 15; tract++)
            {
                var baseLatitude = -0.82f + tract * 1.64f / 14f;
                var tractPhase = tract * 0.83f + hemisphere * 0.31f;
                const int samples = 34;
                for (var sample = 0; sample < samples; sample++)
                {
                    if ((sample + tract * 3) % 17 is 12 or 13 or 14) continue;
                    Vector3 CortexPoint(int step)
                    {
                        var amount = step / (float)samples;
                        var longitude = -1.36f + amount * 2.72f;
                        var latitude = baseLatitude + 0.055f * MathF.Sin(longitude * 4.2f + tractPhase) +
                                       0.026f * MathF.Sin(longitude * 9.3f - tractPhase * 0.7f);
                        var latitudeCosine = MathF.Cos(latitude);
                        return new Vector3(
                            hemisphere * MathF.Abs(latitudeCosine * MathF.Cos(longitude)),
                            MathF.Sin(latitude), latitudeCosine * MathF.Sin(longitude)) * 0.985f;
                    }
                    Segment(CortexPoint(sample), CortexPoint(sample + 1), 88, 0.72f,
                        (sample + tract * 5) % 43 == 0);
                }
            }

            // The medial contour leaves a dark longitudinal cleft between lobes.
            const int fissureSamples = 70;
            for (var sample = 0; sample < fissureSamples; sample++)
            {
                if (sample % 14 is 9 or 10) continue;
                Vector3 FissurePoint(int step)
                {
                    var angle = -MathF.PI * 0.92f + step * MathF.PI * 1.84f / fissureSamples;
                    var edge = new Vector3(hemisphere * 0.026f, MathF.Sin(angle), MathF.Cos(angle));
                    return Vector3.Normalize(edge) * 0.997f;
                }
                Segment(FissurePoint(sample), FissurePoint(sample + 1), 112, 0.86f,
                    sample % 23 == 0);
            }
        }

        if (showNeurons)
        {
            var neuronCyan = Color.FromArgb(255, 67, 218, 255);
            var neuronViolet = Color.FromArgb(255, 164, 76, 255);
            var neuronHot = Color.FromArgb(255, 255, 112, 42);

            Vector3 BranchDirection(int seed)
            {
                var a = seed * 2.3999632f;
                var y = MathF.Sin(seed * 1.731f) * 0.72f;
                var radial = MathF.Sqrt(MathF.Max(0.01f, 1f - y * y));
                return Vector3.Normalize(new Vector3(MathF.Cos(a) * radial, y, MathF.Sin(a) * radial));
            }

            void NeuronLine(Vector3 from, Vector3 to, Color color, float width, int alpha)
            {
                var a = Project(from);
                var b = Project(to);
                var depth = Math.Clamp(((a.Depth + b.Depth) * 0.25f) + 0.58f, 0.18f, 1f);
                var visibleAlpha = (byte)Math.Clamp(alpha * depth * opacity, 0f, 255f);
                ds.DrawLine(a.Position, b.Position,
                    Color.FromArgb((byte)(visibleAlpha * 0.18f), color.R, color.G, color.B), width * 5.2f);
                ds.DrawLine(a.Position, b.Position,
                    Color.FromArgb(visibleAlpha, color.R, color.G, color.B), width);
            }

            void GrowDendrite(Vector3 start, Vector3 direction, float length, int depth, int seed, Color color)
            {
                var end = start + direction * length;
                var endLength = end.Length();
                if (endLength > 0.90f) end *= 0.90f / endLength;
                if (start.X * end.X < 0f) end.X = MathF.CopySign(MathF.Abs(end.X) + 0.025f, start.X);
                NeuronLine(start, end, color, MathF.Max(0.38f, 1.7f - depth * 0.31f), 188 - depth * 22);
                if (depth >= 4) return;

                var jitterA = BranchDirection(seed * 7 + depth * 19 + 3);
                var jitterB = BranchDirection(seed * 11 + depth * 23 + 9);
                var nextA = Vector3.Normalize(direction * 0.72f + jitterA * (0.34f + depth * 0.035f));
                var nextB = Vector3.Normalize(direction * 0.72f + jitterB * (0.34f + depth * 0.035f));
                GrowDendrite(end, nextA, length * 0.69f, depth + 1, seed + 31, color);
                GrowDendrite(end, nextB, length * 0.66f, depth + 1, seed + 47, color);
            }

            for (var neuron = 0; neuron < 14; neuron++)
            {
                var somaDirection = BranchDirection(neuron * 17 + 5);
                var soma = somaDirection * (0.27f + (neuron % 4) * 0.105f);
                var somaView = Project(soma);
                var somaColor = neuron % 3 == 0 ? neuronHot : neuron % 2 == 0 ? neuronViolet : neuronCyan;
                var somaRadius = neuron % 3 == 0 ? 2.3f : 1.65f;
                ds.FillCircle(somaView.Position, somaRadius * 4.2f,
                    Color.FromArgb((byte)(30 * opacity), somaColor.R, somaColor.G, somaColor.B));
                ds.FillCircle(somaView.Position, somaRadius,
                    Color.FromArgb((byte)(235 * opacity), somaColor.R, somaColor.G, somaColor.B));

                for (var dendrite = 0; dendrite < 5; dendrite++)
                {
                    var spread = BranchDirection(neuron * 101 + dendrite * 29 + 13);
                    var outward = Vector3.Normalize(somaDirection * 0.48f + spread * 0.72f);
                    GrowDendrite(soma, outward, 0.115f + dendrite * 0.012f, 0,
                        neuron * 211 + dendrite * 37, somaColor);
                }
            }
        }

        // Sparse luminous junctions make the shell feel computational without
        // turning every indexed file into a glowing bead.
        for (var marker = 0; marker < 96; marker++)
        {
            var y = -0.96f + 1.92f * ((marker * 37 % 97) / 96f);
            var radial = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
            var angle = marker * 2.3999632f;
            var projected = Project(new Vector3(radial * MathF.Cos(angle), y, radial * MathF.Sin(angle)));
            var depth = Math.Clamp(projected.Depth * 0.45f + 0.58f, 0.15f, 1f);
            var markerColor = marker % 11 == 0
                ? Color.FromArgb(255, 255, 133, 34)
                : marker % 3 == 0
                    ? Color.FromArgb(255, 255, 76, 170)
                    : Color.FromArgb(255, 52, 220, 255);
            var markerRadius = marker % 11 == 0 ? 1.45f : 0.72f;
            ds.FillCircle(projected.Position, markerRadius * 3f,
                Color.FromArgb((byte)(32 * depth * opacity), markerColor.R, markerColor.G, markerColor.B));
            ds.FillCircle(projected.Position, markerRadius,
                Color.FromArgb((byte)(205 * depth * opacity), markerColor.R, markerColor.G, markerColor.B));
        }
    }

    private Matrix4x4 CreateAmbientRotation(float time)
    {
        var direction = _reverseOrbit ? -1f : 1f;
        var motionTime = _autoOrbit ? time * _orbitSpeed : 0f;
        var yaw = _orbitPhaseRadians + direction * motionTime * 0.075f;
        var pitch = _orbitTiltRadians + (_autoOrbit ? MathF.Sin(motionTime * 0.12f) * 0.025f : 0f);
        var roll = _autoOrbit ? MathF.Sin(motionTime * 0.08f) * 0.025f : 0f;
        return Matrix4x4.CreateRotationY(yaw) *
               Matrix4x4.CreateRotationX(pitch) *
               Matrix4x4.CreateRotationZ(roll);
    }

    private void DrawActiveBranches(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, Vector2 center, IReadOnlyList<ActiveActivity> activities,
        float liveTime, Color amber, Color gold, Color pale, IReadOnlyDictionary<string, int> pathIndex)
    {
        foreach (var active in activities.OrderBy(item => item.Activity.Timestamp))
        {
            var activity = active.Activity;
            var targetIndex = ResolveNodeIndex(nodes, pathIndex, activity.Path);
            if (targetIndex < 0 && activity.PreviousPath is not null)
                targetIndex = ResolveNodeIndex(nodes, pathIndex, activity.PreviousPath);
            if (targetIndex < 0) continue;

            var signal = ActivityColor(activity.Kind);
            var measuredSize = activity.SizeBytes > 0 ? activity.SizeBytes : nodes[targetIndex].SizeBytes;
            var lifetimeSeconds = Math.Max(0.1, (active.ExpiresAt - activity.Timestamp).TotalSeconds);
            var remaining = Math.Clamp((active.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds / lifetimeSeconds, 0, 1);
            var load = ActivityLoad(activity, measuredSize) * MathF.Sqrt((float)remaining);

            if (_activityVisualizationMode == ActivityVisualizationMode.NodeGlowOnly)
            {
                var targetProjection = projected[targetIndex];
                var pulse = 0.5f + 0.5f * MathF.Sin(liveTime * (2.2f + load * 1.8f));
                var nodeRadius = GetNodeVisualRadius(nodes[targetIndex], targetProjection.Scale);
                var glowRadius = nodeRadius + 3f + pulse * (2f + load * 5f);
                ds.FillCircle(targetProjection.Position, glowRadius * 1.65f,
                    Color.FromArgb((byte)(5 + load * 20), signal.R, signal.G, signal.B));
                ds.FillCircle(targetProjection.Position, glowRadius,
                    Color.FromArgb((byte)(10 + load * 34), signal.R, signal.G, signal.B));
                ds.DrawCircle(targetProjection.Position, glowRadius,
                    Color.FromArgb((byte)(95 + load * 120), signal.R, signal.G, signal.B),
                    0.7f + load * 0.8f);
                if (nodes[targetIndex].IsDirectory)
                    ds.DrawCircle(targetProjection.Position, nodeRadius,
                        Color.FromArgb(225, signal.R, signal.G, signal.B), 1.2f);
                else
                    ds.FillCircle(targetProjection.Position, MathF.Max(0.7f, nodeRadius),
                        Color.FromArgb(225, signal.R, signal.G, signal.B));
                continue;
            }

            List<int> AncestorsToRoot(int index)
            {
                var result = new List<int>();
                var guard = 0;
                while (index >= 0 && index < nodes.Count && guard++ < nodes.Count)
                {
                    result.Add(index);
                    index = nodes[index].ParentIndex;
                }
                return result;
            }

            var targetRoute = AncestorsToRoot(targetIndex);
            targetRoute.Reverse();
            List<int> route;
            if (activity.Kind == FileActivityKind.Moved && activity.PreviousPath is not null)
            {
                var sourceIndex = ResolveNodeIndex(nodes, pathIndex, activity.PreviousPath);
                route = sourceIndex >= 0 ? AncestorsToRoot(sourceIndex) : [];
                if (route.Count > 0 && targetRoute.Count > 0 && route[^1] == targetRoute[0])
                    route.AddRange(targetRoute.Skip(1));
                else
                    route.AddRange(targetRoute);
            }
            else route = targetRoute;
            if (route.Count == 0) continue;

            Vector2 RoutePoint(float amount)
            {
                if (route.Count == 1) return projected[route[0]].Position;
                var scaled = Math.Clamp(amount, 0f, 0.999999f) * (route.Count - 1);
                var edge = Math.Min(route.Count - 2, (int)scaled);
                var local = scaled - edge;
                var firstIndex = route[edge];
                var secondIndex = route[edge + 1];
                var forward = nodes[secondIndex].ParentIndex == firstIndex;
                var parentIndex = forward ? firstIndex : secondIndex;
                var childIndex = forward ? secondIndex : firstIndex;
                var from = projected[firstIndex].Position;
                var to = projected[secondIndex].Position;

                Vector2 CurvePoint(Vector2 curveFrom, Vector2 curveTo, float curveAmount)
                {
                    var curveDelta = curveTo - curveFrom;
                    var curveDistance = MathF.Max(1f, curveDelta.Length());
                    var curveNormal = new Vector2(-curveDelta.Y, curveDelta.X) / curveDistance;
                    var sign = HashUnit(nodes[childIndex].Path + "|NEURON") < 0.5f ? -1f : 1f;
                    var curveControl = Vector2.Lerp(curveFrom, curveTo, 0.5f) + curveNormal * sign *
                                       Math.Clamp(curveDistance * 0.065f, 1.5f, 24f);
                    var inverseAmount = 1f - curveAmount;
                    return inverseAmount * inverseAmount * curveFrom +
                           2f * inverseAmount * curveAmount * curveControl +
                           curveAmount * curveAmount * curveTo;
                }

                // The visible mesh routes crowded directories through small sector
                // somas. Activity uses the identical waypoint instead of cutting
                // directly from the parent to the child.
                if (nodes[parentIndex].ChildCount >= 24)
                {
                    var parentPosition = projected[parentIndex].Position;
                    var childPosition = projected[childIndex].Position;
                    var somaCount = Math.Clamp((nodes[parentIndex].ChildCount + 95) / 96, 2, 12);
                    var childDelta = childPosition - parentPosition;
                    var childAngle = MathF.Atan2(childDelta.Y, childDelta.X);
                    var normalizedAngle = childAngle < 0f ? childAngle + MathF.Tau : childAngle;
                    var sector = Math.Clamp((int)(normalizedAngle / MathF.Tau * somaCount), 0, somaCount - 1);
                    var sectorAngle = (sector + 0.5f) * MathF.Tau / somaCount;
                    var somaRadius = Math.Clamp(8f + MathF.Log2(nodes[parentIndex].ChildCount + 1f) * 1.35f,
                        11f, 25f);
                    var soma = parentPosition + new Vector2(MathF.Cos(sectorAngle), MathF.Sin(sectorAngle)) *
                        somaRadius;
                    var forwardAmount = forward ? local : 1f - local;
                    return forwardAmount < 0.28f
                        ? CurvePoint(parentPosition, soma, forwardAmount / 0.28f)
                        : CurvePoint(soma, childPosition, (forwardAmount - 0.28f) / 0.72f);
                }

                var delta = to - from;
                var distance = MathF.Max(1f, delta.Length());
                var normal = new Vector2(-delta.Y, delta.X) / distance;
                var bendSign = HashUnit(nodes[childIndex].Path + "|NEURON") < 0.5f ? -1f : 1f;
                // Live traffic follows the compact neuron corridor between this
                // child and its parent. The shallow bend cannot arc beyond the
                // brain shell like the former long shortcut could.
                var control = Vector2.Lerp(from, to, 0.5f) + normal * bendSign *
                              Math.Clamp(distance * 0.065f, 1.5f, 24f);
                var inverse = 1f - local;
                return inverse * inverse * from + 2f * inverse * local * control + local * local * to;
            }

            var pathSegments = Math.Clamp((route.Count - 1) * 7, 7, 140);
            var routePoints = new Vector2[pathSegments + 1];
            var cumulativeDistance = new float[pathSegments + 1];
            for (var sample = 0; sample <= pathSegments; sample++)
            {
                routePoints[sample] = RoutePoint(sample / (float)pathSegments);
                if (sample > 0)
                    cumulativeDistance[sample] = cumulativeDistance[sample - 1] +
                                                 Vector2.Distance(routePoints[sample - 1], routePoints[sample]);
            }

            Vector2 TravelPoint(float progress)
            {
                var totalDistance = cumulativeDistance[^1];
                if (totalDistance <= 0.001f) return routePoints[^1];
                var targetDistance = Math.Clamp(progress, 0f, 1f) * totalDistance;
                var upper = Array.BinarySearch(cumulativeDistance, targetDistance);
                if (upper < 0) upper = ~upper;
                upper = Math.Clamp(upper, 1, pathSegments);
                var lower = upper - 1;
                var span = cumulativeDistance[upper] - cumulativeDistance[lower];
                var localProgress = span <= 0.001f
                    ? 1f
                    : (targetDistance - cumulativeDistance[lower]) / span;
                return Vector2.Lerp(routePoints[lower], routePoints[upper], localProgress);
            }

            for (var segment = 0; segment < pathSegments; segment++)
            {
                var from = routePoints[segment];
                var to = routePoints[segment + 1];
                ds.DrawLine(from, to, Color.FromArgb((byte)(8 + load * 24), signal.R, signal.G, signal.B),
                    1.1f + load * 2.2f);
                ds.DrawLine(from, to, Color.FromArgb((byte)(125 + load * 125), signal.R, signal.G, signal.B),
                    0.38f + load * 0.72f);
            }

            var particleCount = 1 + (int)MathF.Floor(load * 3f);
            var elapsedSeconds = Math.Max(0d, (DateTimeOffset.UtcNow - activity.Timestamp).TotalSeconds);
            var travelDuration = Math.Max(0.45d, lifetimeSeconds * 0.74d);
            for (var particle = 0; particle < particleCount; particle++)
            {
                // Each event traverses its path once. Staggered particles form a
                // short packet behind the leader; none wraps to the opposite side.
                var delay = particle * Math.Min(0.13d, travelDuration / (particleCount + 2d));
                if (elapsedSeconds < delay) continue;
                var travel = (float)Math.Clamp((elapsedSeconds - delay) / travelDuration, 0d, 1d);
                var point = TravelPoint(travel);
                ds.FillCircle(point, 0.9f + load * 1.6f,
                    Color.FromArgb((byte)(5 + load * 20), signal.R, signal.G, signal.B));
                ds.FillCircle(point, 0.48f + load * 0.62f,
                    Color.FromArgb((byte)(180 + load * 75), signal.R, signal.G, signal.B));
            }

            var targetPosition = projected[targetIndex].Position;
            var targetPulse = 0.5f + 0.5f * MathF.Sin(liveTime * (2.6f + load * 2.2f));
            var targetRadius = (nodes[targetIndex].IsDirectory ? 7f : 4.2f) + load * 5.5f;
            ds.FillCircle(targetPosition, targetRadius + load * 10f * targetPulse,
                Color.FromArgb((byte)(4 + load * 30), signal.R, signal.G, signal.B));
            ds.DrawCircle(targetPosition, targetRadius + targetPulse * (1f + load * 3f),
                Color.FromArgb((byte)(145 + load * 110), signal.R, signal.G, signal.B),
                0.8f + load * 1.5f);
            ds.FillCircle(targetPosition, 1f + load * 1.7f,
                Color.FromArgb(245, signal.R, signal.G, signal.B));
        }
    }

    private Color ActivityColor(FileActivityKind kind) => kind switch
    {
        FileActivityKind.Accessed => _accessColor,
        FileActivityKind.Modified => _writeColor,
        FileActivityKind.Created => _createColor,
        FileActivityKind.Moved => _moveColor,
        FileActivityKind.Deleted => _deleteColor,
        _ => Color.FromArgb(255, 255, 205, 92)
    };

    private static float ActivityLoad(FileActivity activity, long measuredSize)
    {
        var sizeLoad = measuredSize <= 0
            ? 0f
            : Math.Clamp((MathF.Log2(measuredSize + 1f) - 10f) / 20f, 0f, 1f);
        var trafficLoad = Math.Clamp((activity.SignalCount - 1f) / 12f, 0f, 1f);
        var baseline = activity.Kind switch
        {
            FileActivityKind.Accessed => 0.10f,
            FileActivityKind.Modified => 0.18f,
            FileActivityKind.Created => 0.22f,
            FileActivityKind.Deleted => 0.16f,
            FileActivityKind.Moved => 0.26f,
            _ => 0.12f
        };
        return Math.Clamp(baseline + sizeLoad * 0.46f + trafficLoad * 0.34f, 0.08f, 1f);
    }

    private static int ResolveNodeIndex(IReadOnlyList<VisualNode> nodes,
        IReadOnlyDictionary<string, int> pathIndex, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return -1;
        if (pathIndex.TryGetValue(path, out var exact)) return exact;
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(parent))
        {
            if (pathIndex.TryGetValue(parent, out var directoryIndex) && nodes[directoryIndex].IsDirectory)
                return directoryIndex;
            var next = Path.GetDirectoryName(parent);
            if (string.Equals(next, parent, StringComparison.OrdinalIgnoreCase)) break;
            parent = next;
        }
        return -1;
    }

    private static bool IsPathWithin(string path, string directory)
    {
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == directory.Length) return true;
        var final = directory[^1];
        return final is '\\' or '/' || path[directory.Length] is '\\' or '/';
    }

    private void DrawActivityReadout(CanvasDrawingSession ds, float width, float height,
        IReadOnlyList<ActiveActivity> activities, Color amber, Color pale)
    {
        if (activities.Count == 0) return;
        var latest = activities.MaxBy(item => item.Activity.Timestamp).Activity;
        var color = ActivityColor(latest.Kind);
        var mode = latest.Kind switch
        {
            FileActivityKind.Moved => "MOVE / CUT TRANSFER",
            FileActivityKind.Accessed => "READ / ACCESS",
            FileActivityKind.Modified => "WRITE / PROCESS",
            FileActivityKind.Created => "CREATE / COPY",
            FileActivityKind.Deleted => "DELETE",
            _ => latest.Kind.ToString().ToUpperInvariant()
        };
        var name = Path.GetFileName(latest.Path);
        if (string.IsNullOrWhiteSpace(name)) name = latest.Path;
        DrawHudText(ds, $"LIVE ACCESS // {mode}  ·  {TruncateLabel(name.ToUpperInvariant(), 34)}",
            width * 0.5f - 290, height * 0.105f, 580, 18, color, 8f, CanvasHorizontalAlignment.Center);
        var load = ActivityLoad(latest, latest.SizeBytes);
        var size = latest.SizeBytes > 0 ? FormatBytes(latest.SizeBytes) : "SIZE UNKNOWN";
        DrawHudText(ds,
            $"LOAD {load * 100:00}%  //  {size.ToUpperInvariant()}  //  TRAFFIC {latest.SignalCount:00}  //  " +
            TruncateLabel(latest.Path.ToUpperInvariant(), 58),
            width * 0.5f - 330, height * 0.128f, 660, 16,
            Color.FromArgb(185, pale.R, pale.G, pale.B), 6f, CanvasHorizontalAlignment.Center);
        DrawHudText(ds, "CYAN READ  //  AMBER WRITE  //  GREEN CREATE/COPY  //  VIOLET MOVE/CUT  //  RED DELETE",
            width * 0.5f - 350, height * 0.147f, 700, 15,
            Color.FromArgb(120, pale.R, pale.G, pale.B), 5.3f, CanvasHorizontalAlignment.Center);
    }

    private static void DrawSemanticLabels(CanvasDrawingSession ds, IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<ProjectedNode> projected, float width, float height, float zoom, int selectedNodeIndex,
        Color amber, Color pale, IReadOnlySet<int>? allowedIndices = null)
    {
        var maximumLabels = zoom switch
        {
            < 1.2f => 6,
            < 2f => 16,
            < 3.5f => 42,
            < 6f => 90,
            _ => 150
        };

        IEnumerable<int> labelPool = allowedIndices ?? Enumerable.Range(0, nodes.Count);
        var candidates = labelPool
            .Where(index => index == selectedNodeIndex || nodes[index].IsRoot ||
                nodes[index].IsDirectory && zoom >= 1.18f || !nodes[index].IsDirectory && zoom >= 2.65f)
            .Where(index => projected[index].Position.X > -80 && projected[index].Position.X < width + 80 &&
                            projected[index].Position.Y > 36 && projected[index].Position.Y < height - 35)
            .OrderByDescending(index => index == selectedNodeIndex)
            .ThenByDescending(index => nodes[index].IsRoot)
            .ThenByDescending(index => nodes[index].IsDirectory)
            .ThenByDescending(index => nodes[index].ChildCount)
            .ThenByDescending(index => projected[index].Depth)
            .Take(maximumLabels * 3);

        var format = GetTextFormat(zoom >= 4f ? 7.2f : 7f, CanvasHorizontalAlignment.Left);

        var occupied = new List<LabelBounds>();
        var drawn = 0;
        foreach (var index in candidates)
        {
            if (drawn >= maximumLabels) break;
            var node = nodes[index];
            var position = projected[index].Position;
            var selected = index == selectedNodeIndex;
            var prefix = node.IsRoot ? "ROOT // " : node.IsDirectory
                ? node.Depth == 1 ? "PRIMARY DIR // " : "DIR // "
                : "FILE // ";
            var label = prefix + TruncateLabel(node.Name.ToUpperInvariant(), selected ? 34 : 22);
            var labelWidth = selected ? 226f : node.IsRoot ? 190f : 154f;
            var labelHeight = selected ? 20f : 16f;
            var x = position.X + 12f;
            if (x + labelWidth > width - 12f) x = position.X - labelWidth - 12f;
            var y = position.Y - labelHeight * 0.5f;
            var bounds = new LabelBounds(x, y, x + labelWidth, y + labelHeight);
            if (!selected && occupied.Any(existing => existing.Intersects(bounds))) continue;

            var lineEnd = new Vector2(x > position.X ? x - 3f : x + labelWidth + 3f, position.Y);
            ds.DrawLine(position, lineEnd,
                selected
                    ? Color.FromArgb(230, amber.R, amber.G, amber.B)
                    : Color.FromArgb(92, amber.R, amber.G, amber.B),
                selected ? 1.2f : 0.7f);
            ds.DrawLine(new Vector2(x, y + labelHeight), new Vector2(x + MathF.Min(52f, labelWidth * 0.42f), y + labelHeight),
                selected
                    ? Color.FromArgb(240, amber.R, amber.G, amber.B)
                    : Color.FromArgb(105, amber.R, amber.G, amber.B), 0.8f);
            ds.DrawText(label, x, y, labelWidth, labelHeight,
                selected
                    ? Color.FromArgb(250, pale.R, pale.G, pale.B)
                    : node.IsRoot ? Color.FromArgb(245, pale.R, pale.G, pale.B)
                    : Color.FromArgb(node.IsDirectory ? (byte)220 : (byte)185, amber.R, amber.G, amber.B),
                format);
            occupied.Add(bounds);
            drawn++;
        }
    }

    private static string TruncateLabel(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..Math.Max(1, maximumLength - 1)] + "…";

    private static void DrawHolographicSphere(CanvasDrawingSession ds, Vector2 center, float scale, float time,
        Color amber, Color gold, Color pale)
    {
        ds.FillCircle(center, scale * 1.01f, Color.FromArgb(4, amber.R, amber.G, amber.B));
        ds.DrawCircle(center, scale * 0.98f, Color.FromArgb(55, amber.R, amber.G, amber.B), 1.1f);
        ds.DrawEllipse(center, scale * 0.98f, scale * 0.34f,
            Color.FromArgb(50, gold.R, gold.G, gold.B), 0.8f);
        ds.DrawEllipse(center, scale * 0.58f, scale * 0.98f,
            Color.FromArgb(37, amber.R, amber.G, amber.B), 0.7f);
        ds.DrawEllipse(center, scale * 0.25f, scale * 0.98f,
            Color.FromArgb(28, pale.R, pale.G, pale.B), 0.6f);

        for (var ring = 0; ring < 5; ring++)
        {
            var radius = scale * (0.42f + ring * 0.13f);
            DrawArc(ds, center, radius, -72 + ring * 51 + time * (3 + ring), 92 + ring * 7,
                Color.FromArgb((byte)(35 + ring * 8), amber.R, amber.G, amber.B), 0.65f + ring * 0.09f);
        }

        for (var index = 0; index < 28; index++)
        {
            var angle = index * MathF.Tau / 28 + time * 0.018f;
            var inner = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * scale * 0.92f;
            var outer = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * scale * (index % 4 == 0 ? 1.035f : 1.005f);
            ds.DrawLine(inner, outer, Color.FromArgb(index % 4 == 0 ? (byte)105 : (byte)36,
                gold.R, gold.G, gold.B), index % 4 == 0 ? 1f : 0.55f);
        }
    }

    private static void DrawArchitecture(CanvasDrawingSession ds, float width, float height, Vector2 center,
        float scale, float time, Color amber, Color gold, Color pale)
    {
        DrawArc(ds, center, scale * 1.18f, -156, 74, Color.FromArgb(82, pale.R, pale.G, pale.B), 1f);
        DrawArc(ds, center, scale * 1.18f, -68, 106, Color.FromArgb(205, amber.R, amber.G, amber.B), 2.2f);
        DrawArc(ds, center, scale * 1.18f, 49, 63, Color.FromArgb(72, pale.R, pale.G, pale.B), 1f);
        DrawArc(ds, center, scale * 1.18f, 126, 52, Color.FromArgb(190, gold.R, gold.G, gold.B), 1.8f);
        DrawArc(ds, center, scale * 1.25f, -90 + time * 9, 34, Color.FromArgb(225, amber.R, amber.G, amber.B), 3f);
        DrawArc(ds, center, scale * 1.31f, 165 - time * 5, 22, Color.FromArgb(155, pale.R, pale.G, pale.B), 1.25f);

        for (var index = 0; index < 72; index++)
        {
            var angle = index * MathF.Tau / 72 + time * 0.012f;
            var major = index % 9 == 0;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            ds.DrawLine(center + direction * scale * (major ? 1.11f : 1.15f),
                center + direction * scale * 1.18f,
                Color.FromArgb(major ? (byte)168 : (byte)50, pale.R, pale.G, pale.B),
                major ? 1.15f : 0.55f);
        }

        var left = MathF.Max(26, center.X - scale * 1.52f);
        var right = MathF.Min(width - 26, center.X + scale * 1.52f);
        ds.DrawLine(left, center.Y - scale * 0.63f, left + 58, center.Y - scale * 0.63f,
            Color.FromArgb(100, amber.R, amber.G, amber.B), 1f);
        ds.DrawLine(right - 58, center.Y + scale * 0.64f, right, center.Y + scale * 0.64f,
            Color.FromArgb(100, amber.R, amber.G, amber.B), 1f);
        ds.FillCircle(new Vector2(left, center.Y - scale * 0.63f), 2.5f, gold);
        ds.FillCircle(new Vector2(right, center.Y + scale * 0.64f), 2.5f, gold);
    }

    private static void DrawIndexingField(CanvasDrawingSession ds, Vector2 center, float scale, float time,
        Color amber, Color gold)
    {
        for (var index = 0; index < 56; index++)
        {
            var phase = index * 2.39996f;
            var radius = scale * (0.18f + (index % 11) * 0.067f);
            var position = center + Direction(phase * 57.2958f + time * (4 + index % 3)) * radius;
            var pulse = 0.5f + 0.5f * MathF.Sin(time * 2f + phase);
            ds.FillCircle(position, 0.8f + pulse * 1.6f,
                Color.FromArgb((byte)(50 + pulse * 130), amber.R, amber.G, amber.B));
        }
        DrawArc(ds, center, scale * 0.34f, -90 + time * 45, 132,
            Color.FromArgb(220, gold.R, gold.G, gold.B), 2.2f);
    }

    private static void DrawMappingProgress(CanvasDrawingSession ds, Vector2 center, float scale, float time,
        FileGraphProgress progress, Color amber, Color gold, Color pale)
    {
        var completion = (float)Math.Clamp(progress.EstimatedCompletion, 0, 1);
        var percent = Math.Clamp((int)Math.Round(completion * 100), 0, 100);
        var radius = MathF.Max(42f, scale * 0.13f);
        DrawArc(ds, center, radius, -90, 360,
            Color.FromArgb(42, amber.R, amber.G, amber.B), 1.1f);
        if (completion > 0.001f)
            DrawArc(ds, center, radius, -90, MathF.Max(3f, completion * 360f),
                Color.FromArgb(235, gold.R, gold.G, gold.B), 2.2f);
        else
            DrawArc(ds, center, radius, -90 + time * 70f, 48,
                Color.FromArgb(225, gold.R, gold.G, gold.B), 2.2f);

        DrawHudText(ds, progress.IsComplete ? "MAPPED" : $"EST {percent:00}%",
            center.X - 66, center.Y + radius + 10, 132, 17, gold, 8f, CanvasHorizontalAlignment.Center);
        DrawHudText(ds,
            $"{progress.MappedFileCount:N0} FILES  //  {progress.MappedDirectoryCount:N0} DIRS",
            center.X - 130, center.Y + radius + 27, 260, 16,
            Color.FromArgb(190, pale.R, pale.G, pale.B), 6.5f, CanvasHorizontalAlignment.Center);
        if (!progress.IsComplete && progress.PendingDirectoryCount > 0)
            DrawHudText(ds, $"FRONTIER {progress.PendingDirectoryCount:N0}  //  LIVE MESH ASSEMBLY",
                center.X - 140, center.Y + radius + 42, 280, 15,
                Color.FromArgb(150, amber.R, amber.G, amber.B), 5.8f, CanvasHorizontalAlignment.Center);
    }

    private static void DrawNucleus(CanvasDrawingSession ds, Vector2 center, Color pale, Color stateAccent)
    {
        const float diamond = 11f;
        ds.DrawLine(center + new Vector2(0, -diamond), center + new Vector2(diamond, 0), pale, 1.2f);
        ds.DrawLine(center + new Vector2(diamond, 0), center + new Vector2(0, diamond), pale, 1.2f);
        ds.DrawLine(center + new Vector2(0, diamond), center + new Vector2(-diamond, 0), pale, 1.2f);
        ds.DrawLine(center + new Vector2(-diamond, 0), center + new Vector2(0, -diamond), pale, 1.2f);
        ds.FillCircle(center, 3f, stateAccent);
    }

    private void DrawTelemetry(CanvasDrawingSession ds, float width, float height, FileGraphSnapshot snapshot,
        FileGraphProgress progress, Color amber, Color pale, Color muted)
    {
        var rootCount = _indexing ? progress.RootCount : snapshot.RootCount;
        var fileCount = _indexing ? progress.MappedFileCount : snapshot.FileCount;
        var directoryCount = _indexing ? progress.MappedDirectoryCount : snapshot.DirectoryCount;
        var nodeCount = _indexing ? progress.MappedNodeCount : snapshot.Nodes.Count;
        DrawHudText(ds, "FILESYSTEM FIELD", 40, height * 0.34f, 165, 16, amber, 8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"ROOTS {rootCount:00}", 40, height * 0.38f, 165, 16, pale, 8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"FILES {fileCount:N0}", 40, height * 0.42f, 165, 16, pale, 8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"DIRS  {directoryCount:N0}", 40, height * 0.46f, 165, 16, pale, 8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"CPU {_cpu:00}% / MEM {_memory:00}%", 40, height * 0.50f, 165, 16, muted, 7f, CanvasHorizontalAlignment.Left);

        DrawHudText(ds, "INDEX MATRIX", width - 205, height * 0.34f, 165, 16, amber, 8f, CanvasHorizontalAlignment.Right);
        var mappingPercent = Math.Clamp((int)Math.Round(progress.EstimatedCompletion * 100), 0, 100);
        DrawHudText(ds, _indexing ? $"MAPPING EST {mappingPercent:00}%" : _network ? "LIVE / NETWORKED" : "LIVE / LOCAL", width - 205,
            height * 0.38f, 165, 16, pale, 8f, CanvasHorizontalAlignment.Right);
        DrawHudText(ds, $"NODES {nodeCount:N0}", width - 205, height * 0.42f, 165, 16, pale, 8f, CanvasHorizontalAlignment.Right);
        DrawHudText(ds, $"RELATIONS {Math.Max(0, nodeCount - rootCount):N0}", width - 205,
            height * 0.46f, 165, 16, pale, 8f, CanvasHorizontalAlignment.Right);
        var visibleNodeCount = GetRenderBudget(_zoom, nodeCount);
        DrawHudText(ds, _indexing
                ? $"FRONTIER {progress.PendingDirectoryCount:N0} / VISIBLE {visibleNodeCount:N0}"
                : snapshot.IsTruncated
                    ? $"LIMITED {visibleNodeCount:N0} / {nodeCount:N0}"
                    : $"VISIBLE {visibleNodeCount:N0} / {nodeCount:N0}", width - 205,
            height * 0.50f, 165, 16, muted, 7f, CanvasHorizontalAlignment.Right);
    }

    private static void DrawHudText(CanvasDrawingSession ds, string text, float x, float y, float width,
        float height, Color color, float fontSize, CanvasHorizontalAlignment alignment)
    {
        var format = GetTextFormat(fontSize, alignment);
        ds.DrawText(text, x, y, width, height, color, format);
    }

    private static void DrawArc(CanvasDrawingSession ds, Vector2 center, float radius, float startDegrees,
        float sweepDegrees, Color color, float width)
    {
        if (radius <= 0 || MathF.Abs(sweepDegrees) < 0.01f) return;
        var segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(sweepDegrees) / 6f), 2, 72);
        var previous = center + Direction(startDegrees) * radius;
        for (var segment = 1; segment <= segments; segment++)
        {
            var point = center + Direction(startDegrees + sweepDegrees * segment / segments) * radius;
            ds.DrawLine(previous, point, color, width);
            previous = point;
        }
    }

    private static CanvasTextFormat GetTextFormat(float fontSize, CanvasHorizontalAlignment alignment)
    {
        var normalized = MathF.Max(7f, fontSize);
        var key = ((int)MathF.Round(normalized * 10), alignment);
        lock (TextFormatGate)
        {
            if (TextFormats.TryGetValue(key, out var format)) return format;
            format = new CanvasTextFormat
            {
                FontFamily = "ms-appx:///Assets/Fonts/Orbitron-Medium.ttf",
                FontSize = normalized,
                HorizontalAlignment = alignment,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            TextFormats[key] = format;
            return format;
        }
    }

    private static Vector2 Direction(float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        return new Vector2(MathF.Cos(radians), MathF.Sin(radians));
    }

    private static float HashUnit(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var character in value)
            {
                hash ^= char.ToUpperInvariant(character);
                hash *= 16777619;
            }
            return (hash & 0x00FFFFFF) / 16777215f;
        }
    }

    private static Vector3 HashDirection(string value)
    {
        var a = HashUnit(value);
        var b = HashUnit(value + "|Y");
        var z = 2f * b - 1f;
        var radius = MathF.Sqrt(MathF.Max(0.001f, 1f - z * z));
        var angle = a * MathF.Tau;
        return new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, z);
    }

    private static bool IsDescendantOf(Microsoft.UI.Xaml.DependencyObject source,
        Microsoft.UI.Xaml.DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024d:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024d * 1024):0.0} MB";
        return $"{bytes / (1024d * 1024 * 1024):0.00} GB";
    }

    private Color StateAccent(byte alpha) => _state switch
    {
        BrainState.Warning => Color.FromArgb(alpha, 255, 70, 54),
        BrainState.AwaitingApproval => Color.FromArgb(alpha, 255, 205, 73),
        BrainState.Completed => Color.FromArgb(alpha, 0, 224, 166),
        BrainState.Offline => Color.FromArgb(alpha, 142, 118, 76),
        _ => Color.FromArgb(alpha, 255, 174, 35)
    };
}
