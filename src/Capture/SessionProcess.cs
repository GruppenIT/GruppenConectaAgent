using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Launches a process in the interactive user session from Session 0.
/// Handles both physical console and RDP sessions by enumerating all
/// active sessions when the console session has no user token.
/// </summary>
internal static class SessionProcess
{
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr hToken, uint dwDesiredAccess, IntPtr lpTokenAttributes,
        int impersonationLevel, int tokenType, out IntPtr phNewToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(
        IntPtr hToken, string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll")]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int WTSActive = 0;

    public static bool IsSession0()
    {
        ProcessIdToSessionId(GetCurrentProcessId(), out uint sessionId);
        return sessionId == 0;
    }

    /// <summary>
    /// Finds a user session that has a valid token. Tries the console session
    /// first, then falls back to enumerating all active sessions (covers RDP).
    /// </summary>
    private static (uint sessionId, IntPtr token) GetUserSessionToken(ILogger logger)
    {
        // 1) Try the physical console session first
        uint consoleId = WTSGetActiveConsoleSessionId();
        if (consoleId != 0xFFFFFFFF && consoleId != 0)
        {
            if (WTSQueryUserToken(consoleId, out IntPtr token))
            {
                logger.LogInformation("Using console session {Session}.", consoleId);
                return (consoleId, token);
            }
            logger.LogDebug("Console session {Session} has no user token (error {Err}), trying other sessions...",
                consoleId, Marshal.GetLastWin32Error());
        }

        // 2) Enumerate all sessions and find an active one with a token
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr pSessions, out int count))
            throw new InvalidOperationException(
                $"WTSEnumerateSessions failed (Win32 error {Marshal.GetLastWin32Error()}).");

        try
        {
            int structSize = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessions + i * structSize);

                // Skip Session 0 and non-active sessions
                if (si.SessionId == 0 || si.State != WTSActive)
                    continue;

                if (WTSQueryUserToken(si.SessionId, out IntPtr token))
                {
                    logger.LogInformation("Using active session {Session}.", si.SessionId);
                    return (si.SessionId, token);
                }

                logger.LogDebug("Session {Session} active but no token (error {Err}).",
                    si.SessionId, Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            WTSFreeMemory(pSessions);
        }

        throw new InvalidOperationException("No active user session with a valid token found. Is a user logged in?");
    }

    public static int LaunchInUserSession(string commandLine, ILogger logger)
    {
        var (sessionId, userToken) = GetUserSessionToken(logger);

        try
        {
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, IntPtr.Zero,
                    SecurityImpersonation, TokenPrimary, out IntPtr dupToken))
                throw new InvalidOperationException(
                    $"DuplicateTokenEx failed (Win32 error {Marshal.GetLastWin32Error()}).");

            try
            {
                CreateEnvironmentBlock(out IntPtr envBlock, dupToken, false);

                var si = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = @"winsta0\default"
                };

                bool ok = CreateProcessAsUserW(
                    dupToken, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    envBlock, null, ref si, out var pi);

                if (envBlock != IntPtr.Zero)
                    DestroyEnvironmentBlock(envBlock);

                if (!ok)
                    throw new InvalidOperationException(
                        $"CreateProcessAsUser failed (Win32 error {Marshal.GetLastWin32Error()}).");

                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);

                logger.LogInformation("Capture helper started (PID {Pid}) in session {Session}.",
                    pi.dwProcessId, sessionId);
                return pi.dwProcessId;
            }
            finally { CloseHandle(dupToken); }
        }
        finally { CloseHandle(userToken); }
    }
}
