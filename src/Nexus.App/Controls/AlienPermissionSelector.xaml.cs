using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed partial class AlienPermissionSelector : UserControl
{
    private readonly string[] _labels =
    [
        "OBSERVE // PASSIVE SENSOR",
        "SUGGEST // ADVISORY MODE",
        "CONFIRM // HUMAN GATE",
        "TRUSTED // ROUTINE LINK"
    ];

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(AlienPermissionSelector), new PropertyMetadata("Permission mode"));
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(AlienPermissionSelector), new PropertyMetadata(2, OnSelectedIndexChanged));

    public AlienPermissionSelector()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplySelection();
    }

    public string Header { get => (string)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, Math.Clamp(value, 0, 3)); }
    public string SelectedTag => SelectedIndex switch
    {
        0 => "Observe",
        1 => "Suggest",
        3 => "TrustedRoutines",
        _ => "Confirm"
    };

    private static void OnSelectedIndexChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is AlienPermissionSelector selector && selector.IsLoaded) selector.ApplySelection();
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string index } && int.TryParse(index, out var selected))
            SelectedIndex = selected;
        PermissionFlyout.Hide();
    }

    private void PermissionFlyout_Opening(object? sender, object e)
    {
        ChevronTransform.Rotation = 180;
        ApplySelection();
    }

    private void PermissionFlyout_Closed(object? sender, object e) => ChevronTransform.Rotation = 0;

    private void ApplySelection()
    {
        SelectionText.Text = _labels[Math.Clamp(SelectedIndex, 0, _labels.Length - 1)];
        var options = new[] { ObserveOption, SuggestOption, ConfirmOption, TrustedOption };
        for (var index = 0; index < options.Length; index++)
        {
            var selected = index == SelectedIndex;
            options[index].Background = new SolidColorBrush(selected
                ? Color.FromArgb(120, 0, 188, 204)
                : Color.FromArgb(184, 11, 23, 32));
            options[index].Foreground = new SolidColorBrush(selected
                ? Color.FromArgb(255, 246, 253, 255)
                : Color.FromArgb(226, 227, 241, 245));
        }
    }
}
