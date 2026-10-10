using System.Collections.Immutable;
using Cadence.Plugins.Protocol.Exchange;

namespace Cadence.Plugins.Protocol;

public enum MessageType : ushort
{
    Hello = 1,
    HelloAck = 2,
    Ping = 3,
    Pong = 4,
    CreateInstance = 5,
    InstanceCreated = 6,
    DestroyInstance = 7,
    CaptureState = 8,
    StateResult = 9,
    RestoreState = 10,
    RestoreStateResult = 11,
    SetParameter = 12,
    GetParameters = 13,
    ParameterValues = 14,
    Ack = 15,
    Error = 16,
    Shutdown = 17,
    ScanModule = 18,
    ScanResult = 19,
    InduceTestFault = 20,
}

/// <summary>What a worker process was launched to do.</summary>
public enum WorkerMode : byte
{
    InstanceHost = 1,
    Scanner = 2,
}

public enum ErrorCode : ushort
{
    Internal = 1,
    InvalidRequest = 2,
    PluginNotFound = 3,
    InstanceNotFound = 4,
    InstanceExists = 5,
    ExchangeFailed = 6,
    StateRejected = 7,
    NotSupported = 8,
}

/// <summary>Why a module could not be scanned. A scanner process only ever reports <see cref="Malformed"/>; the host decides the others.</summary>
public enum ScanFailureKind : byte
{
    Crashed = 1,
    TimedOut = 2,
    Malformed = 3,
    ProtocolError = 4,
}

/// <summary>Failure behaviours a worker can be told to show. Test-only: reachable only through <see cref="InduceTestFault"/>.</summary>
public enum TestFault : byte
{
    /// <summary>The worker calls <see cref="Environment.FailFast(string)"/>.</summary>
    FailFast = 1,

    /// <summary>The worker stops answering on both planes but stays alive.</summary>
    Hang = 2,

    /// <summary>The worker writes bytes that are not a valid frame to the control pipe.</summary>
    GarbageFrames = 3,
}

/// <summary>A control-plane message. The request id travels in the frame header, not in the message.</summary>
public abstract record ProtocolMessage
{
    public abstract MessageType Type { get; }
}

/// <summary>First message, sent by the worker after it connects. The token proves it is the process the host launched.</summary>
public sealed record Hello(int ProtocolVersion, string Token, int ProcessId, WorkerMode Mode) : ProtocolMessage
{
    public override MessageType Type => MessageType.Hello;
}

public sealed record HelloAck(int ProtocolVersion) : ProtocolMessage
{
    public override MessageType Type => MessageType.HelloAck;
}

public sealed record Ping(long Sequence) : ProtocolMessage
{
    public override MessageType Type => MessageType.Ping;
}

public sealed record Pong(long Sequence) : ProtocolMessage
{
    public override MessageType Type => MessageType.Pong;
}

/// <summary>
/// Creates a plugin instance. The worker creates the data-plane file in <paramref name="DataPlaneDirectory"/>, stamped with
/// <paramref name="Generation"/>, and restores <paramref name="State"/> if given.
/// </summary>
public sealed record CreateInstance(
    PluginInstanceId InstanceId,
    PluginIdentity Plugin,
    double SampleRate,
    ExchangeOptions Exchange,
    uint Generation,
    string DataPlaneDirectory,
    PluginStateData? State) : ProtocolMessage
{
    public override MessageType Type => MessageType.CreateInstance;
}

/// <summary>
/// Reply to <see cref="CreateInstance"/>. <paramref name="StateRestoreError"/> is set when the given state was rejected;
/// the instance then runs with default parameters.
/// </summary>
public sealed record InstanceCreated(
    PluginInstanceId InstanceId,
    int LatencyFrames,
    ImmutableArray<ParameterDescriptor> Parameters,
    string DataPlanePath,
    uint Generation,
    string? StateRestoreError) : ProtocolMessage
{
    public override MessageType Type => MessageType.InstanceCreated;
}

public sealed record DestroyInstance(PluginInstanceId InstanceId) : ProtocolMessage
{
    public override MessageType Type => MessageType.DestroyInstance;
}

public sealed record CaptureState(PluginInstanceId InstanceId) : ProtocolMessage
{
    public override MessageType Type => MessageType.CaptureState;
}

public sealed record StateResult(PluginInstanceId InstanceId, PluginStateData State) : ProtocolMessage
{
    public override MessageType Type => MessageType.StateResult;
}

public sealed record RestoreState(PluginInstanceId InstanceId, PluginStateData State) : ProtocolMessage
{
    public override MessageType Type => MessageType.RestoreState;
}

public sealed record RestoreStateResult(PluginInstanceId InstanceId, bool Succeeded, string? Message) : ProtocolMessage
{
    public override MessageType Type => MessageType.RestoreStateResult;
}

public sealed record SetParameter(PluginInstanceId InstanceId, uint ParameterId, double Value) : ProtocolMessage
{
    public override MessageType Type => MessageType.SetParameter;
}

public sealed record GetParameters(PluginInstanceId InstanceId) : ProtocolMessage
{
    public override MessageType Type => MessageType.GetParameters;
}

public sealed record ParameterValues(PluginInstanceId InstanceId, ImmutableArray<ParameterValue> Values) : ProtocolMessage
{
    public override MessageType Type => MessageType.ParameterValues;
}

/// <summary>Success reply to a request that returns nothing.</summary>
public sealed record Ack : ProtocolMessage
{
    public override MessageType Type => MessageType.Ack;
}

/// <summary>Failure reply to a request.</summary>
public sealed record ErrorReply(ErrorCode Code, string Message) : ProtocolMessage
{
    public override MessageType Type => MessageType.Error;
}

/// <summary>Asks the worker to exit cleanly.</summary>
public sealed record Shutdown : ProtocolMessage
{
    public override MessageType Type => MessageType.Shutdown;
}

public sealed record ScanModule(string ModulePath) : ProtocolMessage
{
    public override MessageType Type => MessageType.ScanModule;
}

/// <summary>Scanner reply: the plugins in a module, or <see cref="ScanFailureKind.Malformed"/> with a message.</summary>
public sealed record ScanResult(string ModulePath, ImmutableArray<PluginIdentity> Plugins, ScanFailureKind? Failure, string? Message) : ProtocolMessage
{
    public override MessageType Type => MessageType.ScanResult;
}

/// <summary>Test-only: tells the worker to misbehave. Never sent by production code paths.</summary>
public sealed record InduceTestFault(TestFault Fault) : ProtocolMessage
{
    public override MessageType Type => MessageType.InduceTestFault;
}

/// <summary>A decoded frame: the request id that correlates a reply with its request, and the message.</summary>
public readonly record struct Frame(uint RequestId, ProtocolMessage Message);
