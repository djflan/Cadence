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

        // Windows MIDI Services and ALSA adapters are not implemented yet.
        return [];
    }

    /// <summary>
    /// Scheduling for the playback thread. On macOS the real-time time-constraint policy removed
    /// multi-millisecond wake-up delays in measurement (ADR 0012).
    /// </summary>
    public static void ConfigurePlaybackThread()
    {
        if (OperatingSystem.IsMacOS() && !ThreadScheduling.MakeCurrentThreadRealtime(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5)))
        {
            ThreadScheduling.PromoteCurrentThread();
        }
    }
}
