using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Dagnue.Launcher;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint windowHandle, string text, string caption, uint type);

    [STAThread]
    private static void Main()
    {
        var workspace = AppContext.BaseDirectory;
        var application = Path.Combine(workspace, "artifacts", "portable", "Nexus.App.exe");
        var pendingUpdate = Path.Combine(workspace, ".generated", "pending-portable");
        if (!File.Exists(application))
        {
            MessageBox(0,
                "The DAGNUE portable build was not found. Run scripts\\build.ps1 once to regenerate it.",
                "DAGNUE launch error", 0x10);
            return;
        }

        try
        {
            ApplyPendingUpdate(pendingUpdate, Path.GetDirectoryName(application)!);
            Process.Start(new ProcessStartInfo
            {
                FileName = application,
                WorkingDirectory = Path.GetDirectoryName(application)!,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox(0, exception.Message, "DAGNUE could not start", 0x10);
        }
    }

    private static void ApplyPendingUpdate(string pendingDirectory, string applicationDirectory)
    {
        if (!Directory.Exists(pendingDirectory)) return;

        var running = Process.GetProcessesByName("Nexus.App");
        try
        {
            if (running.Any(process => !process.HasExited)) return;
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }

        foreach (var source in Directory.EnumerateFiles(pendingDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(pendingDirectory, source);
            var destination = Path.Combine(applicationDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        Directory.Delete(pendingDirectory, recursive: true);
    }
}
