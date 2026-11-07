using System.Runtime.InteropServices;

namespace HaPcBridge.Service.Native;

internal static class Win32
{
    public static readonly IntPtr WTS_CURRENT_SERVER_HANDLE = IntPtr.Zero;
    public const uint INVALID_SESSION = 0xFFFFFFFF;

    public enum WTS_INFO_CLASS
    {
        WTSUserName = 5,
        WTSDomainName = 7,
        WTSConnectState = 8,
        WTSSessionInfo = 24,
    }

    public enum WTS_CONNECTSTATE_CLASS
    {
        Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTSINFOW
    {
        public WTS_CONNECTSTATE_CLASS State;
        public int SessionId;
        public int IncomingBytes;
        public int OutgoingBytes;
        public int IncomingFrames;
        public int OutgoingFrames;
        public int IncomingCompressedBytes;
        public int OutgoingCompressedBytes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 17)] public string Domain;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string UserName;
        public long ConnectTime;
        public long DisconnectTime;
        public long LastInputTime;
        public long LogonTime;
        public long CurrentTime;
    }

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool WTSQuerySessionInformationW(IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass,
        out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSLogoffSession(IntPtr hServer, uint sessionId, bool wait);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUserW(IntPtr token, string? applicationName, string? commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
        IntPtr environment, string? currentDirectory, ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("powrprof.dll", SetLastError = true)]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    // --- helpers ---

    public static string? QueryString(uint session, WTS_INFO_CLASS cls)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { WTSFreeMemory(buf); }
    }

    public static WTSINFOW? QuerySessionInfo(uint session)
    {
        if (!WTSQuerySessionInformationW(WTS_CURRENT_SERVER_HANDLE, session, WTS_INFO_CLASS.WTSSessionInfo, out var buf, out _)) return null;
        try { return Marshal.PtrToStructure<WTSINFOW>(buf); }
        finally { WTSFreeMemory(buf); }
    }
}
