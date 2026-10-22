using Nexus.Core.Models;

namespace Nexus.Core.Briefings;

public sealed class BriefingService
{
    public DailyBriefing Create(IEnumerable<Reminder> reminders, SystemSnapshot snapshot, DateTimeOffset now)
    {
        var upcoming = reminders.Where(x => x.IsEnabled && x.DueAt >= now && x.DueAt <= now.AddDays(1))
            .OrderBy(x => x.DueAt).Take(5).ToArray();
        var greeting = now.Hour switch
        {
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };
        var notices = new List<string>();
        if (!snapshot.NetworkAvailable) notices.Add("Network connection is unavailable.");
        if (snapshot.MemoryPercent >= 85) notices.Add("Memory usage is high.");
        if (snapshot.DiskPercent >= 90) notices.Add("System drive space is running low.");
        if (!snapshot.CodexConnected) notices.Add("Codex is offline; local tools remain available.");
        var summary = upcoming.Length == 0
            ? "Your next 24 hours are clear."
            : $"You have {upcoming.Length} upcoming reminder{(upcoming.Length == 1 ? "" : "s")} in the next 24 hours.";
        return new DailyBriefing(greeting, summary, upcoming, snapshot, notices);
    }
}
