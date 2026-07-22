using Vortice.MediaFoundation;

namespace Recorder.Encoding;

/// <summary>
/// Reference-counted MFStartup/MFShutdown wrapper. Media Foundation must be started
/// exactly once per process before any MF call and shut down after the last user is
/// gone; ref-counting lets multiple concurrent recordings (or future replay buffers)
/// share the runtime safely.
/// </summary>
public static class MediaFoundationRuntime
{
    private static readonly object Gate = new();
    private static int _referenceCount;

    public static void AddRef()
    {
        lock (Gate)
        {
            if (_referenceCount == 0)
            {
                MediaFactory.MFStartup(useLightVersion: true).CheckError();
            }
            _referenceCount++;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            _referenceCount--;
            if (_referenceCount == 0)
            {
                MediaFactory.MFShutdown();
            }
        }
    }
}
