using System.Runtime.InteropServices;
using System.Text;

namespace HaPcBridge.Agent;

internal static class Native
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr hWnd, StringBuilder name, int count);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("kernel32.dll")] public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public const uint WM_CLOSE = 0x0010;
    public const uint WM_SYSCOMMAND = 0x0112;
    public static readonly IntPtr SC_MONITORPOWER = new(0xF170);
    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    public static string WindowTitle(IntPtr h)
    {
        var len = GetWindowTextLengthW(h);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        // Both values are 32-bit tick counts, unsigned subtraction handles the 49.7-day wrap.
        return unchecked((uint)Environment.TickCount - info.dwTime) / 1000.0;
    }
}
