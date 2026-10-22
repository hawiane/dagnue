using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace Nexus.App.Controls;

public sealed partial class AlienFormChrome : UserControl
{
    private Storyboard? _pulseMotion;
    private bool _animationEnabled;
    private bool _animationRunning;

    public AlienFormChrome()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateAnimationState();
        Unloaded += (_, _) => StopAnimation();
    }

    public void SetAnimationEnabled(bool enabled)
    {
        _animationEnabled = enabled;
        UpdateAnimationState();
    }

    public void SetAnimationPaused(bool paused)
    {
        if (!_animationRunning || _pulseMotion is null) return;
        if (paused)
            _pulseMotion.Pause();
        else if (_animationEnabled)
            _pulseMotion.Resume();
    }

    private void UpdateAnimationState()
    {
        if (!_animationEnabled || !IsLoaded)
        {
            StopAnimation();
            return;
        }

        if (_animationRunning) return;
        {
            var pulse = new DoubleAnimation
            {
                From = 0.25,
                To = 1,
                Duration = new Duration(TimeSpan.FromSeconds(1.4)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(pulse, PulseNode);
            Storyboard.SetTargetProperty(pulse, "Opacity");
            _pulseMotion = new Storyboard();
            _pulseMotion.Children.Add(pulse);
            _pulseMotion.Begin();
            _animationRunning = true;
        }
    }

    private void StopAnimation()
    {
        if (!_animationRunning || _pulseMotion is null) return;
        _pulseMotion.Stop();
        _pulseMotion = null;
        _animationRunning = false;
        PulseNode.Opacity = 0.55;
    }
}
