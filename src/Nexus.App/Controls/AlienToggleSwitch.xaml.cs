using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed partial class AlienToggleSwitch : UserControl
{
    private Storyboard? _motion;

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(AlienToggleSwitch), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty OnContentProperty = DependencyProperty.Register(
        nameof(OnContent), typeof(string), typeof(AlienToggleSwitch), new PropertyMetadata("ON", OnLabelChanged));
    public static readonly DependencyProperty OffContentProperty = DependencyProperty.Register(
        nameof(OffContent), typeof(string), typeof(AlienToggleSwitch), new PropertyMetadata("OFF", OnLabelChanged));
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(AlienToggleSwitch), new PropertyMetadata(false, OnIsOnChanged));

    public AlienToggleSwitch()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyState(false);
    }

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public string OnContent { get => (string)GetValue(OnContentProperty); set => SetValue(OnContentProperty, value); }
    public string OffContent { get => (string)GetValue(OffContentProperty); set => SetValue(OffContentProperty, value); }
    public bool IsOn { get => (bool)GetValue(IsOnProperty); set => SetValue(IsOnProperty, value); }

    private static void OnIsOnChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AlienToggleSwitch control && control.IsLoaded) control.ApplyState(true);
    }

    private static void OnLabelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AlienToggleSwitch control && control.IsLoaded) control.ApplyState(false);
    }

    private void ToggleHit_Click(object sender, RoutedEventArgs e) => IsOn = !IsOn;

    private void ToggleHit_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        TrackGlow.Opacity = 0.92;
        SignalNode.Opacity = 1;
        SwitchHoverTransform.ScaleX = SwitchHoverTransform.ScaleY = 1.045;
    }

    private void ToggleHit_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        TrackGlow.Opacity = IsOn ? 0.36 : 0;
        SignalNode.Opacity = IsOn ? 0.85 : 0.25;
        SwitchHoverTransform.ScaleX = SwitchHoverTransform.ScaleY = 1;
    }

    private void ApplyState(bool animate)
    {
        _motion?.Stop();
        var targetX = IsOn ? 38d : 0d;
        if (animate)
        {
            var animation = new DoubleAnimation
            {
                From = ActuatorTransform.TranslateX,
                To = targetX,
                Duration = new Duration(TimeSpan.FromMilliseconds(260)),
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, ActuatorTransform);
            Storyboard.SetTargetProperty(animation, "TranslateX");
            var rotation = new DoubleAnimation
            {
                From = ActuatorTransform.Rotation,
                To = IsOn ? 180 : 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(rotation, ActuatorTransform);
            Storyboard.SetTargetProperty(rotation, "Rotation");
            _motion = new Storyboard();
            _motion.Children.Add(animation);
            _motion.Children.Add(rotation);
            _motion.Completed += (_, _) =>
            {
                ActuatorTransform.TranslateX = targetX;
                ActuatorTransform.Rotation = IsOn ? 180 : 0;
                _motion = null;
            };
            _motion.Begin();
        }
        else
        {
            ActuatorTransform.TranslateX = targetX;
            ActuatorTransform.Rotation = IsOn ? 180 : 0;
        }

        ActiveTrack.Opacity = IsOn ? 1 : 0;
        TrackGlow.Opacity = IsOn ? 0.36 : 0;
        TrackBase.Stroke = new SolidColorBrush(IsOn
            ? Color.FromArgb(205, 0, 229, 255)
            : Color.FromArgb(140, 129, 154, 164));
        ActuatorCore.Fill = new SolidColorBrush(IsOn
            ? Color.FromArgb(255, 0, 229, 255)
            : Color.FromArgb(255, 113, 137, 145));
        StatusText.Text = IsOn ? OnContent : OffContent;
        StatusText.Foreground = new SolidColorBrush(IsOn
            ? Color.FromArgb(244, 227, 250, 252)
            : Color.FromArgb(201, 216, 232, 237));
        SignalNode.Opacity = IsOn ? 0.85 : 0.25;
        SignalBarA.Opacity = IsOn ? 0.85 : 0.35;
        SignalBarB.Opacity = IsOn ? 1 : 0.45;
        SignalBarC.Opacity = IsOn ? 1 : 0.6;
    }
}
