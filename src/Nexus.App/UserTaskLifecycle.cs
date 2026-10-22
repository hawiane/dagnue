using System.Text.RegularExpressions;
using Nexus.Core.Models;

namespace Nexus.App;

internal static partial class UserTaskLifecycle
{
    public static Reminder Complete(Reminder task, string summary, string? resultPath = null, string kind = "completed")
    {
        var now = DateTimeOffset.Now;
        resultPath ??= ExtractExistingPath(summary);
        var activity = (task.Activity ?? []).Append(new TaskActivity(
            Guid.NewGuid().ToString("N"), now, kind, summary, resultPath)).TakeLast(100).ToArray();

        if (task.TaskType == UserTaskType.Recurring)
        {
            var next = NextOccurrence(task, now);
            return task with
            {
                Status = TaskProgressStatus.NotYet,
                DueAt = next,
                HasSchedule = true,
                IsEnabled = true,
                IsToday = next.LocalDateTime.Date == DateTime.Today,
                CompletedAt = now,
                ResultSummary = summary,
                ResultPath = resultPath,
                Activity = activity
            };
        }

        return task with
        {
            Status = TaskProgressStatus.Done,
            IsEnabled = false,
            CompletedAt = now,
            ResultSummary = summary,
            ResultPath = resultPath,
            Activity = activity
        };
    }

    public static Reminder AddActivity(Reminder task, string kind, string summary, string? path = null)
    {
        var activity = (task.Activity ?? []).Append(new TaskActivity(
            Guid.NewGuid().ToString("N"), DateTimeOffset.Now, kind, summary, path)).TakeLast(100).ToArray();
        return task with { Activity = activity };
    }

    public static DateTimeOffset NextOccurrence(Reminder task, DateTimeOffset now)
    {
        var seed = task.HasSchedule && task.DueAt != DateTimeOffset.MaxValue ? task.DueAt : now;
        var next = task.Recurrence switch
        {
            RecurrenceKind.Weekdays => seed.AddDays(1),
            RecurrenceKind.Weekly => seed.AddDays(7),
            RecurrenceKind.Monthly => seed.AddMonths(1),
            _ => seed.AddDays(1)
        };
        while (next <= now)
        {
            next = task.Recurrence switch
            {
                RecurrenceKind.Weekly => next.AddDays(7),
                RecurrenceKind.Monthly => next.AddMonths(1),
                _ => next.AddDays(1)
            };
        }
        if (task.Recurrence == RecurrenceKind.Weekdays)
            while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.AddDays(1);
        return next;
    }

    public static string? ExtractExistingPath(string text)
    {
        foreach (Match match in WindowsPath().Matches(text))
        {
            var candidate = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '\'', '"');
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    [GeneratedRegex(@"[A-Za-z]:\\[^\r\n<>|*?]+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();
}
