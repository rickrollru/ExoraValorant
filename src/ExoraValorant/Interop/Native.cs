using System.Runtime.InteropServices;

namespace Exora;

internal static partial class Native
{
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int HTCAPTION = 2;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int SW_RESTORE = 9;

    // Hit-test codes for resizing a borderless window from the web content.
    public static readonly Dictionary<string, int> ResizeHitTest = new()
    {
        ["w"] = 10, ["e"] = 11, ["n"] = 12, ["nw"] = 13, ["ne"] = 14, ["s"] = 15, ["sw"] = 16, ["se"] = 17,
    };

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    public static partial IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string? className, string windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    public static void StartWindowAction(IntPtr handle, int hitTest)
    {
        ReleaseCapture();
        SendMessage(handle, WM_NCLBUTTONDOWN, hitTest, IntPtr.Zero);
    }

    // Rounded corners and a dark frame on Windows 11; silently ignored on Windows 10.
    public static void StyleWindow(IntPtr handle, bool dark)
    {
        try
        {
            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
            var darkMode = dark ? 1 : 0;
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
        catch
        {
        }
    }

    public static void FocusExistingWindow(string title)
    {
        var hwnd = FindWindow(null, title);
        if (hwnd == IntPtr.Zero) return;
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }
}
