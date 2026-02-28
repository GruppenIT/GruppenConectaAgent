using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Lightweight helper process that runs in the interactive user session.
/// Handles screen capture (via capture pipe) and input simulation (via input pipe).
///
/// Capture pipe (bidirectional):
///   Service  → Helper : [1 byte quality (1-100)]
///   Helper   → Service: [4 bytes BE length][N bytes JPEG data]
///
/// Input pipe (one-way, service → helper):
///   [1 byte type (1=mouse, 2=key)][4 bytes BE length][N bytes JSON]
/// </summary>
internal static class CaptureHelper
{
    private const string LogPath = @"C:\ProgramData\Gruppen\RemoteAgent\logs\capture-helper.log";

    // -----------------------------------------------------------------------
    // Win32 imports
    // -----------------------------------------------------------------------
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT_RECORD[] pInputs, int cbSize);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // -----------------------------------------------------------------------
    // INPUT structures (shared for mouse and keyboard)
    // -----------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT_RECORD
    {
        public uint Type;
        public INPUT_UNION U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)] public MOUSEINPUT Mi;
        [FieldOffset(0)] public KEYBDINPUT Ki;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort WVk;
        public ushort WScan;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    // Mouse flags
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    // Keyboard flags
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    // Input pipe message types
    private const byte INPUT_TYPE_MOUSE = 1;
    private const byte INPUT_TYPE_KEY = 2;

    // -----------------------------------------------------------------------
    // JSON models (local copies to avoid depending on Protocol namespace)
    // -----------------------------------------------------------------------
    private class MouseEvent
    {
        [JsonPropertyName("x")] public int X { get; set; }
        [JsonPropertyName("y")] public int Y { get; set; }
        [JsonPropertyName("button")] public int Button { get; set; }
        [JsonPropertyName("action")] public string Action { get; set; } = "move";
    }

    private class KeyEvent
    {
        [JsonPropertyName("key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("action")] public string Action { get; set; } = "down";
        [JsonPropertyName("modifiers")] public string[] Modifiers { get; set; } = Array.Empty<string>();
    }

    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------
    public static void Run(string capturePipeName, string? inputPipeName = null)
    {
        try
        {
            Log($"Capture helper starting, capture={capturePipeName}, input={inputPipeName ?? "(none)"}");

            using var capturePipe = new NamedPipeClientStream(".", capturePipeName,
                PipeDirection.InOut, PipeOptions.None);
            capturePipe.Connect(10_000);

            NamedPipeClientStream? inputPipe = null;
            Thread? inputThread = null;

            if (inputPipeName is not null)
            {
                inputPipe = new NamedPipeClientStream(".", inputPipeName,
                    PipeDirection.In, PipeOptions.None);
                inputPipe.Connect(10_000);
                Log("Connected to both pipes.");

                // Start background thread for input events
                inputThread = new Thread(() => InputLoop(inputPipe))
                {
                    IsBackground = true,
                    Name = "InputPipeReader"
                };
                inputThread.Start();
            }
            else
            {
                Log("Connected to capture pipe (no input pipe).");
            }

            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .First(e => e.FormatID == ImageFormat.Jpeg.Guid);

            while (capturePipe.IsConnected)
            {
                int quality = capturePipe.ReadByte();
                if (quality < 0) break; // pipe closed

                int w = GetSystemMetrics(SM_CXSCREEN);
                int h = GetSystemMetrics(SM_CYSCREEN);
                if (w <= 0 || h <= 0) { w = 1920; h = 1080; }

                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                }

                byte[] jpeg;
                using (var ms = new MemoryStream())
                {
                    var ep = new EncoderParameters(1)
                    {
                        Param = { [0] = new EncoderParameter(Encoder.Quality, (long)quality) }
                    };
                    bmp.Save(ms, jpegEncoder, ep);
                    jpeg = ms.ToArray();
                }

                // Write length (4 bytes BE) + JPEG data
                byte[] lenBuf =
                {
                    (byte)(jpeg.Length >> 24),
                    (byte)(jpeg.Length >> 16),
                    (byte)(jpeg.Length >> 8),
                    (byte)jpeg.Length
                };
                capturePipe.Write(lenBuf, 0, 4);
                capturePipe.Write(jpeg, 0, jpeg.Length);
                capturePipe.Flush();
            }

            Log("Capture pipe disconnected, exiting.");
            inputPipe?.Dispose();
        }
        catch (Exception ex)
        {
            Log($"FATAL: {ex}");
        }
    }

    // -----------------------------------------------------------------------
    // Input pipe reader loop
    // -----------------------------------------------------------------------
    private static void InputLoop(NamedPipeClientStream pipe)
    {
        try
        {
            byte[] header = new byte[5];

            while (pipe.IsConnected)
            {
                int b = pipe.ReadByte();
                if (b < 0) break;

                header[0] = (byte)b;
                ReadExactly(pipe, header, 1, 4);

                int len = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
                if (len <= 0 || len > 64 * 1024)
                {
                    Log($"Invalid input payload length: {len}");
                    break;
                }

                byte[] json = new byte[len];
                ReadExactly(pipe, json, 0, len);

                byte type = header[0];
                switch (type)
                {
                    case INPUT_TYPE_MOUSE:
                        var mouse = JsonSerializer.Deserialize<MouseEvent>(json);
                        if (mouse is not null) HandleMouse(mouse);
                        break;

                    case INPUT_TYPE_KEY:
                        var key = JsonSerializer.Deserialize<KeyEvent>(json);
                        if (key is not null) HandleKey(key);
                        break;

                    default:
                        Log($"Unknown input type: {type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Input loop ended: {ex.Message}");
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) throw new EndOfStreamException("Pipe closed while reading.");
            totalRead += read;
        }
    }

    // -----------------------------------------------------------------------
    // Mouse input handling
    // -----------------------------------------------------------------------
    private static void HandleMouse(MouseEvent evt)
    {
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

    private static void SendMouseInput(int absX, int absY, uint flags)
    {
        var inputs = new INPUT_RECORD[]
        {
            new()
            {
                Type = INPUT_MOUSE,
                U = new INPUT_UNION
                {
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
            }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT_RECORD>());
    }

    // -----------------------------------------------------------------------
    // Keyboard input handling
    // -----------------------------------------------------------------------
    private static void HandleKey(KeyEvent evt)
    {
        bool isDown = evt.Action == "down";

        if (isDown)
        {
            foreach (var mod in evt.Modifiers)
                SendKey(MapModifierToVk(mod), true);
        }

        ushort vk = MapJsKeyToVk(evt.Key);
        if (vk != 0)
            SendKey(vk, isDown);

        if (!isDown)
        {
            foreach (var mod in evt.Modifiers)
                SendKey(MapModifierToVk(mod), false);
        }
    }

    private static void SendKey(ushort vk, bool down)
    {
        if (vk == 0) return;

        var inputs = new INPUT_RECORD[]
        {
            new()
            {
                Type = INPUT_KEYBOARD,
                U = new INPUT_UNION
                {
                    Ki = new KEYBDINPUT
                    {
                        WVk = vk,
                        WScan = 0,
                        DwFlags = down ? 0u : KEYEVENTF_KEYUP,
                        Time = 0,
                        DwExtraInfo = IntPtr.Zero
                    }
                }
            }
        };
        SendInput(1, inputs, Marshal.SizeOf<INPUT_RECORD>());
    }

    private static ushort MapModifierToVk(string modifier) => modifier.ToLowerInvariant() switch
    {
        "ctrl" => 0x11,
        "alt" => 0x12,
        "shift" => 0x10,
        "meta" => 0x5B,
        _ => 0
    };

    private static ushort MapJsKeyToVk(string key)
    {
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return (ushort)c;
            if (c is >= '0' and <= '9') return (ushort)c;
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
            "control" or "ctrl" => 0x11,
            "alt" => 0x12,
            "shift" => 0x10,
            "meta" => 0x5B,
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
            "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
            "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            "capslock" => 0x14,
            "numlock" => 0x90,
            "scrolllock" => 0x91,
            "printscreen" => 0x2C,
            "pause" => 0x13,
            "contextmenu" => 0x5D,
            _ => 0
        };
    }

    // -----------------------------------------------------------------------
    // Logging
    // -----------------------------------------------------------------------
    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [CaptureHelper] {message}{Environment.NewLine}");
        }
        catch { /* best effort */ }
    }
}
