using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed class AlienToggleChip : ToggleButton
{
    public AlienToggleChip()
    {
        MinHeight = 38;
        MinWidth = 104;
        Padding = new Thickness(14, 0, 14, 0);
        FontSize = 9;
        CharacterSpacing = 45;
        BorderThickness = new Thickness(1);
        HorizontalContentAlignment = HorizontalAlignment.Center;
        UpdateState();
        Checked += (_, _) => UpdateState();
        Unchecked += (_, _) => UpdateState();
    }

    private void UpdateState()
    {
        var active = IsChecked == true;
        Background = new SolidColorBrush(active ? Color.FromArgb(180, 0, 74, 86) : Color.FromArgb(185, 10, 25, 34));
        BorderBrush = new SolidColorBrush(active ? Color.FromArgb(255, 0, 229, 255) : Color.FromArgb(80, 0, 229, 255));
        Foreground = new SolidColorBrush(active ? Colors.White : Color.FromArgb(255, 130, 151, 160));
    }
}
