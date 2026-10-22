using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed class AlienCycleSelector : UserControl
{
    private readonly TextBlock _readout = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        FontSize = 9,
        CharacterSpacing = 40
    };
    private readonly TextBlock _index = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 3, 5, 0),
        FontSize = 6,
        Foreground = Brush(130, 0, 229, 255)
    };
    private readonly Border _plate;
    private readonly List<(string Label, string Value)> _options = [];
    private int _selectedIndex;

    public event EventHandler? SelectionChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_options.Count == 0) { _selectedIndex = Math.Max(0, value); return; }
            _selectedIndex = ((value % _options.Count) + _options.Count) % _options.Count;
            UpdateReadout();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string? SelectedValue => _options.Count == 0 ? null : _options[_selectedIndex].Value;

    public AlienCycleSelector()
    {
        MinHeight = 40;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(27) });
        _plate = new Border
        {
            Background = Brush(205, 10, 25, 34), BorderBrush = Brush(95, 0, 229, 255),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2)
        };
        Grid.SetColumnSpan(_plate, 3); grid.Children.Add(_plate);
        var previous = Arrow("‹"); var next = Arrow("›");
        previous.Click += (_, _) => SelectedIndex--;
        next.Click += (_, _) => SelectedIndex++;
        Grid.SetColumn(previous, 0); Grid.SetColumn(_readout, 1); Grid.SetColumn(next, 2);
        grid.Children.Add(previous); grid.Children.Add(_readout); grid.Children.Add(next); grid.Children.Add(_index);
        Grid.SetColumn(_index, 2);
        Content = grid;
        PointerWheelChanged += OnWheel;
        PointerEntered += (_, _) => _plate.BorderBrush = Brush(230, 0, 229, 255);
        PointerExited += (_, _) => _plate.BorderBrush = Brush(95, 0, 229, 255);
    }

    public void SetOptions(params (string Label, object Value)[] options)
    {
        _options.Clear();
        _options.AddRange(options.Select(item => (item.Label, item.Value.ToString() ?? string.Empty)));
        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _options.Count - 1));
        UpdateReadout();
    }

    public void SelectValue(object? value)
    {
        var text = value?.ToString();
        var index = _options.FindIndex(option => string.Equals(option.Value, text, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) SelectedIndex = index;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        SelectedIndex += e.GetCurrentPoint(this).Properties.MouseWheelDelta < 0 ? 1 : -1;
        e.Handled = true;
    }

    private void UpdateReadout()
    {
        _readout.Text = _options.Count == 0 ? "NO OPTIONS" : _options[_selectedIndex].Label;
        _index.Text = _options.Count == 0 ? "00" : $"{_selectedIndex + 1:00}";
    }

    private static Button Arrow(string text) => new()
    {
        Content = text, MinWidth = 0, Width = 27, MinHeight = 0, Height = 38,
        Padding = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
        BorderThickness = new Thickness(0), Foreground = Brush(220, 0, 229, 255), FontSize = 18
    };

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b) => new(Color.FromArgb(a, r, g, b));
}
