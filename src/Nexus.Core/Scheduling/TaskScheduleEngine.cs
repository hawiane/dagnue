using Nexus.Core.Models;

namespace Nexus.Core.Scheduling;

public sealed class TaskScheduleEngine
{
    public IReadOnlyList<TaskSchedule> GetDue(IEnumerable<TaskSchedule> schedules, DateTimeOffset now) =>
        schedules.Where(x => x.IsEnabled && x.NextRunAt <= now)
            .OrderBy(x => x.NextRunAt).ToArray();

    public TaskSchedule MarkRun(TaskSchedule schedule, DateTimeOffset now, string? result = null)
    {
        if (schedule.Recurrence == RecurrenceKind.None)
            return schedule with { IsEnabled = false, LastRunAt = now, LastResult = result };

        var next = schedule.NextRunAt;
        do
        {
            next = schedule.Recurrence switch
            {
                RecurrenceKind.Daily => next.AddDays(1),
                RecurrenceKind.Weekdays => NextWeekday(next),
                RecurrenceKind.Weekly => next.AddDays(7),
                RecurrenceKind.Monthly => next.AddMonths(1),
                _ => next
            };
        } while (next <= now);

        return schedule with { NextRunAt = next, LastRunAt = now, LastResult = result };
    }

    private static DateTimeOffset NextWeekday(DateTimeOffset value)
    {
        var result = value.AddDays(1);
        while (result.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) result = result.AddDays(1);
        return result;
    }
}
