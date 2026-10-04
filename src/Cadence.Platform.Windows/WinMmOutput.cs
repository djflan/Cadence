using System.Runtime.Versioning;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Wire;

namespace Cadence.Platform.Windows;

/// <summary>
/// Sends short messages with <c>midiOutShortMsg</c>, which delivers immediately. Nothing is allocated
/// per send. System exclusive is rejected until long-message buffers are implemented.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinMmOutput(WinMmProvider provider, EndpointDescriptor endpoint, IntPtr handle) : IMidiOutput
{
    private int _state = (int)EndpointState.Open;
    private int _sending;

    public EndpointDescriptor Endpoint { get; } = endpoint;

    public EndpointState State => (EndpointState)Volatile.Read(ref _state);

    public event EventHandler<EndpointStateChangedEventArgs>? StateChanged;

    public SendResult Send(ReadOnlySpan<byte> message, MidiTimestamp timestamp)
    {
        // Dispose marks the handle closed, then waits for in-flight sends before closing the device.
        Interlocked.Increment(ref _sending);
        try
        {
            switch (State)
            {
                case EndpointState.Disconnected:
                    return SendResult.Disconnected;
                case EndpointState.Closed or EndpointState.Faulted:
                    return SendResult.Closed;
            }

            var kind = MidiWire.Classify(message);
            if (kind is MidiMessageClass.Invalid or MidiMessageClass.SystemExclusive)
            {
                return SendResult.Rejected;
            }

            var packed = (uint)message[0];
            if (message.Length > 1)
            {
                packed |= (uint)message[1] << 8;
            }

            if (message.Length > 2)
            {
                packed |= (uint)message[2] << 16;
            }

            switch (WinMm.midiOutShortMsg(handle, packed))
            {
                case WinMm.NoError:
                    return SendResult.Sent;
                case WinMm.NotReady:
                    return SendResult.QueueFull;
                case WinMm.NoDriver or WinMm.NoDevice or WinMm.InvalidHandle:
                    Transition(EndpointState.Disconnected, "The WinMM device went away.");
                    return SendResult.Disconnected;
                default:
                    Transition(EndpointState.Faulted, "WinMM reported an error.");
                    return SendResult.Closed;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _sending);
        }
    }

    public void Dispose()
    {
        if (!Transition(EndpointState.Closed, null))
        {
            return;
        }

        var spinner = default(SpinWait);
        while (Volatile.Read(ref _sending) != 0)
        {
            spinner.SpinOnce();
        }

        // Reset turns off any sounding notes before the device is released.
        _ = WinMm.midiOutReset(handle);
        _ = WinMm.midiOutClose(handle);
        provider.Forget(this);
    }

    private bool Transition(EndpointState state, string? reason)
    {
        while (true)
        {
            var current = Volatile.Read(ref _state);
            if (current == (int)EndpointState.Closed || current == (int)state)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, (int)state, current) == current)
            {
                break;
            }
        }

        StateChanged?.Invoke(this, new EndpointStateChangedEventArgs(state, reason));
        return true;
    }
}
