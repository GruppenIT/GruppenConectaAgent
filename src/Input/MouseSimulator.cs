using System.Runtime.InteropServices;
using GruppenRemoteAgent.Protocol;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Input;

public class MouseSimulator
{
    private readonly ILogger<MouseSimulator> _logger;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    public MouseSimulator(ILogger<MouseSimulator> logger)
    {
        _logger = logger;
    }

    public void Simulate(MouseEvent evt)
    {
        _logger.LogDebug("Mouse: {Action} at ({X},{Y}) button={Button}", evt.Action, evt.X, evt.Y, evt.Button);

        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        if (screenW <= 0) screenW = 65535;
        if (screenH <= 0) screenH = 65535;

        int absX = (int)((evt.X * 65535.0) / screenW);
        int absY = (int)((evt.Y * 65535.0) / screenH);

        switch (evt.Action)
        {
            case "move":
                SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
                break;
            case "down":
                SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetButtonDown(evt.Button));
                break;
            case "up":
                SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetButtonUp(evt.Button));
                break;
            case "click":
                SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetButtonDown(evt.Button));
                SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetButtonUp(evt.Button));
                break;
            case "dblclick":
                for (int i = 0; i < 2; i++)
                {
                    SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetButtonDown(evt.Button));
                    SendMouseInput(absX, absY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | GetButtonUp(evt.Button));
                }
                break;
            default:
                _logger.LogWarning("Unknown mouse action: {Action}", evt.Action);
                break;
        }
    }

    private static uint GetButtonDown(int button) => button switch
    {
        0 => MOUSEEVENTF_LEFTDOWN,
        1 => MOUSEEVENTF_MIDDLEDOWN,
        2 => MOUSEEVENTF_RIGHTDOWN,
        _ => MOUSEEVENTF_LEFTDOWN
    };

    private static uint GetButtonUp(int button) => button switch
    {
        0 => MOUSEEVENTF_LEFTUP,
        1 => MOUSEEVENTF_MIDDLEUP,
        2 => MOUSEEVENTF_RIGHTUP,
        _ => MOUSEEVENTF_LEFTUP
    };

    private void SendMouseInput(int absX, int absY, uint flags)
    {
        var inputs = new INPUT[]
        {
            new()
            {
                Type = INPUT_MOUSE,
                Mi = new MOUSEINPUT
                {
                    Dx = absX,
                    Dy = absY,
                    DwFlags = flags,
                    MouseData = 0,
                    Time = 0,
                    DwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }
}
