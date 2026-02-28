using System.Runtime.InteropServices;
using GruppenRemoteAgent.Protocol;

namespace GruppenRemoteAgent.Session;

/// <summary>
/// Enumerates Windows Terminal Services sessions via WTS API.
/// </summary>
internal static class WtsSessionManager
{
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr hServer, int reserved, int version,
        out IntPtr ppSessionInfo, out int pCount);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr hServer, int sessionId, int wtsInfoClass,
        out IntPtr ppBuffer, out int pBytesReturned);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    private const int WTSUserName = 5;

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public int State;
    }

    public static SessionInfoDto[] GetSessions()
    {
        var sessions = new List<SessionInfoDto>();

        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var pSessions, out var count))
            return sessions.ToArray();

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (int i = 0; i < count; i++)
            {
                var si = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessions + i * size);

                // Skip Session 0 (services)
                if (si.SessionId == 0) continue;

                string username = GetSessionUser((int)si.SessionId);
                if (string.IsNullOrEmpty(username)) continue;

                string stationName = Marshal.PtrToStringUni(si.pWinStationName) ?? "";

                string state = si.State switch
                {
                    0 => "Active",
                    1 => "Connected",
                    4 => "Disconnected",
                    _ => "Unknown"
                };

                sessions.Add(new SessionInfoDto
                {
                    Id = (int)si.SessionId,
                    Username = username,
                    State = state,
                    IsConsole = stationName.Equals("Console", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        finally
        {
            WTSFreeMemory(pSessions);
        }

        return sessions.ToArray();
    }

    private static string GetSessionUser(int sessionId)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WTSUserName,
                out var buf, out _))
            return string.Empty;

        try
        {
            return Marshal.PtrToStringUni(buf) ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buf);
        }
    }
}
