using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.UI;

namespace Nexus.App.Controls;

public sealed partial class AlienDatePicker : UserControl
{
    private DateTimeOffset? _date;
    private DateTimeOffset _displayMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);

    public ObservableCollection<AlienCalendarDay> Days { get; } = [];

    public DateTimeOffset? Date
    {
        get => _date;
        set
        {
            _date = value;
            if (value is not null)
                _displayMonth = new DateTimeOffset(value.Value.Year, value.Value.Month, 1, 0, 0, 0, value.Value.Offset);
            UpdateDateReadout();
            BuildMonth();
        }
    }

    public AlienDatePicker()
    {
        InitializeComponent();
        UpdateDateReadout();
        BuildMonth();
    }

    private void CalendarFlyout_Opening(object sender, object e)
    {
        if (_date is not null)
            _displayMonth = new DateTimeOffset(_date.Value.Year, _date.Value.Month, 1, 0, 0, 0, _date.Value.Offset);
        BuildMonth();
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(-1);
        BuildMonth();
    }

    private void NextMonth_Click(object sender, RoutedEventArgs e)
    {
        _displayMonth = _displayMonth.AddMonths(1);
        BuildMonth();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        Date = DateTimeOffset.Now.Date;
        CalendarFlyout.Hide();
    }

    private void DayCell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateTimeOffset selected }) return;
        Date = selected;
        CalendarFlyout.Hide();
    }

    private void UpdateDateReadout()
    {
        DateText.Text = _date is null ? "SELECT DATE" : _date.Value.ToString("dd  ·  MMM  ·  yyyy").ToUpperInvariant();
        SelectedCoordinateText.Text = _date is null ? "NO COORDINATE LOCKED" : _date.Value.ToString("dddd  /  dd.MM.yyyy").ToUpperInvariant();
    }

    private void BuildMonth()
    {
        if (MonthTitle is null) return;
        MonthTitle.Text = _displayMonth.ToString("MMMM  yyyy").ToUpperInvariant();
        Days.Clear();

        var firstOfMonth = new DateTimeOffset(_displayMonth.Year, _displayMonth.Month, 1, 0, 0, 0, _displayMonth.Offset);
        var firstCell = firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek);
        var today = DateTimeOffset.Now.Date;

        for (var index = 0; index < 42; index++)
        {
            var cellDate = firstCell.AddDays(index);
            var currentMonth = cellDate.Month == _displayMonth.Month && cellDate.Year == _displayMonth.Year;
            var selected = _date?.Date == cellDate.Date;
            var isToday = today == cellDate.Date;
            Days.Add(new AlienCalendarDay(cellDate, currentMonth, selected, isToday));
        }
    }
}

public sealed class AlienCalendarDay
{
    public DateTimeOffset Date { get; }
    public string Label => Date.Day.ToString("00");
    public string AccessibleLabel => Date.ToString("dddd, MMMM d, yyyy");
    public Brush Background { get; }
    public Brush BorderBrush { get; }
    public Brush Foreground { get; }
    public double Opacity { get; }

    public AlienCalendarDay(DateTimeOffset date, bool currentMonth, bool selected, bool today)
    {
        Date = date;
        Background = new SolidColorBrush(selected ? Color.FromArgb(92, 0, 229, 255) : Color.FromArgb(10, 8, 19, 27));
        BorderBrush = new SolidColorBrush(selected
            ? Color.FromArgb(255, 0, 229, 255)
            : today ? Color.FromArgb(178, 116, 241, 255) : Color.FromArgb(22, 0, 229, 255));
        Foreground = new SolidColorBrush(selected
            ? Colors.White
            : currentMonth ? Color.FromArgb(230, 222, 239, 245) : Color.FromArgb(96, 111, 133, 143));
        Opacity = currentMonth ? 1 : 0.55;
    }
}
