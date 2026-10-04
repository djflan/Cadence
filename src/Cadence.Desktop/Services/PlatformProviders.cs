using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;

namespace Cadence.Desktop.Services;

/// <summary>Creates the operating system's MIDI providers available on this machine.</summary>
internal static class PlatformProviders
{
    public static IEnumerable<IMidiEndpointProvider> Create(IMonotonicClock clock)
    {
        _ = clock;
        return [];
    }
}
