using System.Runtime.InteropServices;
using GruppenRemoteAgent.Protocol;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Input;

public class KeyboardSimulator
{
    private readonly ILogger<KeyboardSimulator> _logger;

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public KEYBDINPUT Ki;
        // Padding to match union size in native INPUT struct
        private readonly long _padding1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    public KeyboardSimulator(ILogger<KeyboardSimulator> logger)
    {
        _logger = logger;
    }

    public void Simulate(KeyEvent evt)
    {
        _logger.LogDebug("Key: {Action} key={Key} modifiers=[{Mods}]",
            evt.Action, evt.Key, string.Join(",", evt.Modifiers));

        bool isDown = evt.Action == "down";

        // Press modifiers first on key down
        if (isDown)
        {
            foreach (var mod in evt.Modifiers)
                SendKey(MapModifierToVk(mod), true);
        }

        // Send the main key
        ushort vk = MapJsKeyToVk(evt.Key);
        if (vk != 0)
        {
            SendKey(vk, isDown);
        }
        else
        {
            _logger.LogWarning("Unknown key: {Key}", evt.Key);
        }

        // Release modifiers on key up
        if (!isDown)
        {
            foreach (var mod in evt.Modifiers)
                SendKey(MapModifierToVk(mod), false);
        }
    }

    private void SendKey(ushort vk, bool down)
    {
        var inputs = new INPUT[]
        {
            new()
            {
                Type = INPUT_KEYBOARD,
                Ki = new KEYBDINPUT
                {
                    WVk = vk,
                    WScan = 0,
                    DwFlags = down ? 0u : KEYEVENTF_KEYUP,
                    Time = 0,
                    DwExtraInfo = IntPtr.Zero
                }
            }
        };

        SendInput(1, inputs, Marshal.SizeOf<INPUT>());
    }

    private static ushort MapModifierToVk(string modifier) => modifier.ToLowerInvariant() switch
    {
        "ctrl" => 0x11,   // VK_CONTROL
        "alt" => 0x12,    // VK_MENU
        "shift" => 0x10,  // VK_SHIFT
        "meta" => 0x5B,   // VK_LWIN
        _ => 0
    };

    /// <summary>
    /// Maps JavaScript KeyboardEvent.key names to Windows Virtual Key codes.
    /// </summary>
    private static ushort MapJsKeyToVk(string key)
    {
        // Single character keys (letters and digits)
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z')
                return (ushort)c; // VK_A..VK_Z = 0x41..0x5A
            if (c is >= '0' and <= '9')
                return (ushort)c; // VK_0..VK_9 = 0x30..0x39
        }

        return key.ToLowerInvariant() switch
        {
            "enter" => 0x0D,
            "tab" => 0x09,
            "escape" => 0x1B,
            "backspace" => 0x08,
            "delete" => 0x2E,
            "insert" => 0x2D,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "arrowup" => 0x26,
            "arrowdown" => 0x28,
            "arrowleft" => 0x25,
            "arrowright" => 0x27,
            " " or "space" => 0x20,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            "capslock" => 0x14,
            "numlock" => 0x90,
            "scrolllock" => 0x91,
            "printscreen" => 0x2C,
            "pause" => 0x13,
            "contextmenu" => 0x5D,
            _ => 0
        };
    }
}
