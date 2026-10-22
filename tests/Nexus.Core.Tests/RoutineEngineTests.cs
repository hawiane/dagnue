using Nexus.Core.Models;
using Nexus.Core.Scheduling;

namespace Nexus.Core.Tests;

public sealed class RoutineEngineTests
{
    private readonly RoutineEngine _engine = new();

    [Theory]
    [InlineData("daily:09:00")]
    [InlineData("weekdays:08:30")]
    [InlineData("Monday:17:15")]
    public void SupportedTrigger_IsValid(string trigger) => Assert.True(_engine.IsValidTrigger(trigger));

    [Theory]
    [InlineData("every morning")]
    [InlineData("daily:not-a-time")]
    [InlineData("unknown:09:00")]
    public void UnsupportedTrigger_IsInvalid(string trigger) => Assert.False(_engine.IsValidTrigger(trigger));

    [Fact]
    public void WeekdayRoutine_DoesNotRunOnWeekend()
    {
        var saturday = new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.FromHours(8));
        var routine = new Routine("1", "Briefing", "weekdays:09:00", "Prepare briefing", true, false, saturday.AddDays(-2));
        Assert.False(_engine.IsDue(routine, saturday));
    }

    [Fact]
    public void Routine_RunsOnlyOncePerDay()
    {
        var now = new DateTimeOffset(2026, 8, 3, 9, 0, 30, TimeSpan.FromHours(8));
        var routine = new Routine("1", "Briefing", "daily:09:00", "Prepare briefing", true, false, now.AddDays(-1));
        Assert.True(_engine.IsDue(routine, now));
        Assert.False(_engine.IsDue(_engine.MarkRun(routine, now), now));
    }
}
