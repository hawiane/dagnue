using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed class AlienTimeSelector : UserControl
{
    private readonly TextBlock _readout = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        FontSize = 10, CharacterSpacing = 80, Foreground = new SolidColorBrush(Colors.White)
    };
    private TimeSpan? _selectedTime;

    public TimeSpan? SelectedTime
    {
        get => _selectedTime;
        set { _selectedTime = value; UpdateReadout(); }
    }

    public AlienTimeSelector()
    {
        MinHeight = 40;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(31) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(31) });
        var plate = new Border { Background = Brush(205, 10, 25, 34), BorderBrush = Brush(95, 0, 229, 255), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(plate, 3); grid.Children.Add(plate);
        var down = Arrow("−"); var up = Arrow("+");
        down.Click += (_, _) => Step(-15); up.Click += (_, _) => Step(15);
        Grid.SetColumn(down, 0); Grid.SetColumn(_readout, 1); Grid.SetColumn(up, 2);
        grid.Children.Add(down); grid.Children.Add(_readout); grid.Children.Add(up);
        Content = grid;
        PointerWheelChanged += OnWheel;
        UpdateReadout();
    }

    private void Step(int minutes)
    {
        var current = _selectedTime ?? DateTime.Now.TimeOfDay;
        var total = ((int)current.TotalMinutes + minutes + 1440) % 1440;
        SelectedTime = TimeSpan.FromMinutes(total);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        Step(e.GetCurrentPoint(this).Properties.MouseWheelDelta < 0 ? -15 : 15);
        e.Handled = true;
    }

    private void UpdateReadout() => _readout.Text = _selectedTime is null ? "-- : --" : $"{_selectedTime.Value.Hours:00} : {_selectedTime.Value.Minutes:00}";
    private static Button Arrow(string text) => new() { Content = text, MinWidth = 0, Width = 31, Height = 38, Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Foreground = Brush(220, 0, 229, 255), FontSize = 15 };
    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
}
