using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com;

namespace DynamicIsland;

/// <summary>
/// Camera in use, from Media Foundation's sensor activity monitor (Windows 10 2004+):
/// the system pushes a full snapshot of which processes are streaming each camera.
/// (The registry consent store is not updated live on current Windows 11 builds.)
/// </summary>
internal sealed class CameraMonitor : IMFSensorActivitiesReportCallback
{
    private const uint MF_VERSION = 0x00020070; // MF_SDK_VERSION << 16 | MF_API_VERSION
    private IMFSensorActivityMonitor? _monitor;
    private volatile bool _inUse;

    public bool InUse => _inUse;

    public void Start()
    {
        PInvoke.MFStartup(MF_VERSION, 0).ThrowOnFailure();
        PInvoke.MFCreateSensorActivityMonitor(this, out _monitor).ThrowOnFailure();
        _monitor.Start();
    }

    // Called on a Media Foundation worker thread.
    public unsafe void OnActivitiesReport(IMFSensorActivitiesReport report)
    {
        bool any = false;
        report.GetCount(out uint sensors);
        for (uint i = 0; i < sensors && !any; i++)
        {
            report.GetActivityReport(i, out var sensor);
            sensor.GetProcessCount(out uint processes);
            for (uint p = 0; p < processes && !any; p++)
            {
                sensor.GetProcessActivity(p, out var activity);
                BOOL streaming;
                activity.GetStreamingState(&streaming);
                any = streaming;
            }
        }
        _inUse = any;
    }
}

/// <summary>Microphone in use: any active audio session on any capture endpoint (Core Audio).</summary>
internal static class MicrophoneMonitor
{
    public static unsafe bool InUse()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(EDataFlow.eCapture, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var devices);
        devices.GetCount(out uint count);

        var iid = typeof(IAudioSessionManager2).GUID;
        for (uint d = 0; d < count; d++)
        {
            devices.Item(d, out var device);
            device.Activate(&iid, CLSCTX.CLSCTX_ALL, null, out object obj);
            var sessions = ((IAudioSessionManager2)obj).GetSessionEnumerator();
            sessions.GetCount(out int n);
            for (int s = 0; s < n; s++)
            {
                sessions.GetSession(s, out var control);
                AudioSessionState state;
                control.GetState(&state);
                if (state == AudioSessionState.AudioSessionStateActive) return true;
            }
        }
        return false;
    }
}
