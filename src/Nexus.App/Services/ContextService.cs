using System.Runtime.InteropServices;
using System.Text;
using Windows.ApplicationModel.DataTransfer;

namespace Nexus.App.Services;

public sealed class ContextService
{
    public async Task<string> CaptureExplicitContextAsync(bool includeClipboard)
    {
        var sections = new List<string>();
        var title = GetForegroundWindowTitle();
        if (!string.IsNullOrWhiteSpace(title)) sections.Add($"Active window: {title}");
        if (includeClipboard)
        {
            try
            {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text))
                {
                    var text = await content.GetTextAsync();
                    if (!string.IsNullOrWhiteSpace(text))
                        sections.Add($"Clipboard text:\n{text[..Math.Min(text.Length, 4000)]}");
                }
            }
            catch { }
        }
        return string.Join("\n\n", sections);
    }

    private static string GetForegroundWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        var builder = new StringBuilder(length + 1);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(nint hwnd);
}
