using Nexus.Core.Models;
using Nexus.Core.Scheduling;

namespace Nexus.Core.Tests;

public sealed class ScheduleEngineTests
{
    private readonly ScheduleEngine _engine = new();

    [Fact]
    public void DueReminder_IsReturned()
    {
        var now = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(8));
        var reminder = Create(now.AddMinutes(-1));
        Assert.Single(_engine.GetDue([reminder], now));
    }

    [Fact]
    public void OneTimeReminder_IsDisabledAfterTrigger()
    {
        var now = DateTimeOffset.Now;
        var updated = _engine.MarkTriggered(Create(now.AddMinutes(-1)), now);
        Assert.False(updated.IsEnabled);
        Assert.Equal(now, updated.LastTriggeredAt);
    }

    [Fact]
    public void WeekdayReminder_SkipsWeekend()
    {
        var friday = new DateTimeOffset(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);
        var reminder = Create(friday) with { Recurrence = RecurrenceKind.Weekdays };
        var next = _engine.GetNextOccurrence(reminder, friday);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact]
    public void TodayOnlyTask_DoesNotTriggerWithoutSchedule()
    {
        var now = DateTimeOffset.Now;
        var task = Create(now.AddMinutes(-1)) with { IsToday = true, HasSchedule = false };
        Assert.Empty(_engine.GetDue([task], now));
    }

    [Fact]
    public void CompletedTask_DoesNotTrigger()
    {
        var now = DateTimeOffset.Now;
        var task = Create(now.AddMinutes(-1)) with { Status = TaskProgressStatus.Done };
        Assert.Empty(_engine.GetDue([task], now));
    }

    [Fact]
    public void RoutinaryTask_DoesNotAdvanceUntilCompletion()
    {
        var now = new DateTimeOffset(2026, 8, 26, 9, 0, 0, TimeSpan.FromHours(8));
        var due = now.AddMinutes(-1);
        var task = Create(due) with
        {
            TaskType = UserTaskType.Recurring,
            Recurrence = RecurrenceKind.Daily
        };

        var notified = _engine.MarkTriggered(task, now);

        Assert.Equal(due, notified.DueAt);
        Assert.True(notified.IsEnabled);
        Assert.Equal(now, notified.LastTriggeredAt);
        Assert.Empty(_engine.GetDue([notified], now.AddMinutes(1)));
    }

    private static Reminder Create(DateTimeOffset due) =>
        new("r1", "Test", null, due, RecurrenceKind.None, true, due.AddDays(-1));
}
