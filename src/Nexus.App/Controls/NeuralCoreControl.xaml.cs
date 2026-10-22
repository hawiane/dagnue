using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;
using Nexus.Core.Models;
using Nexus.Core.SystemServices;
using Windows.Foundation;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed partial class NeuralCoreControl : UserControl
{
    public enum OptimizationVisualState { Idle, Working, Success, Failed }

    private BrainState _state = BrainState.Offline;
    private float _cpuPercent;
    private float _memoryPercent;
    private float _diskPercent;
    private bool _networkAvailable;
    private int _activeTasks;
    private int _appTaskCount;
    private int _userTaskCount;
    private int _aiReadiness;
    private int _generalAiBaseline = 45;
    private int _capabilityCount;
    private int _capabilityDomainCount;
    private int _reasoningReadiness;
    private int _vaultReadiness;
    private int _domainReadiness;
    private int _systemReadiness;
    private Rect _cpuHitTarget;
    private Rect _memoryHitTarget;
    private Rect _diskHitTarget;
    private Rect _reservoirHitTarget;
    private GlbWireframeModel? _portraitModel;
    private bool _portraitLoadStarted;
    private bool _portraitWebActive;
    private bool _animationPaused;
    private bool _rotatingPortrait;
    private uint _portraitPointerId;
    private float _portraitLastPointerX;
    private float _portraitYaw = 0.28f;
    private readonly Dictionary<SystemOptimizationKind, OptimizationVisualState> _optimizationStates = new();
    private static readonly object TextFormatGate = new();
    private static readonly Dictionary<(int SizeTenths, CanvasHorizontalAlignment Alignment), CanvasTextFormat> TextFormats = [];
    private static readonly (float X, float Y)[] SignalGlyphOffsets =
    [
        (-1.5f, -0.55f), (-0.5f, -0.55f),
        (-1.5f,  0.45f), (-0.5f,  0.45f),
        ( 0.5f,  0.45f), ( 1.5f,  0.45f),
        ( 0.5f,  1.45f), ( 1.5f,  1.45f)
    ];
    private const int TaskReservoirCapacity = 20;
    public float NavigationRotationDegrees { get; set; }
    public int ActiveNavigationIndex { get; set; }

    public NeuralCoreControl()
    {
        InitializeComponent();
        // The radial HUD is mostly slow ambient motion. Fifteen frames per second
        // keeps that motion fluid while leaving the UI dispatcher and compositor
        // enough headroom for windows, forms, and list scrolling.
        HudCanvas.TargetElapsedTime = TimeSpan.FromMilliseconds(66.667);
        Loaded += NeuralCoreControl_Loaded;
        Unloaded += (_, _) => HudCanvas.RemoveFromVisualTree();
    }

    private async void NeuralCoreControl_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (_portraitLoadStarted) return;
        _portraitLoadStarted = true;
        try
        {
            await PortraitWebView.EnsureCoreWebView2Async();
            PortraitWebView.CoreWebView2.SetVirtualHostNameToFolderMapping("dagnue.local",
                Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
            PortraitWebView.CoreWebView2.WebMessageReceived += PortraitWebView_WebMessageReceived;
        _portraitWebActive = true;
            UpdatePortraitWebViewBounds();
            PortraitWebView.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            PortraitWebView.Source = new Uri("https://dagnue.local/Portrait3D/index.html");
        }
        catch
        {
            await LoadNativePortraitFallbackAsync();
        }
    }

    private async void PortraitWebView_WebMessageReceived(CoreWebView2 sender,
        CoreWebView2WebMessageReceivedEventArgs args)
    {
        var message = args.TryGetWebMessageAsString();
        if (message.Equals("portrait-ready", StringComparison.Ordinal))
        {
            if (_animationPaused) await SetPortraitPausedAsync(true);
            return;
        }
        if (!message.StartsWith("portrait-error", StringComparison.Ordinal)) return;
        _portraitWebActive = false;
        PortraitWebView.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        await LoadNativePortraitFallbackAsync();
    }

    private async Task LoadNativePortraitFallbackAsync()
    {
        try
        {
            var modelPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Models", "portrait-bust.glb");
            _portraitModel = await Task.Run(() => GlbWireframeModel.Load(modelPath));
        }
        catch
        {
            // The procedural operator remains the final installation-safe fallback.
            _portraitModel = null;
        }
    }

    private void InteractionSurface_SizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e) =>
        UpdatePortraitWebViewBounds();

    private void UpdatePortraitWebViewBounds()
    {
        var width = (float)InteractionSurface.ActualWidth;
        var height = (float)InteractionSurface.ActualHeight;
        if (width < 50 || height < 50) return;
        var diameter = MathF.Min(width, height);
        var center = new Vector2(width / 2, height / 2);
        var ringLeft = center.X - diameter * 0.468f;
        var leftX = MathF.Max(58, ringLeft / 2);
        var lowerY = MathF.Min(height - 78, center.Y + diameter * 0.29f);
        var portraitCenter = new Vector2(leftX, lowerY - diameter * 0.035f);
        var scale = Math.Clamp(diameter / 760f, 0.70f, 1.08f);
        PortraitWebView.Width = 200 * scale;
        PortraitWebView.Height = 245 * scale;
        Canvas.SetLeft(PortraitWebView, portraitCenter.X - 100 * scale);
        Canvas.SetTop(PortraitWebView, portraitCenter.Y - 128 * scale);
        if (_portraitWebActive)
            PortraitWebView.Visibility = width >= diameter * 1.18f && height >= 500
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;
    }

    public event EventHandler<SystemOptimizationKind>? OptimizationRequested;

    public void SetOptimizationState(SystemOptimizationKind kind, OptimizationVisualState state)
    {
        _optimizationStates[kind] = state;
        HudCanvas.Invalidate();
    }

    private void InteractionSurface_Tapped(object sender, TappedRoutedEventArgs e)
    {
        var point = e.GetPosition(InteractionSurface);
        SystemOptimizationKind? kind = _cpuHitTarget.Contains(point) ? SystemOptimizationKind.Cpu
            : _memoryHitTarget.Contains(point) ? SystemOptimizationKind.Memory
            : _diskHitTarget.Contains(point) ? SystemOptimizationKind.Disk
            : null;
        if (kind is null || GetOptimizationState(kind.Value) == OptimizationVisualState.Working) return;
        OptimizationRequested?.Invoke(this, kind.Value);
        e.Handled = true;
    }

    private void InteractionSurface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(InteractionSurface);
        if (!_reservoirHitTarget.Contains(point.Position)) return;
        _rotatingPortrait = true;
        _portraitPointerId = e.Pointer.PointerId;
        _portraitLastPointerX = (float)point.Position.X;
        InteractionSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void InteractionSurface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_rotatingPortrait || e.Pointer.PointerId != _portraitPointerId) return;
        var x = (float)e.GetCurrentPoint(InteractionSurface).Position.X;
        _portraitYaw += (x - _portraitLastPointerX) * 0.018f;
        _portraitLastPointerX = x;
        HudCanvas.Invalidate();
        e.Handled = true;
    }

    private void InteractionSurface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_rotatingPortrait || e.Pointer.PointerId != _portraitPointerId) return;
        _rotatingPortrait = false;
        InteractionSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void InteractionSurface_PointerCanceled(object sender, PointerRoutedEventArgs e) =>
        EndPortraitRotation(e.Pointer.PointerId);

    private void InteractionSurface_PointerCaptureLost(object sender, PointerRoutedEventArgs e) =>
        EndPortraitRotation(e.Pointer.PointerId);

    private void EndPortraitRotation(uint pointerId)
    {
        if (_rotatingPortrait && pointerId == _portraitPointerId) _rotatingPortrait = false;
    }

    public bool ReducedMotion { get; set; }

    public void SetAnimationPaused(bool paused)
    {
        _animationPaused = paused;
        HudCanvas.Paused = paused;
        if (_portraitWebActive) _ = SetPortraitPausedAsync(paused);
    }

    private async Task SetPortraitPausedAsync(bool paused)
    {
        try
        {
            await PortraitWebView.ExecuteScriptAsync($"window.dagnueSetPaused?.({paused.ToString().ToLowerInvariant()})");
        }
        catch { }
    }

    public void UpdateTelemetry(SystemSnapshot snapshot)
    {
        _cpuPercent = (float)snapshot.CpuPercent;
        _memoryPercent = (float)snapshot.MemoryPercent;
        _diskPercent = (float)snapshot.DiskPercent;
        _networkAvailable = snapshot.NetworkAvailable;
        _activeTasks = snapshot.ActiveTasks;
        var health = SystemHealthScore.Calculate(snapshot.CpuPercent, snapshot.MemoryPercent, snapshot.DiskPercent);
        CoreMetricText.Text = health.ToString();
        CoreMetricText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(health switch
        {
            >= 70 => Color.FromArgb(255, 0, 229, 190),
            >= 45 => Color.FromArgb(255, 0, 229, 255),
            >= 25 => Color.FromArgb(255, 255, 184, 77),
            _ => Color.FromArgb(255, 255, 72, 82)
        });
        ToolTipService.SetToolTip(CoreMetricText,
            $"Live health score: {health}/100\nCPU {snapshot.CpuPercent:0}% · Memory {snapshot.MemoryPercent:0}% · Disk {snapshot.DiskPercent:0}%\nWeights: CPU 40% · Memory 40% · Disk 20%");
    }

    public void UpdateTaskLoad(int appTasks, int userTasks)
    {
        _appTaskCount = Math.Max(0, appTasks);
        _userTaskCount = Math.Max(0, userTasks);
        HudCanvas.Invalidate();
    }

    public void UpdateAiReadiness(int score, int generalAiBaseline, int capabilityCount, int domainCount,
        int reasoningScore, int vaultScore, int domainScore, int systemScore)
    {
        _aiReadiness = Math.Clamp(score, 0, 100);
        _generalAiBaseline = Math.Clamp(generalAiBaseline, 0, 100);
        _capabilityCount = Math.Max(0, capabilityCount);
        _capabilityDomainCount = Math.Max(0, domainCount);
        _reasoningReadiness = Math.Clamp(reasoningScore, 0, 30);
        _vaultReadiness = Math.Clamp(vaultScore, 0, 25);
        _domainReadiness = Math.Clamp(domainScore, 0, 25);
        _systemReadiness = Math.Clamp(systemScore, 0, 20);
        HudCanvas.Invalidate();
    }

    public BrainState State
    {
        get => _state;
        set
        {
            _state = value;
            StateText.Text = value switch
            {
                BrainState.AwaitingApproval => "APPROVAL REQUIRED",
                BrainState.Understanding => "UNDERSTANDING",
                _ => value.ToString().ToUpperInvariant()
            };
            HudCanvas.TargetElapsedTime = value is BrainState.Understanding or BrainState.Reasoning or
                BrainState.Acting or BrainState.AwaitingApproval
                ? TimeSpan.FromMilliseconds(41.667)
                : TimeSpan.FromMilliseconds(66.667);
        }
    }

    private void HudCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        var width = (float)sender.Size.Width;
        var height = (float)sender.Size.Height;
        if (width < 50 || height < 50) return;

        var center = new Vector2(width / 2, height / 2);
        var diameter = MathF.Min(width, height);
        var time = ReducedMotion ? 0f : (float)args.Timing.TotalTime.TotalSeconds;
        var cyan = StateColor(255);
        var instrumentCyan = Color.FromArgb(255, 0, 218, 255);
        var pale = Color.FromArgb(218, 218, 244, 249);
        var muted = Color.FromArgb(105, 110, 155, 165);

        DrawAuxiliaryInstruments(ds, width, height, center, diameter, time, instrumentCyan, pale, muted);
        DrawOuterArchitecture(ds, center, diameter, time, cyan, pale, muted);
        DrawNavigationBed(ds, center, diameter, time, NavigationRotationDegrees, ActiveNavigationIndex, cyan, pale);
        DrawEnergyCore(ds, center, diameter, time, cyan, pale);
    }

    private void DrawAuxiliaryInstruments(CanvasDrawingSession ds, float width, float height, Vector2 center,
        float d, float time, Color cyan, Color pale, Color muted)
    {
        if (width < d * 1.18f || height < 500) return;

        var ringLeft = center.X - d * 0.468f;
        var ringRight = center.X + d * 0.468f;
        var leftX = MathF.Max(58, ringLeft / 2);
        var rightX = MathF.Min(width - 76, ringRight + (width - ringRight) / 2);
        var upperY = MathF.Max(78, center.Y - d * 0.30f);
        var lowerY = MathF.Min(height - 78, center.Y + d * 0.29f);

        DrawChronoNode(ds, new Vector2(leftX, upperY), d * 0.067f, time, cyan, pale, muted);
        // The readiness array is edge-docked: only its left-facing hemisphere is drawn.
        DrawAiReadinessArray(ds, new Vector2(width - 7, center.Y - d * 0.105f), d, time, cyan, pale, muted);
        // The old link ring lived at the lower-left. Task load now has a more
        // meaningful physical metaphor: a fluid holographic operator on the right.
        var reservoirCenter = new Vector2(leftX, lowerY - d * 0.035f);
        var reservoirScale = Math.Clamp(d / 760f, 0.70f, 1.08f);
        _reservoirHitTarget = new Rect(reservoirCenter.X - 82 * reservoirScale,
            reservoirCenter.Y - 132 * reservoirScale, 164 * reservoirScale, 250 * reservoirScale);
        DrawTaskReservoirHuman(ds, reservoirCenter, d, time, cyan, pale, muted);
        var instrumentY = center.Y + d * 0.195f;
        var sideOffset = Math.Clamp(d * 0.078f, 52f, 68f);
        var cpuCenter = new Vector2(rightX - sideOffset, instrumentY - 5);
        var cpuRadius = Math.Clamp(d * 0.036f, 24f, 32f);
        var memoryHeight = d * 0.205f;
        var diskCenter = new Vector2(rightX + sideOffset, instrumentY);
        var diskHeight = d * 0.154f;
        _cpuHitTarget = new Rect(cpuCenter.X - cpuRadius - 22, cpuCenter.Y - cpuRadius - 35,
            (cpuRadius + 22) * 2, (cpuRadius + 35) * 2);
        _memoryHitTarget = new Rect(rightX - 27, instrumentY - memoryHeight / 2 - 30, 54, memoryHeight + 60);
        _diskHitTarget = new Rect(diskCenter.X - 31, diskCenter.Y - diskHeight / 2 - 30, 62, diskHeight + 60);

        var cpuColor = InstrumentColor(SystemOptimizationKind.Cpu, cyan);
        var memoryColor = InstrumentColor(SystemOptimizationKind.Memory, cyan);
        var diskColor = InstrumentColor(SystemOptimizationKind.Disk, cyan);
        DrawCpuRotor(ds, new Vector2(rightX - sideOffset, instrumentY - 5),
            cpuRadius, _cpuPercent, time, cpuColor, pale, muted);
        DrawSegmentedMeter(ds, new Vector2(rightX, instrumentY), d * 0.205f,
            _memoryPercent, "MEM", memoryColor, pale, muted);
        DrawDiskCrystal(ds, new Vector2(rightX + sideOffset, instrumentY),
            d * 0.154f, _diskPercent, time, diskColor, pale, muted);
        DrawCpuOptimizationEffect(ds, cpuCenter, cpuRadius + 6,
            GetOptimizationState(SystemOptimizationKind.Cpu), time);
        var memoryState = GetOptimizationState(SystemOptimizationKind.Memory);
        if (memoryState != OptimizationVisualState.Idle)
            DrawBarOptimizationEffect(ds,
            [
                new Vector2(rightX, instrumentY - memoryHeight / 2),
                new Vector2(rightX + 15, instrumentY - memoryHeight / 2 + 12),
                new Vector2(rightX + 15, instrumentY + memoryHeight / 2 - 12),
                new Vector2(rightX, instrumentY + memoryHeight / 2),
                new Vector2(rightX - 15, instrumentY + memoryHeight / 2 - 12),
                new Vector2(rightX - 15, instrumentY - memoryHeight / 2 + 12)
            ], memoryState, time);
        var diskState = GetOptimizationState(SystemOptimizationKind.Disk);
        if (diskState != OptimizationVisualState.Idle)
            DrawBarOptimizationEffect(ds,
            [
                new Vector2(diskCenter.X, diskCenter.Y - diskHeight / 2),
                new Vector2(diskCenter.X + 19, diskCenter.Y - diskHeight / 2 + 13),
                new Vector2(diskCenter.X + 19, diskCenter.Y + diskHeight / 2 - 14),
                new Vector2(diskCenter.X, diskCenter.Y + diskHeight / 2),
                new Vector2(diskCenter.X - 19, diskCenter.Y + diskHeight / 2 - 14),
                new Vector2(diskCenter.X - 19, diskCenter.Y - diskHeight / 2 + 13)
            ], diskState, time);
        DrawTaskReservoirReadout(ds, new Vector2(leftX, lowerY + d * 0.125f), d, cyan, pale, muted);
    }

    private void DrawAiReadinessArray(CanvasDrawingSession ds, Vector2 center, float diameter, float time,
        Color cyan, Color pale, Color muted)
    {
        var radius = Math.Clamp(diameter * 0.145f, 94f, 126f);
        var readiness = _aiReadiness / 100f;
        var tier = _aiReadiness switch { < 25 => 0, < 50 => 1, < 75 => 2, _ => 3 };
        var active = tier == 0 ? Color.FromArgb(255, 0, 116, 126) : Color.FromArgb(255, 0, 188, 188);
        var bright = Color.FromArgb(255, 0, 221, 216);
        var pulse = ReducedMotion ? 0.4f : (MathF.Sin(time * (0.8f + tier * .25f)) + 1f) / 2f;
        ds.FillRectangle(center.X - radius - 25, center.Y - radius - 25, radius + 25, (radius + 25) * 2,
            Color.FromArgb((byte)(3 + pulse * 5), active.R, active.G, active.B));
        ds.DrawLine(center.X, center.Y - radius - 21, center.X, center.Y + radius + 21,
            Color.FromArgb(180, bright.R, bright.G, bright.B), 1.2f);

        // Heavy broken outer bands, matching the asymmetrical alien reference array.
        DrawArc(ds, center, radius + 20, 91, 42 + readiness * 24, Color.FromArgb(235, bright.R, bright.G, bright.B), 3.2f);
        DrawArc(ds, center, radius + 20, 143, 31 + tier * 5, Color.FromArgb(145, active.R, active.G, active.B), 1.4f);
        DrawArc(ds, center, radius + 20, 187, 75 + readiness * 18, Color.FromArgb(220, active.R, active.G, active.B), 5.2f);
        var segmentCount = 32 + tier * 4;
        var litSegments = (int)MathF.Round(segmentCount * readiness);
        for (var i = 0; i < segmentCount; i++)
        {
            var start = 96 + i * 168f / segmentCount;
            var color = i < litSegments ? Color.FromArgb(235, active.R, active.G, active.B)
                : Color.FromArgb(32, muted.R, muted.G, muted.B);
            DrawArc(ds, center, radius + 11, start, i % 7 == 0 ? 3.2f : 2.1f, color,
                i % 6 == 0 ? 8.5f : 5.5f);
        }

        var categoryValues = new[]
        {
            _reasoningReadiness / 30f, _vaultReadiness / 25f,
            _domainReadiness / 25f, _systemReadiness / 20f
        };
        var categoryColors = new[] { bright, Color.FromArgb(255, 0, 172, 170),
            Color.FromArgb(255, 0, 135, 137), Color.FromArgb(255, 25, 104, 108) };
        for (var category = 0; category < categoryValues.Length; category++)
        {
            // Each capability owns two fine traces, so the contents are visible as bands rather than a legend.
            for (var trace = 0; trace < 2; trace++)
            {
                var ringRadius = radius - 1 - category * 13f - trace * 4.7f;
                var direction = (category + trace) % 2 == 0 ? 1f : -1f;
                var rotation = ReducedMotion ? 0 : direction * MathF.Sin(time * (.45f + category * .08f)) * 2.5f;
                var start = 92 + category * 7 + trace * 3 + rotation;
                var span = (174 - trace * 7) * categoryValues[category];
                DrawArc(ds, center, ringRadius, start, 178 - trace * 4,
                    Color.FromArgb(25, muted.R, muted.G, muted.B), trace == 0 ? 3.4f : 1.1f);
                DrawArc(ds, center, ringRadius, start, span,
                    Color.FromArgb(trace == 0 ? (byte)205 : (byte)145,
                        categoryColors[category].R, categoryColors[category].G, categoryColors[category].B),
                    trace == 0 ? 2.6f : .9f);
            }
        }

        // Inner packet blocks and a rotating scan line reproduce the reference's live data core.
        for (var i = 0; i < 18 + tier * 3; i++)
        {
            var angle = (94 + i * 9.2f + (ReducedMotion ? 0 : MathF.Sin(time * .55f) * 3f)) * MathF.PI / 180;
            var r = radius * (.26f + (i % 4) * .025f);
            var p = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * r;
            ds.FillRectangle(p.X - 1.5f, p.Y - 1.5f, i % 5 == 0 ? 5f : 2.5f, 2f,
                Color.FromArgb((byte)(70 + (i % 3) * 35), active.R, active.G, active.B));
        }
        var scanAngle = (90 + (ReducedMotion ? 90 : (time * (7 + tier * 2)) % 180)) * MathF.PI / 180;
        var scanEnd = center + new Vector2(MathF.Cos(scanAngle), MathF.Sin(scanAngle)) * (radius * .48f);
        ds.DrawLine(center, scanEnd, Color.FromArgb(100, bright.R, bright.G, bright.B), .8f);
        DrawArc(ds, center, radius * .49f, 90, 180, Color.FromArgb(55, active.R, active.G, active.B), .8f);

        // Compact diagnostic text sits outside the visual core, as in the supplied reference.
        var textX = center.X - radius * .88f;
        var textY = center.Y - 25;
        DrawHudText(ds, $"AI READY {_aiReadiness:00}", textX, textY, 84, 9, bright, 5.4f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"RSN {_reasoningReadiness:00}/30", textX, textY + 10, 84, 8, categoryColors[0], 4.8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"VLT {_vaultReadiness:00}/25", textX, textY + 18, 84, 8, categoryColors[1], 4.8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"DOM {_domainReadiness:00}/25", textX, textY + 26, 84, 8, categoryColors[2], 4.8f, CanvasHorizontalAlignment.Left);
        DrawHudText(ds, $"SYS {_systemReadiness:00}/20", textX, textY + 34, 84, 8, categoryColors[3], 4.8f, CanvasHorizontalAlignment.Left);
    }

    private OptimizationVisualState GetOptimizationState(SystemOptimizationKind kind) =>
        _optimizationStates.GetValueOrDefault(kind, OptimizationVisualState.Idle);

    private Color InstrumentColor(SystemOptimizationKind kind, Color fallback) => GetOptimizationState(kind) switch
    {
        OptimizationVisualState.Working => Color.FromArgb(255, 255, 145, 0),
        OptimizationVisualState.Success => Color.FromArgb(255, 35, 235, 137),
        OptimizationVisualState.Failed => Color.FromArgb(255, 255, 70, 74),
        _ => fallback
    };

    private static void DrawCpuOptimizationEffect(CanvasDrawingSession ds, Vector2 center, float radius,
        OptimizationVisualState state, float time)
    {
        if (state == OptimizationVisualState.Idle) return;
        var pulse = state == OptimizationVisualState.Working
            ? (MathF.Sin(time * 4.8f) + 1f) / 2f
            : 0.62f;
        var color = state switch
        {
            OptimizationVisualState.Working => Color.FromArgb(255, 255, 145, 0),
            OptimizationVisualState.Success => Color.FromArgb(255, 35, 235, 137),
            _ => Color.FromArgb(255, 255, 70, 74)
        };
        ds.DrawCircle(center, radius + 2,
            Color.FromArgb((byte)(30 + pulse * 50), color.R, color.G, color.B), 11f + pulse * 3f);
        ds.DrawCircle(center, radius + 2,
            Color.FromArgb((byte)(105 + pulse * 85), color.R, color.G, color.B), 4f + pulse * 1.5f);
        ds.DrawCircle(center, radius + 2,
            Color.FromArgb((byte)(215 + pulse * 40), color.R, color.G, color.B), 1.6f);
    }

    private static void DrawBarOptimizationEffect(CanvasDrawingSession ds, Vector2[] border,
        OptimizationVisualState state, float time)
    {
        if (state == OptimizationVisualState.Idle) return;
        var pulse = state == OptimizationVisualState.Working
            ? (MathF.Sin(time * 4.8f) + 1f) / 2f
            : 0.62f;
        var color = state switch
        {
            OptimizationVisualState.Working => Color.FromArgb(255, 255, 145, 0),
            OptimizationVisualState.Success => Color.FromArgb(255, 35, 235, 137),
            _ => Color.FromArgb(255, 255, 70, 74)
        };
        DrawClosedBorder(ds, border,
            Color.FromArgb((byte)(28 + pulse * 48), color.R, color.G, color.B), 10f + pulse * 3f);
        DrawClosedBorder(ds, border,
            Color.FromArgb((byte)(105 + pulse * 85), color.R, color.G, color.B), 4f + pulse * 1.5f);
        DrawClosedBorder(ds, border,
            Color.FromArgb((byte)(215 + pulse * 40), color.R, color.G, color.B), 1.6f);
    }

    private static void DrawClosedBorder(CanvasDrawingSession ds, Vector2[] border, Color color, float width)
    {
        for (var index = 0; index < border.Length; index++)
            ds.DrawLine(border[index], border[(index + 1) % border.Length], color, width);
    }


    private void DrawTaskReservoirHuman(CanvasDrawingSession ds, Vector2 center, float diameter, float time,
        Color cyan, Color pale, Color muted)
    {
        if (_portraitWebActive) return;
        if (_portraitModel is not null)
        {
            DrawPortraitWireframe(ds, _portraitModel, center, diameter, time, cyan, pale);
            return;
        }

        var scale = Math.Clamp(diameter / 760f, 0.70f, 1.08f);
        var yaw = ReducedMotion ? 0.32f : time * 0.28f;
        var perspective = 0.94f + MathF.Cos(yaw) * 0.06f;
        var lateral = MathF.Sin(yaw) * 2.5f * scale;
        var totalTasks = _appTaskCount + _userTaskCount;
        var load = Math.Clamp(totalTasks / (float)TaskReservoirCapacity, 0f, 1f);
        var full = totalTasks >= TaskReservoirCapacity;
        var fluid = full ? Color.FromArgb(255, 255, 48, 58) : Color.FromArgb(255, 0, 229, 255);
        var glow = full ? Color.FromArgb(255, 255, 72, 82) : cyan;

        Vector2 P(float x, float y) => center + new Vector2((x * perspective + lateral) * scale, y * scale);

        // Curved anatomical silhouette: relaxed shoulders, articulated arms and hands,
        // a defined waist and hips, and naturally separated legs and feet.
        using var bodyBuilder = new CanvasPathBuilder(ds);
        bodyBuilder.BeginFigure(P(-8, -84));
        bodyBuilder.AddCubicBezier(P(-16, -83), P(-25, -80), P(-31, -72));
        bodyBuilder.AddCubicBezier(P(-36, -63), P(-39, -49), P(-41, -34));
        bodyBuilder.AddCubicBezier(P(-42, -23), P(-47, -8), P(-49, 5));
        bodyBuilder.AddCubicBezier(P(-50, 10), P(-48, 15), P(-44, 16));
        bodyBuilder.AddCubicBezier(P(-40, 17), P(-37, 13), P(-37, 8));
        bodyBuilder.AddCubicBezier(P(-36, -4), P(-32, -19), P(-30, -30));
        bodyBuilder.AddCubicBezier(P(-27, -39), P(-24, -45), P(-20, -48));
        bodyBuilder.AddCubicBezier(P(-20, -28), P(-16, -7), P(-17, 11));
        bodyBuilder.AddCubicBezier(P(-22, 18), P(-24, 27), P(-22, 36));
        bodyBuilder.AddCubicBezier(P(-22, 53), P(-20, 72), P(-17, 86));
        bodyBuilder.AddCubicBezier(P(-21, 90), P(-21, 96), P(-12, 97));
        bodyBuilder.AddCubicBezier(P(-7, 97), P(-5, 95), P(-5, 91));
        bodyBuilder.AddCubicBezier(P(-5, 72), P(-3, 53), P(0, 37));
        bodyBuilder.AddCubicBezier(P(3, 53), P(5, 72), P(5, 91));
        bodyBuilder.AddCubicBezier(P(5, 95), P(7, 97), P(12, 97));
        bodyBuilder.AddCubicBezier(P(21, 96), P(21, 90), P(17, 86));
        bodyBuilder.AddCubicBezier(P(20, 72), P(22, 53), P(22, 36));
        bodyBuilder.AddCubicBezier(P(24, 27), P(22, 18), P(17, 11));
        bodyBuilder.AddCubicBezier(P(16, -7), P(20, -28), P(20, -48));
        bodyBuilder.AddCubicBezier(P(24, -45), P(27, -39), P(30, -30));
        bodyBuilder.AddCubicBezier(P(32, -19), P(36, -4), P(37, 8));
        bodyBuilder.AddCubicBezier(P(37, 13), P(40, 17), P(44, 16));
        bodyBuilder.AddCubicBezier(P(48, 15), P(50, 10), P(49, 5));
        bodyBuilder.AddCubicBezier(P(47, -8), P(42, -23), P(41, -34));
        bodyBuilder.AddCubicBezier(P(39, -49), P(36, -63), P(31, -72));
        bodyBuilder.AddCubicBezier(P(25, -80), P(16, -83), P(8, -84));
        bodyBuilder.EndFigure(CanvasFigureLoop.Closed);
        using var body = CanvasGeometry.CreatePath(bodyBuilder);

        using var headBuilder = new CanvasPathBuilder(ds);
        headBuilder.BeginFigure(P(0, -124));
        headBuilder.AddCubicBezier(P(-10, -124), P(-16, -117), P(-16, -106));
        headBuilder.AddCubicBezier(P(-15, -96), P(-10, -89), P(-5, -86));
        headBuilder.AddCubicBezier(P(-3, -84), P(-2, -82), P(0, -81));
        headBuilder.AddCubicBezier(P(2, -82), P(3, -84), P(5, -86));
        headBuilder.AddCubicBezier(P(10, -89), P(15, -96), P(16, -106));
        headBuilder.AddCubicBezier(P(16, -117), P(10, -124), P(0, -124));
        headBuilder.EndFigure(CanvasFigureLoop.Closed);
        using var head = CanvasGeometry.CreatePath(headBuilder);
        var headCenter = P(0, -104);

        // The hologram stays dimensional even with an empty reservoir. The task
        // fluid adds brightness and color rather than defining the silhouette.
        ds.FillCircle(P(0, -25), 52f * scale, Color.FromArgb(9, glow.R, glow.G, glow.B));
        using (ds.CreateLayer(1f, body))
            ds.FillRectangle(center.X - 62 * scale, P(0, -126).Y, 124 * scale, 226 * scale,
                Color.FromArgb(42, glow.R, glow.G, glow.B));
        using (ds.CreateLayer(1f, head))
            ds.FillRectangle(center.X - 24 * scale, P(0, -126).Y, 48 * scale, 48 * scale,
                Color.FromArgb(52, glow.R, glow.G, glow.B));

        var top = P(0, -124).Y;
        var bottom = P(0, 98).Y;
        var fluidTop = bottom - (bottom - top) * load;
        using (ds.CreateLayer(1f, body))
        {
            ds.FillRectangle(center.X - 62 * scale, fluidTop, 124 * scale, bottom - fluidTop,
                Color.FromArgb(full ? (byte)122 : (byte)82, fluid.R, fluid.G, fluid.B));
            for (var wave = 0; wave < 5; wave++)
            {
                var y = fluidTop + wave * 4.4f * scale;
                ds.DrawLine(center.X - 62 * scale, y,
                    center.X + 62 * scale, y + MathF.Sin(time * 2.2f + wave) * 2f,
                    Color.FromArgb((byte)(185 - wave * 22), fluid.R, fluid.G, fluid.B), 1.1f);
            }
        }
        using (ds.CreateLayer(1f, head))
        {
            ds.FillRectangle(center.X - 22 * scale, fluidTop, 44 * scale, bottom - fluidTop,
                Color.FromArgb(full ? (byte)122 : (byte)82, fluid.R, fluid.G, fluid.B));
        }

        ds.DrawGeometry(body, Color.FromArgb(16, glow.R, glow.G, glow.B), 18f * scale);
        ds.DrawGeometry(body, Color.FromArgb(42, glow.R, glow.G, glow.B), 8f * scale);
        ds.DrawGeometry(body, Color.FromArgb(242, glow.R, glow.G, glow.B), 1.35f * scale);
        ds.DrawGeometry(head, Color.FromArgb(18, glow.R, glow.G, glow.B), 16f * scale);
        ds.DrawGeometry(head, Color.FromArgb(55, glow.R, glow.G, glow.B), 7f * scale);
        ds.DrawGeometry(head, Color.FromArgb(245, glow.R, glow.G, glow.B), 1.3f * scale);

        // Dense anatomical wireframe. Small triangles read as a body surface rather
        // than the oversized polygon cage used by the previous design.
        var mesh = Color.FromArgb(128, glow.R, glow.G, glow.B);
        var nodes = new[]
        {
            P(0,-84), P(-15,-79), P(15,-79), P(-28,-70), P(28,-70),
            P(-22,-55), P(0,-61), P(22,-55), P(-35,-43), P(35,-43),
            P(-39,-26), P(-22,-30), P(0,-36), P(22,-30), P(39,-26),
            P(-43,-7), P(-18,-10), P(0,-15), P(18,-10), P(43,-7),
            P(-44,11), P(-16,10), P(0,7), P(16,10), P(44,11),
            P(-21,28), P(-9,28), P(0,35), P(9,28), P(21,28),
            P(-20,48), P(-7,51), P(7,51), P(20,48),
            P(-18,69), P(-6,72), P(6,72), P(18,69),
            P(-16,88), P(-5,91), P(5,91), P(16,88)
        };
        var links = new (int A, int B)[]
        {
            (0,1),(0,2),(1,3),(1,6),(2,6),(2,4),(3,5),(4,7),(5,6),(6,7),
            (3,8),(5,8),(4,9),(7,9),(8,10),(8,11),(9,13),(9,14),(10,15),(14,19),
            (15,20),(11,12),(12,13),(11,16),(12,16),(12,17),(12,18),(13,18),(16,17),(17,18),
            (20,16),(18,24),(16,21),(17,21),(17,22),(17,23),(18,23),(21,22),(22,23),
            (21,25),(21,26),(22,26),(22,27),(22,28),(23,28),(23,29),(25,26),(26,27),(27,28),(28,29),
            (25,30),(26,30),(26,31),(27,31),(27,32),(28,32),(28,33),(29,33),(30,31),(32,33),
            (30,34),(31,34),(31,35),(32,36),(32,37),(33,37),(34,35),(36,37),
            (34,38),(35,38),(35,39),(36,40),(36,41),(37,41),(38,39),(40,41)
        };
        foreach (var (a, b) in links) ds.DrawLine(nodes[a], nodes[b], mesh, 0.58f * scale);

        ds.DrawEllipse(headCenter, 14f * perspective * scale, 6.2f * scale,
            Color.FromArgb(155, glow.R, glow.G, glow.B), 0.7f);
        ds.DrawEllipse(headCenter + new Vector2(0, -4 * scale), 10f * perspective * scale, 13f * scale,
            Color.FromArgb(92, glow.R, glow.G, glow.B), 0.55f);
        ds.DrawLine(P(0, -122), P(0, -83), Color.FromArgb(145, glow.R, glow.G, glow.B), 0.65f);
        ds.DrawLine(P(-12, -113), P(12, -96), Color.FromArgb(100, glow.R, glow.G, glow.B), 0.55f);
        ds.DrawLine(P(12, -113), P(-12, -96), Color.FromArgb(100, glow.R, glow.G, glow.B), 0.55f);

        foreach (var joint in new[] { P(-28, -70), P(28, -70), P(-39, -26), P(39, -26), P(-44, 11), P(44, 11), P(-21, 28), P(21, 28), P(-18, 69), P(18, 69) })
        {
            ds.FillCircle(joint, 3.2f * scale, Color.FromArgb(42, glow.R, glow.G, glow.B));
            ds.DrawCircle(joint, 1.45f * scale, Color.FromArgb(205, glow.R, glow.G, glow.B), 0.75f);
        }

        for (var scan = -76; scan <= 88; scan += 9)
        {
            var half = scan < -48 ? 25f : scan < 15 ? 19f : scan < 36 ? 22f : 18f;
            ds.DrawLine(P(-half, scan), P(half, scan), Color.FromArgb(42, glow.R, glow.G, glow.B), 0.48f);
        }

        var baseCenter = P(0, 104);
        var orbit = 1f + MathF.Sin(time * 2f) * 0.05f;
        ds.FillEllipse(baseCenter + new Vector2(0, 5 * scale), 66 * scale, 17 * scale,
            Color.FromArgb(18, glow.R, glow.G, glow.B));
        ds.DrawEllipse(baseCenter, 68 * scale * orbit, 15 * scale, Color.FromArgb(28, glow.R, glow.G, glow.B), 15f);
        ds.DrawEllipse(baseCenter, 68 * scale * orbit, 15 * scale, Color.FromArgb(235, glow.R, glow.G, glow.B), 1.5f);
        ds.DrawEllipse(baseCenter + new Vector2(0, 6 * scale), 57 * scale, 11 * scale,
            Color.FromArgb(190, glow.R, glow.G, glow.B), 2.5f);
        ds.DrawEllipse(baseCenter - new Vector2(0, 6 * scale), 45 * scale, 7.5f * scale,
            Color.FromArgb(220, pale.R, pale.G, pale.B), 1.15f);
        ds.DrawEllipse(baseCenter - new Vector2(0, 10 * scale), 31 * scale, 5 * scale,
            Color.FromArgb(125, glow.R, glow.G, glow.B), 4f);
        ds.DrawLine(baseCenter + new Vector2(-50 * scale, 8 * scale), baseCenter + new Vector2(-42 * scale, 18 * scale),
            Color.FromArgb(130, glow.R, glow.G, glow.B), 1f);
        ds.DrawLine(baseCenter + new Vector2(50 * scale, 8 * scale), baseCenter + new Vector2(42 * scale, 18 * scale),
            Color.FromArgb(130, glow.R, glow.G, glow.B), 1f);
        for (var beam = -2; beam <= 2; beam++)
        {
            var beamX = center.X + beam * 15 * scale;
            ds.DrawLine(beamX, baseCenter.Y - 4 * scale, beamX + MathF.Sin(yaw + beam) * 4 * scale,
                P(0, -94).Y, Color.FromArgb((byte)(18 + (2 - Math.Abs(beam)) * 10), glow.R, glow.G, glow.B), 1f);
        }
        for (var particle = 0; particle < 34; particle++)
        {
            var phase = particle * 2.399963f + time * (0.22f + particle % 4 * 0.04f);
            var py = baseCenter.Y - ((particle * 31 + time * 19) % (228 * scale));
            var px = center.X + MathF.Sin(phase) * (33 + particle % 6 * 6) * scale;
            var alpha = (byte)(55 + particle % 4 * 28);
            ds.FillCircle(px, py, (particle % 3 == 0 ? 1.2f : 0.7f) * scale,
                Color.FromArgb(alpha, glow.R, glow.G, glow.B));
        }
        var scanY = top + ((time * 36f) % Math.Max(1f, bottom - top));
        ds.DrawLine(center.X - 42 * scale, scanY, center.X + 42 * scale, scanY,
            Color.FromArgb(118, glow.R, glow.G, glow.B), 0.8f);
    }

    private void DrawPortraitWireframe(CanvasDrawingSession ds, GlbWireframeModel model, Vector2 center,
        float diameter, float time, Color cyan, Color pale)
    {
        var scale = Math.Clamp(diameter / 760f, 0.70f, 1.08f);
        var totalTasks = _appTaskCount + _userTaskCount;
        var load = Math.Clamp(totalTasks / (float)TaskReservoirCapacity, 0f, 1f);
        var full = totalTasks >= TaskReservoirCapacity;
        var glow = full ? Color.FromArgb(255, 255, 72, 82) : cyan;
        var fluid = full ? Color.FromArgb(255, 255, 48, 58) : Color.FromArgb(255, 0, 229, 255);
        var bounds = model.Maximum - model.Minimum;
        var modelHeight = Math.Max(0.001f, bounds.Y);
        var modelCenter = (model.Minimum + model.Maximum) * 0.5f;
        var fit = 216f * scale / modelHeight;
        var anchor = center + new Vector2(0, -13f * scale);
        var cosine = MathF.Cos(_portraitYaw);
        var sine = MathF.Sin(_portraitYaw);
        var projected = new Vector2[model.Vertices.Length];
        var depths = new float[model.Vertices.Length];

        for (var index = 0; index < model.Vertices.Length; index++)
        {
            var local = model.Vertices[index] - modelCenter;
            var rotatedX = local.X * cosine + local.Z * sine;
            var rotatedZ = -local.X * sine + local.Z * cosine;
            var perspective = 1f / Math.Max(0.72f, 1f + rotatedZ / modelHeight * 0.48f);
            projected[index] = new Vector2(anchor.X + rotatedX * fit * perspective,
                anchor.Y - local.Y * fit * perspective);
            depths[index] = rotatedZ / modelHeight;
        }

        ds.FillCircle(center + new Vector2(0, -14 * scale), 62f * scale,
            Color.FromArgb(10, glow.R, glow.G, glow.B));
        for (var offset = 0; offset < model.TriangleIndices.Length; offset += 3)
        {
            var a = model.TriangleIndices[offset];
            var b = model.TriangleIndices[offset + 1];
            var c = model.TriangleIndices[offset + 2];
            DrawPortraitEdge(ds, model, projected, depths, a, b, load, glow, fluid, scale);
            DrawPortraitEdge(ds, model, projected, depths, b, c, load, glow, fluid, scale);
            DrawPortraitEdge(ds, model, projected, depths, c, a, load, glow, fluid, scale);
        }

        var baseCenter = center + new Vector2(0, 104 * scale);
        var orbit = 1f + MathF.Sin(time * 2f) * 0.05f;
        ds.FillEllipse(baseCenter + new Vector2(0, 5 * scale), 66 * scale, 17 * scale,
            Color.FromArgb(18, glow.R, glow.G, glow.B));
        ds.DrawEllipse(baseCenter, 68 * scale * orbit, 15 * scale,
            Color.FromArgb(32, glow.R, glow.G, glow.B), 14f);
        ds.DrawEllipse(baseCenter, 68 * scale * orbit, 15 * scale,
            Color.FromArgb(235, glow.R, glow.G, glow.B), 1.5f);
        ds.DrawEllipse(baseCenter + new Vector2(0, 6 * scale), 57 * scale, 11 * scale,
            Color.FromArgb(190, glow.R, glow.G, glow.B), 2.5f);
        ds.DrawEllipse(baseCenter - new Vector2(0, 6 * scale), 45 * scale, 7.5f * scale,
            Color.FromArgb(220, pale.R, pale.G, pale.B), 1.15f);

        for (var beam = -2; beam <= 2; beam++)
        {
            var beamX = center.X + beam * 15 * scale;
            ds.DrawLine(beamX, baseCenter.Y - 4 * scale, beamX + MathF.Sin(_portraitYaw + beam) * 4 * scale,
                center.Y - 102 * scale, Color.FromArgb((byte)(18 + (2 - Math.Abs(beam)) * 10),
                    glow.R, glow.G, glow.B), 1f);
        }

        var top = center.Y - 121 * scale;
        var bottom = center.Y + 96 * scale;
        var scanY = top + ((time * 36f) % Math.Max(1f, bottom - top));
        ds.DrawLine(center.X - 55 * scale, scanY, center.X + 55 * scale, scanY,
            Color.FromArgb(105, glow.R, glow.G, glow.B), 0.8f);
    }

    private static void DrawPortraitEdge(CanvasDrawingSession ds, GlbWireframeModel model,
        Vector2[] projected, float[] depths, int first, int second, float load, Color glow, Color fluid,
        float scale)
    {
        var midpointY = (model.Vertices[first].Y + model.Vertices[second].Y) * 0.5f;
        var normalizedHeight = (midpointY - model.Minimum.Y) / Math.Max(0.001f, model.Maximum.Y - model.Minimum.Y);
        var submerged = normalizedHeight <= load;
        var depth = Math.Clamp((depths[first] + depths[second]) * 0.5f + 0.5f, 0f, 1f);
        var source = submerged ? fluid : glow;
        var alpha = (byte)Math.Clamp((submerged ? 158f : 76f) + depth * 82f, 35f, 235f);
        ds.DrawLine(projected[first], projected[second],
            Color.FromArgb(alpha, source.R, source.G, source.B), (submerged ? 0.82f : 0.58f) * scale);
    }

    private void DrawTaskReservoirReadout(CanvasDrawingSession ds, Vector2 center, float diameter,
        Color cyan, Color pale, Color muted)
    {
        var total = _appTaskCount + _userTaskCount;
        var percent = Math.Clamp(total * 100 / TaskReservoirCapacity, 0, 100);
        var full = total >= TaskReservoirCapacity;
        var color = full ? Color.FromArgb(255, 255, 48, 58) : cyan;
        var width = Math.Clamp(diameter * 0.23f, 135f, 190f);
        DrawHudText(ds, $"TASK RESERVOIR  {percent:00}%", center.X - width / 2, center.Y,
            width, 16, color, 8f);
        DrawHudText(ds, $"DAGNUE {_appTaskCount:00}  //  USER {_userTaskCount:00}  //  CAP {TaskReservoirCapacity:00}",
            center.X - width / 2, center.Y + 15, width, 13,
            Color.FromArgb(150, pale.R, pale.G, pale.B), 6.3f);
        if (full)
            DrawHudText(ds, "CAPACITY CRITICAL", center.X - width / 2, center.Y + 29, width, 13,
                Color.FromArgb(245, 255, 48, 58), 6.8f);
    }

    private static void DrawChronoNode(CanvasDrawingSession ds, Vector2 center, float radius, float time,
        Color cyan, Color pale, Color muted)
    {
        var now = DateTimeOffset.Now;
        DrawArc(ds, center, radius, -165, 71, Color.FromArgb(175, pale.R, pale.G, pale.B), 2.5f);
        DrawArc(ds, center, radius, -86, 98, Color.FromArgb(105, pale.R, pale.G, pale.B), 2f);
        DrawArc(ds, center, radius, 22, 119, Color.FromArgb(205, pale.R, pale.G, pale.B), 2.5f);
        DrawArc(ds, center, radius - 7, -145, 274, Color.FromArgb(62, pale.R, pale.G, pale.B), 1.1f);
        DrawTicks(ds, center, radius - 10, 48, 0, pale, 2.2f, 5.2f);

        var secondSweep = (now.Second + now.Millisecond / 1000f) * 6;
        DrawArc(ds, center, radius - 14, -90, secondSweep,
            Color.FromArgb(46, cyan.R, cyan.G, cyan.B), 8f);
        DrawArc(ds, center, radius - 14, -90, secondSweep,
            Color.FromArgb(235, cyan.R, cyan.G, cyan.B), 2.4f);

        DrawHudText(ds, now.ToString("HH:mm"), center.X - radius * 0.62f, center.Y - 9,
            radius * 1.24f, 18, Color.FromArgb(245, pale.R, pale.G, pale.B), 15f);
        DrawHudText(ds, now.ToString("dddd").ToUpperInvariant(), center.X - radius, center.Y - radius - 17,
            radius * 2, 12, Color.FromArgb(175, pale.R, pale.G, pale.B), 7f);

        var satellite = center + new Vector2(radius * 0.95f, radius * 1.05f);
        ds.DrawCircle(satellite, 7.5f, Color.FromArgb(140, pale.R, pale.G, pale.B), 2f);
        ds.DrawCircle(satellite + new Vector2(-20, 17), 5.5f, Color.FromArgb(105, muted.R, muted.G, muted.B), 1.5f);
    }

    private void DrawLinkGauge(CanvasDrawingSession ds, Vector2 center, float radius, float time,
        Color cyan, Color pale, Color muted)
    {
        var signal = _networkAvailable ? 0.88f + 0.035f * MathF.Sin(time * 1.4f) : 0.06f;
        DrawArc(ds, center, radius, -86, 302, Color.FromArgb(45, muted.R, muted.G, muted.B), 8f);
        DrawArc(ds, center, radius, -86, 302 * signal,
            Color.FromArgb(38, cyan.R, cyan.G, cyan.B), 14f);
        DrawArc(ds, center, radius, -86, 302 * signal,
            Color.FromArgb(245, cyan.R, cyan.G, cyan.B), 8f);
        ds.DrawCircle(center, radius + 7, Color.FromArgb(60, pale.R, pale.G, pale.B), 1f);

        DrawHudText(ds, _networkAvailable ? "LINK  ONLINE" : "LINK  OFFLINE",
            center.X - 60, center.Y + radius + 11, 120, 18,
            _networkAvailable ? cyan : muted, 10f);
        DrawHudText(ds, "LOCAL SIGNAL ARRAY", center.X - 60, center.Y + radius + 27, 120, 12,
            Color.FromArgb(145, pale.R, pale.G, pale.B), 6.5f);
    }

    private static void DrawCpuRotor(CanvasDrawingSession ds, Vector2 center, float radius, float value,
        float time, Color cyan, Color pale, Color muted)
    {
        var normalized = Math.Clamp(value, 0, 100) / 100f;
        ds.FillCircle(center, radius + 8, Color.FromArgb(10, cyan.R, cyan.G, cyan.B));
        DrawArc(ds, center, radius + 6, -145, 72, Color.FromArgb(115, pale.R, pale.G, pale.B), 1.2f);
        DrawArc(ds, center, radius + 6, -60, 96, Color.FromArgb(56, pale.R, pale.G, pale.B), 1f);
        DrawArc(ds, center, radius + 6, 49, 82, Color.FromArgb(165, pale.R, pale.G, pale.B), 1.4f);
        DrawArc(ds, center, radius, -90, 300, Color.FromArgb(34, muted.R, muted.G, muted.B), 5f);
        DrawArc(ds, center, radius, -90, 300 * normalized,
            Color.FromArgb(38, cyan.R, cyan.G, cyan.B), 9f);
        DrawArc(ds, center, radius, -90, 300 * normalized,
            Color.FromArgb(238, cyan.R, cyan.G, cyan.B), 3.4f);
        DrawArc(ds, center, radius - 8, -70 + time * 32, 92,
            Color.FromArgb(210, pale.R, pale.G, pale.B), 1.5f);

        for (var index = 0; index < 12; index++)
        {
            var angle = (-90 + index * 30) * MathF.PI / 180;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            ds.DrawLine(center + direction * (radius + 9), center + direction * (radius + (index % 3 == 0 ? 15 : 12)),
                Color.FromArgb(index % 3 == 0 ? (byte)205 : (byte)85, pale.R, pale.G, pale.B),
                index % 3 == 0 ? 1.2f : 0.7f);
        }

        var diamond = radius * 0.25f;
        ds.DrawLine(center + new Vector2(0, -diamond), center + new Vector2(diamond, 0), cyan, 1.2f);
        ds.DrawLine(center + new Vector2(diamond, 0), center + new Vector2(0, diamond), cyan, 1.2f);
        ds.DrawLine(center + new Vector2(0, diamond), center + new Vector2(-diamond, 0), cyan, 1.2f);
        ds.DrawLine(center + new Vector2(-diamond, 0), center + new Vector2(0, -diamond), cyan, 1.2f);
        ds.FillCircle(center, 2.2f, Color.FromArgb(235, pale.R, pale.G, pale.B));

        DrawHudText(ds, "CPU", center.X - 30, center.Y - radius - 31, 60, 13,
            Color.FromArgb(188, pale.R, pale.G, pale.B), 7f);
        DrawHudText(ds, $"{value:00}%", center.X - 34, center.Y + radius + 16, 68, 15,
            Color.FromArgb(238, cyan.R, cyan.G, cyan.B), 9f);
    }

    private static void DrawDiskCrystal(CanvasDrawingSession ds, Vector2 center, float height, float value,
        float time, Color cyan, Color pale, Color muted)
    {
        const int segments = 13;
        var halfWidth = 15f;
        var top = center.Y - height / 2;
        var bottom = center.Y + height / 2;
        var left = center.X - halfWidth;
        var right = center.X + halfWidth;
        var active = (int)MathF.Round(Math.Clamp(value, 0, 100) / 100 * segments);

        ds.FillRectangle(left - 5, top + 13, halfWidth * 2 + 10, height - 26,
            Color.FromArgb(8, cyan.R, cyan.G, cyan.B));
        ds.DrawLine(center.X, top, right + 4, top + 13, Color.FromArgb(180, pale.R, pale.G, pale.B), 1.4f);
        ds.DrawLine(right + 4, top + 13, right + 4, bottom - 14, Color.FromArgb(110, pale.R, pale.G, pale.B), 1.1f);
        ds.DrawLine(right + 4, bottom - 14, center.X, bottom, Color.FromArgb(185, pale.R, pale.G, pale.B), 1.4f);
        ds.DrawLine(center.X, bottom, left - 4, bottom - 14, Color.FromArgb(185, pale.R, pale.G, pale.B), 1.4f);
        ds.DrawLine(left - 4, bottom - 14, left - 4, top + 13, Color.FromArgb(110, pale.R, pale.G, pale.B), 1.1f);
        ds.DrawLine(left - 4, top + 13, center.X, top, Color.FromArgb(180, pale.R, pale.G, pale.B), 1.4f);

        var segmentTop = top + 16;
        var segmentArea = height - 32;
        var gap = 2f;
        var segmentHeight = (segmentArea - gap * (segments - 1)) / segments;
        for (var index = 0; index < segments; index++)
        {
            var y = segmentTop + index * (segmentHeight + gap);
            var isActive = index >= segments - active;
            var inset = MathF.Abs(index - (segments - 1) / 2f) * 0.28f;
            var x = left + inset;
            var width = halfWidth * 2 - inset * 2;
            var alpha = isActive ? (byte)(175 + index * 5) : (byte)45;
            ds.FillRectangle(x, y, width, segmentHeight,
                isActive ? Color.FromArgb(alpha, cyan.R, cyan.G, cyan.B) : Color.FromArgb(alpha, muted.R, muted.G, muted.B));
            ds.DrawRectangle(x, y, width, segmentHeight,
                Color.FromArgb(isActive ? (byte)150 : (byte)45, pale.R, pale.G, pale.B), 0.65f);
        }

        var scanY = segmentTop + ((time * 23) % MathF.Max(1, segmentArea));
        ds.DrawLine(left - 1, scanY, right + 1, scanY, Color.FromArgb(145, cyan.R, cyan.G, cyan.B), 1f);
        ds.DrawLine(right + 8, top + 23, right + 12, top + 23, Color.FromArgb(130, cyan.R, cyan.G, cyan.B), 1f);
        ds.DrawLine(right + 8, bottom - 23, right + 12, bottom - 23, Color.FromArgb(75, cyan.R, cyan.G, cyan.B), 1f);

        DrawHudText(ds, "DISK", center.X - 31, top - 25, 62, 13,
            Color.FromArgb(188, pale.R, pale.G, pale.B), 7f);
        DrawHudText(ds, $"{value:00}%", center.X - 34, bottom + 8, 68, 15,
            Color.FromArgb(238, cyan.R, cyan.G, cyan.B), 9f);
    }

    private static void DrawTaskEntities(CanvasDrawingSession ds, Vector2 center, int taskCount, float time,
        Color cyan, Color pale, Color muted)
    {
        const int slots = 5;
        const float spacing = 23f;
        var startX = center.X - spacing * (slots - 1) / 2;
        DrawHudText(ds, "TASK ARRAY", center.X - 70, center.Y - 35, 140, 13,
            Color.FromArgb(180, pale.R, pale.G, pale.B), 7f);

        for (var index = 0; index < slots; index++)
        {
            var active = index < Math.Min(taskCount, slots);
            var x = startX + index * spacing;
            var y = center.Y + (active ? MathF.Sin(time * 2.4f + index) * 1.4f : 0);
            var color = active ? cyan : muted;
            var strong = active ? (byte)235 : (byte)70;
            var soft = active ? (byte)48 : (byte)12;

            if (active) ds.FillCircle(new Vector2(x, y + 1), 11, Color.FromArgb(soft, color.R, color.G, color.B));
            ds.DrawLine(new Vector2(x, y - 10), new Vector2(x + 4, y - 6), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x + 4, y - 6), new Vector2(x, y - 2), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x, y - 2), new Vector2(x - 4, y - 6), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x - 4, y - 6), new Vector2(x, y - 10), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.FillCircle(new Vector2(x, y - 6), 1.25f, active ? pale : color);

            ds.DrawLine(new Vector2(x - 5, y), new Vector2(x + 5, y), Color.FromArgb(strong, color.R, color.G, color.B), 1.2f);
            ds.DrawLine(new Vector2(x - 5, y), new Vector2(x - 8, y + 6), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x + 5, y), new Vector2(x + 8, y + 6), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x - 5, y), new Vector2(x - 3, y + 10), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x + 5, y), new Vector2(x + 3, y + 10), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x - 3, y + 10), new Vector2(x, y + 13), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
            ds.DrawLine(new Vector2(x + 3, y + 10), new Vector2(x, y + 13), Color.FromArgb(strong, color.R, color.G, color.B), 1f);
        }

        DrawHudText(ds, taskCount > slots ? $"ACTIVE {taskCount:00}+" : $"ACTIVE {taskCount:00}",
            center.X - 60, center.Y + 19, 120, 13,
            taskCount > 0 ? Color.FromArgb(230, cyan.R, cyan.G, cyan.B) : Color.FromArgb(120, muted.R, muted.G, muted.B), 7f);
    }

    private static void DrawSegmentedMeter(CanvasDrawingSession ds, Vector2 center, float height, float value,
        string label, Color cyan, Color pale, Color muted)
    {
        const int segments = 22;
        var width = 18f;
        var gap = 2f;
        var top = center.Y - height / 2;
        var taperInset = 14f;
        var segmentTop = top + taperInset;
        var segmentAreaHeight = height - taperInset * 2;
        var segmentHeight = (segmentAreaHeight - gap * (segments - 1)) / segments;
        var active = (int)MathF.Round(Math.Clamp(value, 0, 100) / 100 * segments);

        ds.DrawLine(center.X - width / 2 - 6, top + 12, center.X - width / 2 - 6, top + height - 12,
            Color.FromArgb(80, pale.R, pale.G, pale.B), 1.2f);
        ds.DrawLine(center.X + width / 2 + 6, top + 12, center.X + width / 2 + 6, top + height - 12,
            Color.FromArgb(140, pale.R, pale.G, pale.B), 1.5f);
        ds.DrawLine(center.X - width / 2 - 6, top + 12, center.X + width / 2, top,
            Color.FromArgb(145, pale.R, pale.G, pale.B), 1.5f);
        ds.DrawLine(center.X + width / 2, top, center.X + width / 2 + 6, top + 12,
            Color.FromArgb(145, pale.R, pale.G, pale.B), 1.5f);
        ds.DrawLine(center.X - width / 2 - 6, top + height - 12, center.X, top + height,
            Color.FromArgb(145, pale.R, pale.G, pale.B), 1.5f);
        ds.DrawLine(center.X + width / 2 + 6, top + height - 12, center.X, top + height,
            Color.FromArgb(145, pale.R, pale.G, pale.B), 1.5f);

        for (var i = 0; i < segments; i++)
        {
            var y = segmentTop + i * (segmentHeight + gap);
            var isActive = i >= segments - active;
            var color = isActive
                ? Color.FromArgb((byte)(175 + 70f * (i + 1) / segments), cyan.R, cyan.G, cyan.B)
                : Color.FromArgb(72, muted.R, muted.G, muted.B);
            ds.FillRectangle(center.X - width / 2, y, width, segmentHeight, color);
            ds.DrawRectangle(center.X - width / 2, y, width, segmentHeight,
                Color.FromArgb(isActive ? (byte)185 : (byte)55, pale.R, pale.G, pale.B), 0.7f);
        }

        DrawHudText(ds, label, center.X - 26, top - 24, 52, 13,
            Color.FromArgb(180, pale.R, pale.G, pale.B), 7f);
        DrawHudText(ds, $"{value:00}%", center.X - 30, top + height + 8, 60, 16,
            Color.FromArgb(235, cyan.R, cyan.G, cyan.B), 10f);
    }

    private static void DrawHudText(CanvasDrawingSession ds, string text, float x, float y, float width,
        float height, Color color, float fontSize,
        CanvasHorizontalAlignment alignment = CanvasHorizontalAlignment.Center)
    {
        var format = GetTextFormat(fontSize, alignment);
        ds.DrawText(text, x, y, width, height, color, format);
    }

    private static void DrawOuterArchitecture(CanvasDrawingSession ds, Vector2 center, float d, float time, Color cyan, Color pale, Color muted)
    {
        DrawOuterCellInstrument(ds, center, d, time, cyan, pale);

        var outer = d * 0.468f;
        DrawArc(ds, center, outer, -164, 116, Color.FromArgb(205, pale.R, pale.G, pale.B), 2.2f);
        DrawArc(ds, center, outer, -33, 71, Color.FromArgb(230, pale.R, pale.G, pale.B), 2.2f);
        DrawArc(ds, center, outer, 53, 84, Color.FromArgb(190, pale.R, pale.G, pale.B), 2.2f);
        DrawArc(ds, center, outer, 151, 42, Color.FromArgb(245, cyan.R, cyan.G, cyan.B), 3.2f);
        DrawArc(ds, center, outer - 8, -8 + time * 5, 34, Color.FromArgb(245, cyan.R, cyan.G, cyan.B), 4.2f);

        DrawTicks(ds, center, d * 0.448f, 144, time * -0.7f, pale, 4.5f, 11f);
        DrawTicks(ds, center, d * 0.415f, 72, time * 0.9f, cyan, 3f, 7f);

        for (var i = 0; i < 8; i++)
        {
            var angle = (i * 45f - 22.5f) * MathF.PI / 180;
            var p = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (d * 0.455f);
            ds.DrawCircle(p, i % 2 == 0 ? 4.5f : 2.1f, i % 2 == 0 ? cyan : muted, 1.2f);
        }
    }

    private static void DrawOuterCellInstrument(CanvasDrawingSession ds, Vector2 center, float d, float time,
        Color stateColor, Color pale)
    {
        var innerTrack = d * 0.397f;
        var outerTrack = d * 0.411f;

        // Broken graphite tracks preserve the open, mechanical construction of the reference dial.
        DrawArc(ds, center, innerTrack, -171, 70, Color.FromArgb(58, pale.R, pale.G, pale.B), 1.1f);
        DrawArc(ds, center, innerTrack, -95, 76, Color.FromArgb(62, pale.R, pale.G, pale.B), 1.1f);
        DrawArc(ds, center, innerTrack, -13, 78, Color.FromArgb(55, pale.R, pale.G, pale.B), 1.1f);
        DrawArc(ds, center, innerTrack, 72, 101, Color.FromArgb(58, pale.R, pale.G, pale.B), 1.1f);

        DrawArc(ds, center, outerTrack, -166, 64, Color.FromArgb(225, pale.R, pale.G, pale.B), 2.35f);
        DrawArc(ds, center, outerTrack, -96, 78, Color.FromArgb(238, pale.R, pale.G, pale.B), 2.35f);
        DrawArc(ds, center, outerTrack, -12, 53, Color.FromArgb(205, pale.R, pale.G, pale.B), 2.2f);
        DrawArc(ds, center, outerTrack, 48, 65, Color.FromArgb(224, pale.R, pale.G, pale.B), 2.35f);
        DrawArc(ds, center, outerTrack, 120, 54, Color.FromArgb(218, pale.R, pale.G, pale.B), 2.25f);

        var scanAngle = -7f + 4f * MathF.Sin(time * 0.75f);
        DrawArc(ds, center, outerTrack + 1.5f, scanAngle, 17,
            Color.FromArgb(42, stateColor.R, stateColor.G, stateColor.B), 8f);
        DrawArc(ds, center, outerTrack + 1.5f, scanAngle, 17,
            Color.FromArgb(250, stateColor.R, stateColor.G, stateColor.B), 3.3f);

        DrawOuterStatusLabel(ds, center, d * 0.404f, -158, "UP", pale);
        DrawOuterStatusLabel(ds, center, d * 0.404f, -22, "FREE", pale);
        DrawOuterStatusLabel(ds, center, d * 0.404f, 158, "DN", pale);
        DrawOuterStatusLabel(ds, center, d * 0.404f, 22, "USED", pale);

        var topMarker = center + Direction(-88) * outerTrack;
        var topTangent = Direction(2);
        ds.DrawCircle(topMarker - topTangent * 4.2f, 2.2f, Color.FromArgb(205, pale.R, pale.G, pale.B), 1f);
        ds.DrawCircle(topMarker + topTangent * 4.2f, 2.2f, Color.FromArgb(145, pale.R, pale.G, pale.B), 1f);

        DrawOuterBarPair(ds, center, outerTrack, 0, pale);
        DrawOuterBarPair(ds, center, outerTrack, 180, stateColor);
    }

    private static void DrawOuterStatusLabel(CanvasDrawingSession ds, Vector2 center, float radius,
        float angleDegrees, string text, Color pale)
    {
        var labelCenter = center + Direction(angleDegrees) * radius;
        var rotation = angleDegrees + 90;
        if (rotation > 90) rotation -= 180;
        if (rotation < -90) rotation += 180;

        var format = GetTextFormat(7.2f, CanvasHorizontalAlignment.Center);

        var previousTransform = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation(rotation * MathF.PI / 180, labelCenter);
        try
        {
            ds.DrawText(text, labelCenter.X - 18, labelCenter.Y - 7, 36, 14,
                Color.FromArgb(245, pale.R, pale.G, pale.B), format);
        }
        finally
        {
            ds.Transform = previousTransform;
        }
    }

    private static void DrawOuterBarPair(CanvasDrawingSession ds, Vector2 center, float radius,
        float angleDegrees, Color color)
    {
        var radial = Direction(angleDegrees);
        var tangent = new Vector2(-radial.Y, radial.X);
        var marker = center + radial * radius;
        ds.DrawLine(marker - tangent * 5.5f - radial * 2.2f, marker + tangent * 5.5f - radial * 2.2f,
            Color.FromArgb(235, color.R, color.G, color.B), 2.2f);
        ds.DrawLine(marker - tangent * 5.5f + radial * 2.2f, marker + tangent * 5.5f + radial * 2.2f,
            Color.FromArgb(105, color.R, color.G, color.B), 1.2f);
    }

    private static void DrawNavigationBed(CanvasDrawingSession ds, Vector2 center, float d, float time,
        float navigationRotation, int activeNavigationIndex, Color cyan, Color pale)
    {
        var innerRadius = d * 0.326f;
        var outerRadius = d * 0.382f;
        const int cells = 20;
        for (var i = 0; i < cells; i++)
        {
            var cellCenter = -81f + navigationRotation + i * (360f / cells);
            var navigationSlot = i switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                19 => 3,
                18 => 4,
                _ => -1
            };
            var selected = navigationSlot == activeNavigationIndex;
            var start = cellCenter - 8.25f;
            var sweep = 16.5f;
            var cellColor = Color.FromArgb(selected ? (byte)238 : (byte)188, pale.R, pale.G, pale.B);
            if (selected)
                DrawArc(ds, center, (innerRadius + outerRadius) / 2, start, sweep,
                    Color.FromArgb(25, pale.R, pale.G, pale.B), outerRadius - innerRadius);
            DrawArc(ds, center, outerRadius, start, sweep, cellColor, selected ? 1.65f : 1.15f);
            DrawArc(ds, center, innerRadius, start, sweep, cellColor, selected ? 1.65f : 1.15f);
            ds.DrawLine(center + Direction(start) * innerRadius, center + Direction(start) * outerRadius,
                cellColor, selected ? 1.65f : 1.15f);
            ds.DrawLine(center + Direction(start + sweep) * innerRadius,
                center + Direction(start + sweep) * outerRadius, cellColor, selected ? 1.65f : 1.15f);

            if (i is 4 or 8 or 12 or 16)
            {
                DrawCellSignalGlyph(ds, center, (innerRadius + outerRadius) / 2, cellCenter, d, time, i, pale);
            }
        }

        DrawTicks(ds, center, d * 0.306f, 112, navigationRotation + time * -2.2f, pale, 5f, 12f);
        DrawTicks(ds, center, d * 0.286f, 56, navigationRotation + time * 3.5f, cyan, 9f, 16f);
    }

    private static void DrawCellSignalGlyph(CanvasDrawingSession ds, Vector2 center, float radius,
        float angleDegrees, float diameter, float time, int cellIndex, Color pale)
    {
        var angle = angleDegrees * MathF.PI / 180;
        var glyphCenter = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
        var square = Math.Clamp(diameter * 0.0042f, 2.4f, 4.1f);
        var spacing = square * 1.55f;
        var pulse = 0.82f + 0.18f * MathF.Sin(time * 2.4f + cellIndex * 0.7f);
        var coreAlpha = (byte)(205 + 45 * pulse);
        var glowAlpha = (byte)(28 + 24 * pulse);
        var previousTransform = ds.Transform;
        ds.Transform = Matrix3x2.CreateRotation((angleDegrees + 90) * MathF.PI / 180, glyphCenter);
        try
        {
            foreach (var offset in SignalGlyphOffsets)
            {
                var x = glyphCenter.X + offset.X * spacing - square / 2;
                var y = glyphCenter.Y + offset.Y * spacing - square / 2;
                ds.FillRectangle(x - 1.5f, y - 1.5f, square + 3, square + 3,
                    Color.FromArgb(glowAlpha, pale.R, pale.G, pale.B));
                ds.FillRectangle(x, y, square, square,
                    Color.FromArgb(coreAlpha, 245, 252, 255));
            }

            ds.DrawLine(
                glyphCenter.X - spacing * 0.1f,
                glyphCenter.Y + spacing * 0.45f,
                glyphCenter.X + spacing * 0.1f,
                glyphCenter.Y + spacing * 0.45f,
                Color.FromArgb((byte)(115 * pulse), pale.R, pale.G, pale.B),
                1f);
        }
        finally
        {
            ds.Transform = previousTransform;
        }
    }

    private static void DrawEnergyCore(CanvasDrawingSession ds, Vector2 center, float d, float time, Color cyan, Color pale)
    {
        var glowRadius = d * 0.247f;
        ds.DrawCircle(center, glowRadius, Color.FromArgb(18, cyan.R, cyan.G, cyan.B), 30f);
        ds.DrawCircle(center, glowRadius, Color.FromArgb(35, cyan.R, cyan.G, cyan.B), 19f);
        ds.DrawCircle(center, glowRadius, Color.FromArgb(82, cyan.R, cyan.G, cyan.B), 10f);
        ds.DrawCircle(center, glowRadius, Color.FromArgb(245, cyan.R, cyan.G, cyan.B), 4.2f);
        DrawArc(ds, center, glowRadius + 7, -82 + time * 17, 78, Color.FromArgb(255, 240, 254, 255), 3.2f);
        DrawArc(ds, center, glowRadius + 7, 66 + time * 17, 56, Color.FromArgb(245, cyan.R, cyan.G, cyan.B), 3.2f);

        DrawTicks(ds, center, d * 0.218f, 80, time * -5.2f, cyan, 7f, 14f);
        ds.DrawCircle(center, d * 0.172f, Color.FromArgb(170, pale.R, pale.G, pale.B), 2.2f);
        ds.DrawCircle(center, d * 0.137f, Color.FromArgb(115, pale.R, pale.G, pale.B), 1.2f);
        DrawArc(ds, center, d * 0.112f, -110 + time * -24, 205, Color.FromArgb(235, pale.R, pale.G, pale.B), 6f);
        DrawArc(ds, center, d * 0.094f, 18 + time * 31, 118, Color.FromArgb(255, cyan.R, cyan.G, cyan.B), 3f);
        ds.DrawCircle(center, d * 0.078f, Color.FromArgb(150, pale.R, pale.G, pale.B), 1.4f);
    }

    private static void DrawTicks(CanvasDrawingSession ds, Vector2 center, float radius, int count, float rotationDegrees, Color color, float minorLength, float majorLength)
    {
        for (var i = 0; i < count; i++)
        {
            var angle = (rotationDegrees + i * 360f / count) * MathF.PI / 180;
            var length = i % 8 == 0 ? majorLength : minorLength;
            var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            ds.DrawLine(center + direction * (radius - length), center + direction * radius,
                Color.FromArgb(i % 8 == 0 ? (byte)225 : (byte)115, color.R, color.G, color.B), i % 8 == 0 ? 1.8f : 0.85f);
        }
    }

    private static void DrawArc(CanvasDrawingSession ds, Vector2 center, float radius, float startDegrees, float sweepDegrees, Color color, float width)
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
        var radians = degrees * MathF.PI / 180;
        return new Vector2(MathF.Cos(radians), MathF.Sin(radians));
    }

    private Color StateColor(byte alpha)
    {
        var color = _state switch
        {
            BrainState.Warning => Color.FromArgb(alpha, 255, 72, 82),
            BrainState.AwaitingApproval => Color.FromArgb(alpha, 255, 184, 77),
            BrainState.Completed => Color.FromArgb(alpha, 0, 224, 166),
            BrainState.Offline => Color.FromArgb(alpha, 112, 150, 158),
            _ => Color.FromArgb(alpha, 0, 218, 255)
        };
        return color;
    }
}
