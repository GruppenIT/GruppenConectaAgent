using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Lightweight capture process that runs in the interactive user session.
/// Connects to a named pipe created by the service, waits for quality requests,
/// captures the screen, and sends JPEG frames back.
///
/// Protocol:
///   Service  → Helper : [1 byte quality (1-100)]
///   Helper   → Service: [4 bytes BE length][N bytes JPEG data]
/// </summary>
internal static class CaptureHelper
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public static void Run(string pipeName)
    {
        var logFile = @"C:\ProgramData\Gruppen\RemoteAgent\logs\capture-helper.log";
        try
        {
            Log(logFile, $"Capture helper starting, pipe={pipeName}");

            using var pipe = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.None);

            pipe.Connect(10_000); // 10 s timeout
            Log(logFile, "Connected to service pipe.");

            var jpegEncoder = ImageCodecInfo.GetImageEncoders()
                .First(e => e.FormatID == ImageFormat.Jpeg.Guid);

            while (pipe.IsConnected)
            {
                int quality = pipe.ReadByte();
                if (quality < 0) break; // pipe closed

                int w = GetSystemMetrics(0); // SM_CXSCREEN
                int h = GetSystemMetrics(1); // SM_CYSCREEN
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
                pipe.Write(lenBuf, 0, 4);
                pipe.Write(jpeg, 0, jpeg.Length);
                pipe.Flush();
            }

            Log(logFile, "Pipe disconnected, exiting.");
        }
        catch (Exception ex)
        {
            Log(logFile, $"FATAL: {ex}");
        }
    }

    private static void Log(string path, string message)
    {
        try
        {
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [CaptureHelper] {message}{Environment.NewLine}");
        }
        catch { /* best effort */ }
    }
}
