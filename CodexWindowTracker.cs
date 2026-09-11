using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CodexQuotaWidget;

internal static class CodexWindowTracker
{
    private const int GwlExStyle = -20;
    private const int GwlpHwndParent = -8;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int DwmwaCloaked = 14;

    public static nint ResolveHostWindow(nint cached)
    {
        if (cached != nint.Zero && IsWindow(cached) && IsHostCandidate(cached))
        {
            return cached;
        }

        var foreground = GetForegroundWindow();
        if (foreground != nint.Zero && IsHostCandidate(foreground))
        {
            return foreground;
        }

        nint best = nint.Zero;
        long bestArea = 0;
        EnumWindows((window, _) =>
        {
            if (!IsHostCandidate(window) || !GetWindowRect(window, out var rect))
            {
                return true;
            }

            var area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                best = window;
            }

            return true;
        }, nint.Zero);
        return best;
    }

    public static bool DockBadge(
        nint badgeWindow,
        nint hostWindow,
        double logicalWidth,
        double logicalHeight,
        int horizontalOffset,
        int verticalOffset)
    {
        if (!GetWindowRect(hostWindow, out var host))
        {
            return false;
        }

        var dpi = GetDpiForWindow(hostWindow);
        var scale = dpi > 0 ? dpi / 96d : 1d;
        var width = Math.Max(1, (int)Math.Round(logicalWidth * scale));
        var height = Math.Max(1, (int)Math.Round(logicalHeight * scale));

        // Leave the native minimize/maximize/close cluster untouched and sit in the free title-bar area.
        var systemButtonsWidth = (int)Math.Round(146 * scale);
        var rightGap = (int)Math.Round(12 * scale);
        // Codex Desktop uses a compact custom title bar.  The previous 48px
        // assumption placed a 28px badge several pixels into the toolbar below.
        var titleBarHeight = (int)Math.Round(36 * scale);
        var x = host.Right - systemButtonsWidth - rightGap - width + (int)Math.Round(horizontalOffset * scale);
        var y = host.Top + Math.Max(3, (titleBarHeight - height) / 2) + (int)Math.Round(verticalOffset * scale);

        return SetWindowPos(
            badgeWindow,
            nint.Zero,
            x,
            y,
            width,
            height,
            SwpNoActivate | SwpShowWindow | SwpNoOwnerZOrder);
    }

    public static void MakeNoActivateToolWindow(nint window)
    {
        var style = GetWindowLongPtr(window, GwlExStyle).ToInt64();
        SetWindowLongPtr(window, GwlExStyle, new nint(style | WsExToolWindow | WsExNoActivate));
    }

    public static void SetOwnedWindow(nint window, nint owner) =>
        SetWindowLongPtr(window, GwlpHwndParent, owner);

    public static bool IsMinimized(nint window) => IsIconic(window);

    public static bool TryGetRect(nint window, out WindowRect? result)
    {
        result = null;
        if (window == nint.Zero || !GetWindowRect(window, out var rect))
        {
            return false;
        }

        result = new WindowRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
        return true;
    }

    private static bool IsHostCandidate(nint window)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || IsCloaked(window))
        {
            return false;
        }

        var className = new StringBuilder(128);
        if (GetClassName(window, className, className.Capacity) == 0 ||
            !className.ToString().StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal))
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            return fileName.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCloaked(nint window)
    {
        var cloaked = 0;
        return DwmGetWindowAttribute(window, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    public sealed record WindowRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint window, int index, int value);

    private static nint GetWindowLongPtr(nint window, int index) =>
        nint.Size == 8 ? GetWindowLongPtr64(window, index) : new nint(GetWindowLong32(window, index));

    private static nint SetWindowLongPtr(nint window, int index, nint value) =>
        nint.Size == 8 ? SetWindowLongPtr64(window, index, value) : new nint(SetWindowLong32(window, index, value.ToInt32()));

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
