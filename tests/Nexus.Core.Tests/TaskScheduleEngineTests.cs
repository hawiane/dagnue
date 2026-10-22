using Nexus.Core.Models;
using Nexus.Core.Scheduling;

namespace Nexus.Core.Tests;

public sealed class TaskScheduleEngineTests
{
    [Fact]
    public void MarkRun_AdvancesRecurringScheduleAndDisablesOneShot()
    {
        var now = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var engine = new TaskScheduleEngine();
        var daily = new TaskSchedule("d", "w", "Daily", "Run", now.AddHours(-1),
            RecurrenceKind.Daily, true, now.AddDays(-2));
        var once = daily with { Id = "o", Recurrence = RecurrenceKind.None };

        Assert.True(engine.MarkRun(daily, now).NextRunAt > now);
        Assert.False(engine.MarkRun(once, now).IsEnabled);
    }
}
