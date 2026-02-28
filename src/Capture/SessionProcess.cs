using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Launches a process in the interactive (console) user session from Session 0.
/// Requires the calling process to run as LocalSystem (which Windows services do).
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

    /// <summary>
    /// Returns true if the current process is running in Session 0 (service session).
    /// </summary>
    public static bool IsSession0()
    {
        ProcessIdToSessionId(GetCurrentProcessId(), out uint sessionId);
        return sessionId == 0;
    }

    /// <summary>
    /// Launches a command in the active console user session.
    /// </summary>
    public static int LaunchInUserSession(string commandLine, ILogger logger)
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == 0xFFFFFFFF)
            throw new InvalidOperationException("No active console session found.");

        logger.LogInformation("Launching capture helper in session {Session}...", sessionId);

        if (!WTSQueryUserToken(sessionId, out IntPtr userToken))
        {
            int err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"WTSQueryUserToken failed for session {sessionId} (Win32 error {err}). Is a user logged in?");
        }

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
