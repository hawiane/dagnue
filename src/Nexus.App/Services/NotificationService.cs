using System.Security;
using Microsoft.Windows.AppNotifications;

namespace Nexus.App.Services;

public sealed record NotificationHistoryItem(
    int Sequence,
    string Title,
    string Body,
    DateTimeOffset Timestamp,
    string? Argument);

public sealed class NotificationService : IDisposable
{
    private readonly object _historyGate = new();
    private readonly List<NotificationHistoryItem> _history = [];
    private bool _registered;
    private int _shownCount;

    public int ShownCount => Volatile.Read(ref _shownCount);
    public IReadOnlyList<NotificationHistoryItem> History
    {
        get { lock (_historyGate) return [.. _history]; }
    }
    public event Action<int>? NotificationShown;

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false;
        }
    }

    public void Show(string title, string body, string? argument = null)
    {
        var count = Interlocked.Increment(ref _shownCount);
        lock (_historyGate)
        {
            _history.Add(new NotificationHistoryItem(count, title, body, DateTimeOffset.Now, argument));
            if (_history.Count > 64) _history.RemoveRange(0, _history.Count - 64);
        }
        NotificationShown?.Invoke(count);
        if (!_registered) return;
        var safeTitle = SecurityElement.Escape(title) ?? string.Empty;
        var safeBody = SecurityElement.Escape(body) ?? string.Empty;
        var launch = SecurityElement.Escape(argument ?? "open") ?? "open";
        var xml = $"""
            <toast launch="{launch}">
              <visual><binding template="ToastGeneric">
                <text>{safeTitle}</text><text>{safeBody}</text>
              </binding></visual>
            </toast>
            """;
        AppNotificationManager.Default.Show(new AppNotification(xml));
    }

    public void Dispose()
    {
        if (_registered)
        {
            try { AppNotificationManager.Default.Unregister(); } catch { }
        }
    }
}
