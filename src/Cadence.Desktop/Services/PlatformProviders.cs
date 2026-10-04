using System.Diagnostics;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Platform.CoreMidi;

namespace Cadence.Desktop.Services;

/// <summary>Creates the operating system's MIDI providers available on this machine.</summary>
internal static class PlatformProviders
{
    /// <summary>The virtual port Cadence publishes so other applications can receive from it.</summary>
    public const string VirtualOutputName = "Cadence Out";

    public static IEnumerable<IMidiEndpointProvider> Create(IMonotonicClock clock)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var coreMidi = new CoreMidiProvider(clock);
                coreMidi.CreateVirtualOutput(VirtualOutputName);
                return [coreMidi];
            }
            catch (EndpointUnavailableException ex)
            {
                // Cadence still works with its built-in monitor; the reason is visible in diagnostics.
                Trace.TraceWarning($"CoreMIDI is unavailable: {ex.Message}");
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows MIDI Services is the planned primary adapter; WinMM works on every Windows version.
            return [new Cadence.Platform.Windows.WinMmProvider()];
        }
        else if (OperatingSystem.IsLinux())
        {
            try
            {
                var alsa = new Cadence.Platform.Alsa.AlsaProvider();
                alsa.CreateVirtualOutput(VirtualOutputName);
                return [alsa];
            }
            catch (EndpointUnavailableException ex)
            {
                Trace.TraceWarning($"ALSA is unavailable: {ex.Message}");
            }
        }

        return [];
    }

    /// <summary>
    /// Scheduling for the playback thread. On macOS the real-time time-constraint policy removed
    /// multi-millisecond wake-up delays in measurement (ADR 0012). On Windows a 1 ms timer resolution
    /// and the MMCSS "Pro Audio" task do the same job (ADR 0013).
    /// </summary>
    public static void ConfigurePlaybackThread()
    {
        if (OperatingSystem.IsMacOS() && !ThreadScheduling.MakeCurrentThreadRealtime(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5)))
        {
            ThreadScheduling.PromoteCurrentThread();
        }
        else if (OperatingSystem.IsWindows())
        {
            Cadence.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
            Cadence.Platform.Windows.ThreadScheduling.JoinProAudioTask();
        }
    }
}
