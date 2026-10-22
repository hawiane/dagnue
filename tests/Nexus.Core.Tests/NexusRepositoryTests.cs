using Nexus.Core.Data;
using Nexus.Core.Models;
using System.Text.Json;

namespace Nexus.Core.Tests;

public sealed class NexusRepositoryTests
{
    [Fact]
    public void LegacyReminderJson_DefaultsToScheduledNotYetTask()
    {
        const string json = """
            {"Id":"legacy","Title":"Old reminder","Notes":null,"DueAt":"2026-08-24T09:00:00+08:00","Recurrence":0,"IsEnabled":true,"CreatedAt":"2026-08-23T09:00:00+08:00","LastTriggeredAt":null}
            """;
        var reminder = JsonSerializer.Deserialize<Reminder>(json);
        Assert.NotNull(reminder);
        Assert.True(reminder.HasSchedule);
        Assert.Equal(TaskProgressStatus.NotYet, reminder.Status);
        Assert.Equal(TaskUrgency.Normal, reminder.Urgency);
    }

    [Fact]
    public async Task SettingsAndReminder_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var repository = new NexusRepository(path);
            await repository.InitializeAsync();
            var settings = new AssistantSettings(AssistantName: "ORBIT", ReducedMotion: true);
            await repository.SaveSettingsAsync(settings);
            Assert.Equal(settings, await repository.GetSettingsAsync());

            var completed = DateTimeOffset.Now;
            var reminder = new Reminder("r1", "Meeting", "Prepare notes", DateTimeOffset.Now.AddHours(1),
                RecurrenceKind.Weekdays, true, DateTimeOffset.Now,
                TaskType: UserTaskType.Recurring, ExecutionMode: TaskExecutionMode.AiAssisted,
                CompletedAt: completed, ResultSummary: "Prepared the notes", ResultPath: @"C:\Work\notes.docx",
                AiThreadId: "thread-task-1", AiSuggestion: "Email the notes next",
                Activity: [new TaskActivity("a1", completed, "ai-completion", "Prepared the notes", @"C:\Work\notes.docx")]);
            await repository.SaveReminderAsync(reminder);
            var loaded = Assert.Single(await repository.GetRemindersAsync());
            Assert.Equal(reminder.Id, loaded.Id);
            Assert.Equal(reminder.Title, loaded.Title);
            Assert.Equal(UserTaskType.Recurring, loaded.TaskType);
            Assert.Equal(TaskExecutionMode.AiAssisted, loaded.ExecutionMode);
            Assert.Equal("Prepared the notes", loaded.ResultSummary);
            Assert.Equal(@"C:\Work\notes.docx", loaded.ResultPath);
            Assert.Equal("Email the notes next", loaded.AiSuggestion);
            Assert.Equal("ai-completion", Assert.Single(loaded.Activity!).Kind);
        }
        finally
        {
            SqliteConnectionClear(path);
        }
    }

    [Fact]
    public async Task TaskWorkspace_RoundTripsWithIndependentThreadAndForm()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-task-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var repository = new NexusRepository(path);
            await repository.InitializeAsync();
            var now = DateTimeOffset.Now;
            var workspace = new TaskWorkspace("task-1", "Trip planner", "Build a checklist", "thread-7",
                "{\"message\":\"Ready\",\"controls\":[{\"type\":\"checkbox\",\"label\":\"Passport\"}]}",
                "ready", now, now);
            await repository.SaveTaskWorkspaceAsync(workspace);
            var loaded = Assert.Single(await repository.GetTaskWorkspacesAsync());
            Assert.Equal(workspace, loaded);
            var command = new TaskCommandHistory("command-1", workspace.Id, "Build the checklist", now,
                now.AddSeconds(2), "completed", "Checklist updated");
            await repository.SaveTaskCommandAsync(command);
            Assert.Equal(command, Assert.Single(await repository.GetTaskCommandsAsync(workspace.Id)));
            var schedule = new TaskSchedule("schedule-1", workspace.Id, "Daily brief", "Create the brief",
                now.AddHours(2), RecurrenceKind.Daily, true, now);
            await repository.SaveTaskScheduleAsync(schedule);
            Assert.Equal(schedule, Assert.Single(await repository.GetTaskSchedulesAsync(workspace.Id)));
            await repository.DeleteTaskWorkspaceAsync(workspace.Id);
            Assert.Empty(await repository.GetTaskWorkspacesAsync());
            Assert.Empty(await repository.GetTaskCommandsAsync(workspace.Id));
            Assert.Empty(await repository.GetTaskSchedulesAsync(workspace.Id));
        }
        finally
        {
            SqliteConnectionClear(path);
        }
    }

    [Fact]
    public async Task CapabilityProfileAndAudit_RoundTripWithoutSecretMaterial()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexus-capability-test-{Guid.NewGuid():N}.db");
        try
        {
            await using var repository = new NexusRepository(path);
            await repository.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var profile = new CapabilityProfile("db-1", "HRIS read only", CapabilityKind.Database,
                true, true, CapabilityPermission.AutomaticRead, "hris-service", "HRIS", "readonly_user",
                DatabaseProvider.Oracle, "EMPLOYEE_BIRTHDAYS", "DAGNUE/capability/db-1", now, now);
            await repository.SaveCapabilityProfileAsync(profile);
            Assert.Equal(profile, Assert.Single(await repository.GetCapabilityProfilesAsync()));
            Assert.Equal(profile, await repository.GetCapabilityProfileAsync(profile.Id));

            var audit = new CapabilityAuditEntry("audit-1", now, "task-1", profile.Id,
                "database.query", "completed", ActionRisk.ReadOnly, "2 rows returned");
            await repository.AddCapabilityAuditAsync(audit);
            Assert.Equal(audit, Assert.Single(await repository.GetRecentCapabilityAuditAsync()));

            await repository.DeleteCapabilityProfileAsync(profile.Id);
            Assert.Empty(await repository.GetCapabilityProfilesAsync());
        }
        finally { SqliteConnectionClear(path); }
    }

    private static void SqliteConnectionClear(string path)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
