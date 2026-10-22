using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Nexus.App.Services;

public sealed class DesktopCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    public Task<string> CaptureAsync(string resultsDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(resultsDirectory);
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0) throw new InvalidOperationException("The interactive desktop is unavailable.");
            var path = Path.Combine(resultsDirectory, $"desktop-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }, cancellationToken);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
