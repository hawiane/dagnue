using Nexus.Core.Models;

namespace Nexus.Core.Scheduling;

public sealed class RoutineEngine
{
    public bool IsValidTrigger(string trigger) => TryParseTrigger(trigger, out _, out _);

    public IReadOnlyList<Routine> GetDue(IEnumerable<Routine> routines, DateTimeOffset now)
    {
        return routines.Where(x => x.IsEnabled && IsDue(x, now)).ToArray();
    }

    public bool IsDue(Routine routine, DateTimeOffset now)
    {
        if (!TryParseTrigger(routine.Trigger, out var days, out var time)) return false;
        if (days is not null && !days.Contains(now.DayOfWeek)) return false;
        var scheduled = new DateTimeOffset(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, now.Offset);
        if (now < scheduled || now - scheduled > TimeSpan.FromMinutes(1)) return false;
        return routine.LastRunAt is null || routine.LastRunAt.Value.Date != now.Date;
    }

    public Routine MarkRun(Routine routine, DateTimeOffset now) => routine with { LastRunAt = now };

    private static bool TryParseTrigger(string trigger, out DayOfWeek[]? days, out TimeSpan time)
    {
        days = null;
        time = default;
        var parts = trigger.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !TimeSpan.TryParse(parts[1], out time)) return false;
        days = parts[0].ToLowerInvariant() switch
        {
            "daily" => null,
            "weekdays" => [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
            "weekends" => [DayOfWeek.Saturday, DayOfWeek.Sunday],
            _ when Enum.TryParse<DayOfWeek>(parts[0], true, out var day) => [day],
            _ => []
        };
        return days is null || days.Length > 0;
    }
}
