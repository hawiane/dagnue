using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Nexus.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const int WmApp = 0x8000;
    private const int CallbackMessage = WmApp + 42;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmRButtonUp = 0x0205;
    private const int GwlpWndProc = -4;
    private const uint NimAdd = 0x00000000;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmReturnCmd = 0x0100;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;

    private readonly nint _hwnd;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly WindowProc _windowProc;
    private readonly nint _originalProc;
    private NotifyIconData _data;

    public TrayIconService(nint hwnd, DispatcherQueue dispatcher, Action show, Action exit)
    {
        _hwnd = hwnd;
        _dispatcher = dispatcher;
        _show = show;
        _exit = exit;
        _windowProc = WindowProcedure;
        _originalProc = SetWindowLongPtr(hwnd, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_windowProc));
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var icon = LoadImage(nint.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile);
        _data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = hwnd,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = icon,
            Tip = "DAGNUE Desktop Assistant"
        };
        ShellNotifyIcon(NimAdd, ref _data);
    }

    public bool IsPaused { get; private set; }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            var mouseMessage = unchecked((int)lParam.ToInt64());
            if (mouseMessage == WmLButtonDoubleClick) _dispatcher.TryEnqueue(() => _show());
            else if (mouseMessage == WmRButtonUp) ShowMenu();
            return nint.Zero;
        }
        return CallWindowProc(_originalProc, hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, 1, "Open DAGNUE");
        AppendMenu(menu, MfString, 2, IsPaused ? "Resume notifications" : "Pause notifications");
        AppendMenu(menu, MfSeparator, 0, string.Empty);
        AppendMenu(menu, MfString, 3, "Exit");
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenu(menu, TpmReturnCmd, point.X, point.Y, 0, _hwnd, nint.Zero);
        DestroyMenu(menu);
        switch (command)
        {
            case 1: _dispatcher.TryEnqueue(() => _show()); break;
            case 2: IsPaused = !IsPaused; break;
            case 3: _dispatcher.TryEnqueue(() => _exit()); break;
        }
    }

    public void Dispose()
    {
        ShellNotifyIcon(NimDelete, ref _data);
        if (_originalProc != nint.Zero) SetWindowLongPtr(_hwnd, GwlpWndProc, _originalProc);
        if (_data.IconHandle != nint.Zero) DestroyIcon(_data.IconHandle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newProc);
    [DllImport("user32.dll")] private static extern nint CallWindowProc(nint previous, nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
