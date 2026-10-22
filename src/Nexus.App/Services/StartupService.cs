using Windows.ApplicationModel;

namespace Nexus.App.Services;

public sealed class StartupService
{
    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync("NexusStartup");
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch { return false; }
    }

    public async Task<bool> SetEnabledAsync(bool enabled)
    {
        try
        {
            var task = await StartupTask.GetAsync("NexusStartup");
            if (enabled)
            {
                var state = await task.RequestEnableAsync();
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            task.Disable();
            return false;
        }
        catch { return false; }
    }
}
