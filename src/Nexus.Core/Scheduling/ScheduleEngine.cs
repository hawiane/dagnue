using Nexus.Core.Models;

namespace Nexus.Core.Scheduling;

public sealed class ScheduleEngine
{
    public IReadOnlyList<Reminder> GetDue(IEnumerable<Reminder> reminders, DateTimeOffset now)
    {
        return reminders
            .Where(x => x.IsEnabled && x.HasSchedule && x.Status != TaskProgressStatus.Done && x.DueAt <= now &&
                (x.LastTriggeredAt is null || x.LastTriggeredAt < x.DueAt))
            .OrderBy(x => x.DueAt)
            .ToArray();
    }

    public Reminder MarkTriggered(Reminder reminder, DateTimeOffset triggeredAt)
    {
        // A routinary user task advances only after its occurrence is actually completed.
        // Triggering is only the notification that work is due.
        if (reminder.TaskType == UserTaskType.Recurring)
            return reminder with { LastTriggeredAt = triggeredAt };

        var nextDue = GetNextOccurrence(reminder, triggeredAt);
        return reminder.Recurrence == RecurrenceKind.None
            ? reminder with { IsEnabled = false, LastTriggeredAt = triggeredAt }
            : reminder with { DueAt = nextDue, LastTriggeredAt = triggeredAt };
    }

    public DateTimeOffset GetNextOccurrence(Reminder reminder, DateTimeOffset after)
    {
        var next = reminder.DueAt;
        if (reminder.Recurrence == RecurrenceKind.None)
        {
            return next;
        }

        do
        {
            next = reminder.Recurrence switch
            {
                RecurrenceKind.Daily => next.AddDays(1),
                RecurrenceKind.Weekdays => NextWeekday(next),
                RecurrenceKind.Weekly => next.AddDays(7),
                RecurrenceKind.Monthly => next.AddMonths(1),
                _ => next
            };
        }
        while (next <= after);

        return next;
    }

    private static DateTimeOffset NextWeekday(DateTimeOffset value)
    {
        var result = value.AddDays(1);
        while (result.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            result = result.AddDays(1);
        }
        return result;
    }
}
