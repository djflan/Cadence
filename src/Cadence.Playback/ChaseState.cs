using Cadence.Domain.Midi;

namespace Cadence.Playback;

/// <summary>A message to send to <see cref="Slot"/> before playback starts mid-sequence.</summary>
internal readonly record struct ChaseMessage(int Slot, ChannelMessage Message);

/// <summary>
/// Computes the controller state in effect at a position so starting playback mid-song sounds as it
/// would have from the top: bank select, program, continuous controllers, pitch bend, and channel
/// pressure, per output slot and channel.
/// </summary>
/// <remarks>
/// Deliberately not chased: notes already sounding at the position, SysEx (resending device setup
/// on every seek would be surprising), RPN/NRPN selection and data entry (their meaning depends on
/// sequence), and channel mode messages.
/// </remarks>
internal static class ChaseState
{
    public static ChaseMessage[] Compute(PlaybackPlan plan, long tick)
    {
        var channels = new SortedDictionary<(int Slot, byte Channel), ChannelState>();
        var end = plan.FirstIndexAtOrAfter(tick);
        for (var i = 0; i < end; i++)
        {
            var e = plan.Events[i];
            if (e.PayloadIndex >= 0 || e.IsNote)
            {
                continue;
            }

            var message = e.Message;
            var key = (e.Slot, message.Channel.Index);
            if (!channels.TryGetValue(key, out var state))
            {
                state = new ChannelState();
                channels[key] = state;
            }

            switch (message.Kind)
            {
                case ChannelMessageKind.ControlChange when IsChased(message.Data1):
                    state.Controllers[message.Data1] = message;
                    break;
                case ChannelMessageKind.ProgramChange:
                    state.Program = message;
                    break;
                case ChannelMessageKind.PitchBend:
                    state.PitchBend = message;
                    break;
                case ChannelMessageKind.ChannelPressure:
                    state.Pressure = message;
                    break;
            }
        }

        var result = new List<ChaseMessage>();
        foreach (var ((slot, _), state) in channels)
        {
            // Bank select first, then program, then the remaining controllers in number order.
            AddIfSet(slot, state.Controllers[0]);
            AddIfSet(slot, state.Controllers[32]);
            AddIfSet(slot, state.Program);
            for (var controller = 1; controller < state.Controllers.Length; controller++)
            {
                if (controller != 32)
                {
                    AddIfSet(slot, state.Controllers[controller]);
                }
            }

            AddIfSet(slot, state.PitchBend);
            AddIfSet(slot, state.Pressure);
        }

        return [.. result];

        void AddIfSet(int slot, ChannelMessage? message)
        {
            if (message is { } m)
            {
                result.Add(new ChaseMessage(slot, m));
            }
        }
    }

    private static bool IsChased(byte controller) =>
        controller is not (6 or 38) and not (>= 96 and <= 101) and < 120;

    private sealed class ChannelState
    {
        public ChannelMessage?[] Controllers { get; } = new ChannelMessage?[120];

        public ChannelMessage? Program { get; set; }

        public ChannelMessage? PitchBend { get; set; }

        public ChannelMessage? Pressure { get; set; }
    }
}
