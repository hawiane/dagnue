using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nexus.App.Services;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;
using Windows.Graphics;
using Windows.UI;

namespace Nexus.App;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _nativeWindow;
    private readonly OverlappedPresenter _presenter;
    private readonly InputNonClientPointerSource _nonClientPointerSource;
    private readonly nint _windowHandle;
    private readonly Queue<InitializationProgress> _bootQueue = new();
    private readonly object _bootQueueLock = new();
    private readonly DispatcherTimer _bootTimer = new() { Interval = TimeSpan.FromMilliseconds(190) };
    private readonly Stopwatch _bootClock = Stopwatch.StartNew();
    private Storyboard? _bootMotion;
    private Storyboard? _titleMotion;
    private readonly WindowSubclassProcedure _windowSubclass;
    private bool _windowInteractionActive;
    private bool _sizeMoveActive;
    private bool _windowMinimized;
    private bool _initializationComplete;
    private bool _bootTransitionStarted;
    private bool _initialMaximizeApplied;
    private int _displayedBootPercent;

    private const uint WmEnterSizeMove = 0x0231;
    private const uint WmExitSizeMove = 0x0232;

    private delegate nint WindowSubclassProcedure(nint windowHandle, uint message, nint wParam, nint lParam,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint windowHandle, WindowSubclassProcedure callback,
        nuint subclassId, nuint referenceData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint windowHandle, WindowSubclassProcedure callback,
        nuint subclassId);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint windowHandle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint windowHandle, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint windowHandle, int attribute, ref uint value, int size);

    private const int GwlStyle = -16;
    private const long WsThickFrame = 0x00040000L;
    private const long WsBorder = 0x00800000L;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const int DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;

    public MainWindow()
    {
        InitializeComponent();
        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _nativeWindow = AppWindow.GetFromWindowId(windowId);
        _presenter = (OverlappedPresenter)_nativeWindow.Presenter;
        _nonClientPointerSource = InputNonClientPointerSource.GetForWindowId(windowId);
        _windowSubclass = WindowSubclassProc;
        SetWindowSubclass(_windowHandle, _windowSubclass, 1, 0);
        // DAGNUE renders its own chrome. The native resize border also creates a
        // light DWM strip/shadow beside the edge-docked readiness instrument.
        _presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        RemoveNativeEdgeChrome();
        _nativeWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        _presenter.Maximize();
        ConfigureNonClientRegions();
        _nativeWindow.Changed += NativeWindow_Changed;
        _nativeWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        _nativeWindow.Closing += OnClosing;
        Activated += MainWindow_Activated;
        App.Services.InitializationProgressed += Services_InitializationProgressed;
        App.Services.Initialized += Services_Initialized;
        RootFrame.Navigate(typeof(MainPage));
        _bootTimer.Tick += BootTimer_Tick;
        _bootTimer.Start();
        StartBootMotion();
        StartTitleBarMotion();
        Closed += (_, _) =>
        {
            _nativeWindow.Changed -= NativeWindow_Changed;
            RemoveWindowSubclass(_windowHandle, _windowSubclass, 1);
        };
    }

    private void RemoveNativeEdgeChrome()
    {
        var style = GetWindowLongPtr(_windowHandle, GwlStyle).ToInt64();
        style &= ~(WsThickFrame | WsBorder);
        SetWindowLongPtr(_windowHandle, GwlStyle, new nint(style));
        var noBorder = DwmColorNone;
        _ = DwmSetWindowAttribute(_windowHandle, DwmwaBorderColor, ref noBorder, sizeof(uint));
        _ = SetWindowPos(_windowHandle, 0, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpFrameChanged);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialMaximizeApplied || args.WindowActivationState == WindowActivationState.Deactivated) return;
        _initialMaximizeApplied = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _presenter.Maximize();
            RemoveNativeEdgeChrome();
            MaximizeButton.Content = "◈";
        });
    }

    private nint WindowSubclassProc(nint windowHandle, uint message, nint wParam, nint lParam,
        nuint subclassId, nuint referenceData)
    {
        if (message == WmEnterSizeMove)
        {
            _sizeMoveActive = true;
            UpdateWindowInteractionMode();
        }
        else if (message == WmExitSizeMove)
        {
            _sizeMoveActive = false;
            UpdateWindowInteractionMode();
        }

        return DefSubclassProc(windowHandle, message, wParam, lParam);
    }

    private void NativeWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange) ConfigureNonClientRegions();
        if (args.DidPresenterChange)
        {
            _windowMinimized = _presenter.State == OverlappedPresenterState.Minimized;
            UpdateWindowInteractionMode();
        }
    }

    private void UpdateWindowInteractionMode() =>
        SetWindowInteractionMode(_sizeMoveActive || _windowMinimized);

    private void ConfigureNonClientRegions()
    {
        var scale = Math.Max(1.0, GetDpiForWindow(_windowHandle) / 96.0);
        var titleHeight = (int)Math.Ceiling(32 * scale);
        var captionControlsWidth = (int)Math.Ceiling(129 * scale);
        var captionWidth = Math.Max(1, _nativeWindow.Size.Width - captionControlsWidth);
        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Caption,
            [new RectInt32(0, 0, captionWidth, titleHeight)]);
        _nonClientPointerSource.SetRegionRects(NonClientRegionKind.Passthrough,
            [new RectInt32(captionWidth, 0, captionControlsWidth, titleHeight)]);
    }

    private void Services_InitializationProgressed(object? sender, InitializationProgress progress)
    {
        lock (_bootQueueLock) _bootQueue.Enqueue(progress);
    }

    private void Services_Initialized(object? sender, EventArgs e) => _initializationComplete = true;

    private async void BootTimer_Tick(object? sender, object e)
    {
        InitializationProgress? progress = null;
        var queueEmpty = false;
        lock (_bootQueueLock)
        {
            if (_bootQueue.Count > 0) progress = _bootQueue.Dequeue();
            queueEmpty = _bootQueue.Count == 0;
        }

        if (progress is not null) DisplayBootProgress(progress);
        if (!_initializationComplete || !queueEmpty || _displayedBootPercent < 100 || _bootTransitionStarted) return;

        _bootTransitionStarted = true;
        _bootTimer.Stop();
        var remaining = TimeSpan.FromMilliseconds(Math.Max(0, 3200 - _bootClock.ElapsedMilliseconds));
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining);
        RevealMainInterface();
    }

    private void DisplayBootProgress(InitializationProgress progress)
    {
        _displayedBootPercent = progress.Percent;
        BootPercentText.Text = $"{progress.Percent:00}%";
        BootStageText.Text = progress.Stage;
        BootDetailText.Text = progress.Detail;
        BootStageText.Foreground = new SolidColorBrush(progress.Warning
            ? Color.FromArgb(255, 255, 190, 76)
            : Color.FromArgb(232, 0, 229, 255));

        BootLog4.Text = BootLog3.Text;
        BootLog3.Text = BootLog2.Text;
        BootLog2.Text = BootLog1.Text;
        BootLog1.Text = $"> {progress.Percent:00}  {progress.Stage}  ::  {progress.Detail}";

        var progressAnimation = new DoubleAnimation
        {
            To = 4.7 * progress.Percent,
            Duration = new Duration(TimeSpan.FromMilliseconds(280)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(progressAnimation, BootProgressFill);
        Storyboard.SetTargetProperty(progressAnimation, "Width");
        var storyboard = new Storyboard();
        storyboard.Children.Add(progressAnimation);
        storyboard.Begin();
    }

    private void StartBootMotion()
    {
        var motion = new Storyboard();
        AddRotation(motion, BootOuterRotation, 0, 360, 12);
        AddRotation(motion, BootMiddleRotation, 360, 0, 8.5);
        AddRotation(motion, BootInnerRotation, 0, 360, 18);

        var pulse = new DoubleAnimation
        {
            From = 0.38,
            To = 1,
            Duration = new Duration(TimeSpan.FromSeconds(1.45)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(pulse, BootCoreGlow);
        Storyboard.SetTargetProperty(pulse, "Opacity");
        motion.Children.Add(pulse);
        _bootMotion = motion;
        motion.Begin();
    }

    private static void AddRotation(Storyboard storyboard, RotateTransform transform, double from, double to, double seconds)
    {
        var rotation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromSeconds(seconds)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(rotation, transform);
        Storyboard.SetTargetProperty(rotation, "Angle");
        storyboard.Children.Add(rotation);
    }

    private void RevealMainInterface()
    {
        var reveal = new Storyboard();
        var bootFade = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(680)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(bootFade, BootOverlay);
        Storyboard.SetTargetProperty(bootFade, "Opacity");
        reveal.Children.Add(bootFade);

        var interfaceFade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(180),
            Duration = new Duration(TimeSpan.FromMilliseconds(820)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(interfaceFade, RootFrame);
        Storyboard.SetTargetProperty(interfaceFade, "Opacity");
        reveal.Children.Add(interfaceFade);
        reveal.Completed += (_, _) =>
        {
            BootOverlay.Visibility = Visibility.Collapsed;
            _bootMotion?.Stop();
            App.Services.InitializationProgressed -= Services_InitializationProgressed;
            App.Services.Initialized -= Services_Initialized;
        };
        reveal.Begin();
    }

    private void StartTitleBarMotion()
    {
        var trace = new DoubleAnimation
        {
            From = 0,
            To = 1350,
            Duration = new Duration(TimeSpan.FromSeconds(6.8)),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(trace, TitleSweepTransform);
        Storyboard.SetTargetProperty(trace, "X");
        var storyboard = new Storyboard();
        storyboard.Children.Add(trace);
        _titleMotion = storyboard;
        storyboard.Begin();
    }

    private void SetWindowInteractionMode(bool active)
    {
        if (_windowInteractionActive == active) return;
        _windowInteractionActive = active;
        if (active)
        {
            _titleMotion?.Pause();
            _bootMotion?.Pause();
        }
        else
        {
            _titleMotion?.Resume();
            if (BootOverlay.Visibility == Visibility.Visible) _bootMotion?.Resume();
        }

        if (RootFrame.Content is MainPage page) page.SetWindowInteractionMode(active);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => _presenter.Minimize();

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize()
    {
        if (_presenter.State == OverlappedPresenterState.Maximized)
        {
            _presenter.Restore();
            MaximizeButton.Content = "◇";
        }
        else
        {
            _presenter.Maximize();
            MaximizeButton.Content = "◈";
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Services.RequestExit();

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!App.Services.IsExitRequested)
        {
            args.Cancel = true;
            sender.Hide();
            App.Services.Notifications.Show("DAGNUE is still active", "The assistant is running in the system tray.");
        }
    }

    public void ShowFromTray()
    {
        _nativeWindow.Show();
        Activate();
    }

    public void ExitFromTray()
    {
        _nativeWindow.Closing -= OnClosing;
        Close();
    }
}
