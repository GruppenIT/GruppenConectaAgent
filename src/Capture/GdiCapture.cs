using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GruppenRemoteAgent.Capture;

/// <summary>
/// Fallback screen capture using GDI+ (Graphics.CopyFromScreen).
/// Works on all Windows versions but is slower than DXGI.
/// </summary>
public class GdiCapture : IScreenCapture
{
    private readonly ILogger _logger;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    public GdiCapture(ILogger<GdiCapture> logger)
    {
        _logger = logger;
        _logger.LogInformation("GDI+ capture initialized as fallback.");
    }

    public bool IsAvailable() => true;

    public byte[] CaptureScreen(int jpegQuality)
    {
        int width = GetSystemMetrics(SM_CXSCREEN);
        int height = GetSystemMetrics(SM_CYSCREEN);

        if (width <= 0 || height <= 0)
        {
            width = 1920;
            height = 1080;
            _logger.LogWarning("Could not detect screen size, defaulting to {W}x{H}.", width, height);
        }

        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        return EncodeJpeg(bmp, jpegQuality);
    }

    private static byte[] EncodeJpeg(Bitmap bmp, int quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        var encoderParams = new EncoderParameters(1)
        {
            Param = { [0] = new EncoderParameter(Encoder.Quality, (long)quality) }
        };

        using var ms = new MemoryStream();
        bmp.Save(ms, encoder, encoderParams);
        return ms.ToArray();
    }

    public void Dispose()
    {
        // No resources to release for GDI+
    }
}
