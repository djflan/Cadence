using System.Collections.Immutable;
using Cadence.Domain.Midi;
using Cadence.Domain.Sequencing;
using Cadence.Midi.Endpoints;
using Cadence.Playback;
using Cadence.Profiles;

namespace Cadence.Application.Routing;

/// <summary>The output slots and plan bindings derived from resolved routes.</summary>
/// <param name="Slots">One endpoint per output slot; tracks sharing an endpoint share a slot.</param>
/// <param name="Bindings">Plan bindings for every track that can play.</param>
public sealed record PreparedRouting(ImmutableArray<EndpointId> Slots, ImmutableDictionary<TrackId, PlanTrackBinding> Bindings);

/// <summary>Outputs opened for a <see cref="PreparedRouting"/>. Disposing closes every port.</summary>
public sealed class OpenedOutputs : IDisposable
{
    internal OpenedOutputs(IMidiOutput?[] outputs, ImmutableArray<string> problems)
    {
        Outputs = outputs;
        Problems = problems;
    }

    /// <summary>One output per slot; <see langword="null"/> where opening failed.</summary>
    public IReadOnlyList<IMidiOutput?> Outputs { get; }

    public ImmutableArray<string> Problems { get; }

    public void Dispose()
    {
        foreach (var output in Outputs)
        {
            output?.Dispose();
        }
    }
}

/// <summary>Turns resolved routes into playback bindings and open outputs.</summary>
public static class PlaybackRouting
{
    /// <summary>
    /// Assigns a slot to each distinct usable endpoint and builds plan bindings, including the voice
    /// selection messages from the route's profile. Tracks whose endpoint is unusable are left out.
    /// </summary>
    public static PreparedRouting Prepare(Sequence sequence, IEnumerable<ResolvedRoute> routes)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(routes);
        var slots = new List<EndpointId>();
        var bindings = ImmutableDictionary.CreateBuilder<TrackId, PlanTrackBinding>();

        foreach (var resolved in routes.Where(r => r.CanPlay))
        {
            var endpoint = resolved.Endpoint.Endpoint!.Id;
            var slot = slots.IndexOf(endpoint);
            if (slot < 0)
            {
                slots.Add(endpoint);
                slot = slots.Count - 1;
            }

            var route = resolved.Route!;
            bindings[resolved.Track] = new PlanTrackBinding(slot, route.Channel, route.Transpose)
            {
                InitialMessages = VoiceMessages(sequence.FindTrack(resolved.Track), resolved),
            };
        }

        return new PreparedRouting([.. slots], bindings.ToImmutable());
    }

    /// <summary>Opens one output per slot. Failures leave the slot empty and are reported, not thrown.</summary>
    public static async Task<OpenedOutputs> OpenAsync(PreparedRouting routing, EndpointDirectory directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(directory);
        var outputs = new IMidiOutput?[routing.Slots.Length];
        var problems = ImmutableArray.CreateBuilder<string>();
        try
        {
            for (var i = 0; i < outputs.Length; i++)
            {
                try
                {
                    outputs[i] = await directory.OpenOutputAsync(routing.Slots[i], cancellationToken).ConfigureAwait(false);
                }
                catch (EndpointUnavailableException ex)
                {
                    problems.Add(ex.Message);
                }
            }
        }
        catch
        {
            foreach (var output in outputs)
            {
                output?.Dispose();
            }

            throw;
        }

        return new OpenedOutputs(outputs, problems.ToImmutable());
    }

    private static ImmutableArray<ChannelMessage> VoiceMessages(Track? track, ResolvedRoute resolved)
    {
        if (resolved.Route?.Voice is not { } voice || resolved.Profile.Profile?.FindBank(voice.BankId) is not { } bank)
        {
            return [];
        }

        var channel = resolved.Route.Channel ?? FirstChannel(track) ?? MidiChannel.FromIndex(0);
        return DeviceProfile.SelectionMessages(bank, voice.Program, channel);
    }

    private static MidiChannel? FirstChannel(Track? track) =>
        track?.Events.Select(e => e switch
        {
            NoteEvent note => note.Channel,
            ChannelEvent channel => channel.Message.Channel,
            _ => (MidiChannel?)null,
        }).FirstOrDefault(c => c is not null);
}
