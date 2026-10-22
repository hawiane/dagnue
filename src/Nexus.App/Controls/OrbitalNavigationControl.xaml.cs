using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed class OrbitalNavigationRequestedEventArgs(string route) : EventArgs
{
    public string Route { get; } = route;
}

public sealed class OrbitalRotationChangedEventArgs(double degrees) : EventArgs
{
    public double Degrees { get; } = degrees;
}

public sealed partial class OrbitalNavigationControl : UserControl
{
    private readonly List<Button> _items = [];
    private readonly DispatcherTimer _inertiaTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _visualTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private const double InitialOrbitDegrees = -81;
    // Bottom-center is a divider between two trapezoids. Place FIND nine degrees
    // to the right so its label occupies the middle of an actual navigation cell.
    private static readonly double[] NavigationAngles = [-81, -63, -45, -27, -99, -117, 81, 99];
    private double _orbitOffset = InitialOrbitDegrees * Math.PI / 180;
    private double _velocity;
    private double _lastPointerY;
    private double _signalPhase;
    private double _instrumentWidth;
    private double _instrumentHeight;
    private bool _dragging;
    private bool _dragged;
    private bool _animationPaused;
    private uint _pointerId;

    public event EventHandler<OrbitalNavigationRequestedEventArgs>? NavigationRequested;
    public event EventHandler<OrbitalRotationChangedEventArgs>? RotationChanged;

    public OrbitalNavigationControl()
    {
        InitializeComponent();
        _items.AddRange([CoreOrbitButton, ScheduleOrbitButton, TasksOrbitButton, ActivityOrbitButton,
            MemoryOrbitButton, SettingsOrbitButton, FindOrbitButton, AppsOrbitButton]);
        SizeChanged += (_, _) => ArrangeOrbit();
        Loaded += (_, _) =>
        {
            ArrangeOrbit();
            if (!_animationPaused) _visualTimer.Start();
        };
        Unloaded += (_, _) => { _visualTimer.Stop(); _inertiaTimer.Stop(); };
        _inertiaTimer.Tick += InertiaTimer_Tick;
        _visualTimer.Tick += (_, _) =>
        {
            _signalPhase = (_signalPhase + 0.34) % 360;
            SignalRotation.Angle = _signalPhase;
        };
        AddHandler(PointerPressedEvent, new PointerEventHandler(Orbit_PointerPressed), true);
        AddHandler(PointerMovedEvent, new PointerEventHandler(Orbit_PointerMoved), true);
        AddHandler(PointerReleasedEvent, new PointerEventHandler(Orbit_PointerReleased), true);
        AddHandler(PointerCanceledEvent, new PointerEventHandler(Orbit_PointerReleased), true);
        AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Orbit_PointerWheelChanged), true);
        SetActiveRoute("core");
    }

    public void SetAnimationPaused(bool paused)
    {
        _animationPaused = paused;
        if (paused)
        {
            _visualTimer.Stop();
            _inertiaTimer.Stop();
            return;
        }

        if (IsLoaded) _visualTimer.Start();
    }

    public void UpdateTelemetry(double cpu, double memory, double disk, string power, bool network, bool codex,
        int queue, int approvals, DateTimeOffset now)
    {
        OuterCpuText.Text = $"CPU  {cpu:00}%";
        OuterMemoryText.Text = $"MEM  {memory:00}%";
        OuterDiskText.Text = $"DISK {disk:00}%";
        OuterPowerText.Text = power.Contains("plugged", StringComparison.OrdinalIgnoreCase) ? "PWR PLUGGED" : "PWR BATTERY";
        OuterCodexText.Text = codex ? "CODEX LINKED" : "CODEX OFF";
        OuterNetworkText.Text = network ? "NET ONLINE" : "NET OFFLINE";
        OuterQueueText.Text = $"Q {queue:00}  /  A {approvals:00}";
        OuterClockText.Text = now.ToString("HH:mm  /  dd.MM");
    }

    public void SetActiveRoute(string route)
    {
        foreach (var item in _items)
        {
            var active = string.Equals(item.Tag as string, route, StringComparison.OrdinalIgnoreCase);
            item.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            item.BorderBrush = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
            item.Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 252, 255));
        }
    }

    private void Orbit_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        _inertiaTimer.Stop();
        _pointerId = point.PointerId;
        _lastPointerY = point.Position.Y;
        _velocity = 0;
        _dragging = true;
        _dragged = false;
        CapturePointer(e.Pointer);
    }

    private void Orbit_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _pointerId) return;
        var y = e.GetCurrentPoint(this).Position.Y;
        var delta = y - _lastPointerY;
        _lastPointerY = y;
        if (Math.Abs(delta) > 1.2) _dragged = true;
        _velocity = delta * 0.0044;
        _orbitOffset += _velocity;
        ArrangeOrbit();
        e.Handled = true;
    }

    private void Orbit_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || e.Pointer.PointerId != _pointerId) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        if (Math.Abs(_velocity) > 0.002) _inertiaTimer.Start();
    }

    private void Orbit_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _orbitOffset += delta > 0 ? -0.22 : 0.22;
        _velocity = delta > 0 ? -0.016 : 0.016;
        _inertiaTimer.Start();
        ArrangeOrbit();
        e.Handled = true;
    }

    private void InertiaTimer_Tick(object? sender, object e)
    {
        _orbitOffset += _velocity;
        _velocity *= 0.925;
        if (Math.Abs(_velocity) < 0.00035) _inertiaTimer.Stop();
        ArrangeOrbit();
    }

    private void ArrangeOrbit()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var centerX = ActualWidth / 2;
        var centerY = ActualHeight / 2;
        var diameter = Math.Min(ActualWidth, ActualHeight);
        var radiusX = diameter * 0.354;
        var radiusY = radiusX;
        var outerRadius = diameter * 0.468;

        PositionTelemetryArc(centerX, centerY, outerRadius);

        PositionEllipse(CellOrbit, centerX, centerY, radiusX, radiusY);
        PositionEllipse(SignalOrbit, centerX, centerY, radiusX + 15, radiusY + 15);
        PositionEllipse(MeasureOrbit, centerX, centerY, radiusX + 36, radiusY + 36);
        PositionEllipse(OuterOrbit, centerX, centerY, radiusX + 60, radiusY + 60);
        PositionEllipse(DragTrack, centerX, centerY, radiusX, radiusY);
        TopGuide.X1 = centerX - 72; TopGuide.X2 = centerX + 72; TopGuide.Y1 = TopGuide.Y2 = centerY - radiusY - 60;
        BottomGuide.X1 = centerX - 72; BottomGuide.X2 = centerX + 72; BottomGuide.Y1 = BottomGuide.Y2 = centerY + radiusY + 60;

        if (Math.Abs(_instrumentWidth - ActualWidth) > 1 || Math.Abs(_instrumentHeight - ActualHeight) > 1)
        {
            _instrumentWidth = ActualWidth;
            _instrumentHeight = ActualHeight;
            BuildInstrumentation(centerX, centerY, radiusX, radiusY);
        }

        for (var index = 0; index < _items.Count; index++)
        {
            var item = _items[index];
            var rotationDelta = _orbitOffset - InitialOrbitDegrees * Math.PI / 180;
            var angle = NavigationAngles[index] * Math.PI / 180 + rotationDelta;
            var tangentRotation = angle * 180 / Math.PI + 90;
            tangentRotation = ((tangentRotation + 180) % 360 + 360) % 360 - 180;
            if (tangentRotation > 90)
            {
                tangentRotation -= 180;
            }
            else if (tangentRotation < -90)
            {
                tangentRotation += 180;
            }
            // The decorative trapezoids are drawn just inside the nominal cell
            // orbit. Position the interactive controls on that same track so the
            // label and hover treatment sit inside the visible box. FIND begins at
            // the bottom of the ring, where the previous mismatch was most visible.
            var cellRadiusX = radiusX - 7;
            var cellRadiusY = radiusY - 7;
            if (ReferenceEquals(item, FindOrbitButton) || ReferenceEquals(item, AppsOrbitButton))
            {
                cellRadiusX -= 5;
                cellRadiusY -= 5;
            }
            var x = centerX + cellRadiusX * Math.Cos(angle);
            var y = centerY + cellRadiusY * Math.Sin(angle);
            var depth = 0.74 + 0.26 * ((Math.Cos(angle) + 1) / 2);
            var opacity = Math.Clamp(depth, 0.72, 1);
            var scale = 0.80 + 0.20 * depth;

            item.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(item, x - item.DesiredSize.Width / 2);
            Canvas.SetTop(item, y - item.DesiredSize.Height / 2);
            item.Opacity = opacity;
            item.IsHitTestVisible = opacity > 0.16;
            item.RenderTransformOrigin = new Point(0.5, 0.5);
            item.RenderTransform = new CompositeTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                Rotation = tangentRotation
            };
            Canvas.SetZIndex(item, (int)(depth * 100));
        }
        RotationChanged?.Invoke(this, new OrbitalRotationChangedEventArgs(_orbitOffset * 180 / Math.PI - InitialOrbitDegrees));
    }

    private void PositionTelemetryArc(double centerX, double centerY, double radius)
    {
        var leftRows = new[] { LeftTelemetryTitle, OuterCpuText, OuterMemoryText, OuterDiskText };
        var leftOffsets = new[] { -120d, -88d, -56d, -24d };
        for (var i = 0; i < leftRows.Length; i++)
        {
            var row = leftRows[i];
            var dy = leftOffsets[i];
            var edge = Math.Sqrt(Math.Max(0, radius * radius - dy * dy));
            Canvas.SetLeft(row, Math.Max(4, centerX - edge - row.Width - 8));
            Canvas.SetTop(row, centerY + dy - row.Height / 2);
        }

        var rightRows = new[]
        {
            RightTelemetryTitle, OuterPowerText, OuterNetworkText,
            OuterCodexText, OuterQueueText, OuterClockText
        };
        var rightOffsets = new[] { -118d, -91d, -64d, -37d, -10d, 17d };
        for (var i = 0; i < rightRows.Length; i++)
        {
            var row = rightRows[i];
            var dy = rightOffsets[i];
            var edge = Math.Sqrt(Math.Max(0, radius * radius - dy * dy));
            Canvas.SetLeft(row, Math.Min(ActualWidth - row.Width - 4, centerX + edge + 8));
            Canvas.SetTop(row, centerY + dy - row.Height / 2);
        }
    }

    private void BuildInstrumentation(double centerX, double centerY, double radiusX, double radiusY)
    {
        TickCanvas.Children.Clear();
        DecorCellCanvas.Children.Clear();
        TickCanvas.Width = DecorCellCanvas.Width = ActualWidth;
        TickCanvas.Height = DecorCellCanvas.Height = ActualHeight;

        for (var index = 0; index < 96; index++)
        {
            var angle = index * Math.PI * 2 / 96;
            var major = index % 8 == 0;
            var innerX = radiusX + (major ? 42 : 49);
            var innerY = radiusY + (major ? 42 : 49);
            var outerX = radiusX + 58;
            var outerY = radiusY + 58;
            TickCanvas.Children.Add(new Line
            {
                X1 = centerX + innerX * Math.Cos(angle),
                Y1 = centerY + innerY * Math.Sin(angle),
                X2 = centerX + outerX * Math.Cos(angle),
                Y2 = centerY + outerY * Math.Sin(angle),
                Stroke = new SolidColorBrush(Color.FromArgb(major ? (byte)205 : (byte)88, 195, 242, 250)),
                StrokeThickness = major ? 1.6 : 0.8
            });
        }

        for (var index = 0; index < 20; index++)
        {
            var angle = index * Math.PI * 2 / 20 + 0.08;
            var cell = new Border
            {
                Width = index % 4 == 0 ? 62 : 48,
                Height = 27,
                Background = new SolidColorBrush(Color.FromArgb(index % 4 == 0 ? (byte)40 : (byte)22, 0, 185, 212)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(index % 4 == 0 ? (byte)185 : (byte)115, 208, 241, 247)),
                BorderThickness = new Thickness(1.2),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = angle * 180 / Math.PI + 90 }
            };
            var x = centerX + (radiusX - 5) * Math.Cos(angle);
            var y = centerY + (radiusY - 5) * Math.Sin(angle);
            Canvas.SetLeft(cell, x - cell.Width / 2);
            Canvas.SetTop(cell, y - cell.Height / 2);
            DecorCellCanvas.Children.Add(cell);
        }
    }

    private static void PositionEllipse(FrameworkElement ellipse, double centerX, double centerY, double radiusX, double radiusY)
    {
        ellipse.Width = radiusX * 2;
        ellipse.Height = radiusY * 2;
        Canvas.SetLeft(ellipse, centerX - radiusX);
        Canvas.SetTop(ellipse, centerY - radiusY);
    }

    private static double SmoothStep(double edge0, double edge1, double value)
    {
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private void OrbitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dragged || sender is not Button { Tag: string route }) return;
        SetActiveRoute(route);
        NavigationRequested?.Invoke(this, new OrbitalNavigationRequestedEventArgs(route));
    }

    private void OrbitButton_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            VisualStateManager.GoToState(button, "PointerOver", true);
            SetCellHoverAppearance(button, true);
        }
    }

    private void OrbitButton_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button button)
        {
            VisualStateManager.GoToState(button, "Normal", true);
            SetCellHoverAppearance(button, false);
        }
    }

    private static void SetCellHoverAppearance(Button button, bool hovered)
    {
        button.ApplyTemplate();

        if (FindNamedDescendant<Grid>(button, "Cell") is { RenderTransform: CompositeTransform transform })
        {
            transform.ScaleX = hovered ? 1.04 : 1;
            transform.ScaleY = hovered ? 1.04 : 1;
        }

        if (FindNamedDescendant<Microsoft.UI.Xaml.Shapes.Path>(button, "HoverAura") is { } aura)
            aura.Opacity = hovered ? 0.72 : 0;
        if (FindNamedDescendant<Microsoft.UI.Xaml.Shapes.Path>(button, "HoverPlate") is { } plate)
            plate.Opacity = hovered ? 0.82 : 0;
        if (FindNamedDescendant<Microsoft.UI.Xaml.Shapes.Path>(button, "HoverTrace") is { } trace)
            trace.Opacity = hovered ? 1 : 0;
    }

    private static T? FindNamedDescendant<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && element.Name == name) return element;
            if (FindNamedDescendant<T>(child, name) is { } match) return match;
        }

        return null;
    }
}
