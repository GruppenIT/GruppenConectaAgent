namespace GruppenRemoteAgent.Capture;

public interface IScreenCapture : IDisposable
{
    byte[] CaptureScreen(int jpegQuality);
    bool IsAvailable();
}
