using Cadence.Plugins.Protocol;
using Cadence.Plugins.Protocol.Exchange;
using Cadence.PluginWorker.Plugins;

namespace Cadence.PluginWorker;

/// <summary>
/// Instance-host mode: serves control requests one at a time until the host sends Shutdown or the pipe closes.
/// Every request gets exactly one reply with the request's id; failures become <see cref="ErrorReply"/>.
/// </summary>
internal sealed class InstanceHostSession(Stream pipe)
{
    private readonly Dictionary<PluginInstanceId, HostedInstance> _instances = [];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await FrameCodec.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (frame is not { } request || request.Message is Shutdown)
                {
                    return;
                }

                if (request.Message is InduceTestFault fault)
                {
                    await WorkerFaults.InduceAsync(fault.Fault, pipe, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var reply = Handle(request.Message);
                await FrameCodec.WriteAsync(pipe, request.RequestId, reply, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var instance in _instances.Values)
            {
                instance.Dispose();
            }

            _instances.Clear();
        }
    }

    private ProtocolMessage Handle(ProtocolMessage message) => message switch
    {
        Ping ping => new Pong(ping.Sequence),
        CreateInstance create => Create(create),
        DestroyInstance destroy => Destroy(destroy.InstanceId),
        CaptureState capture => WithInstance(capture.InstanceId, i => new StateResult(i.Id, i.SaveState())),
        RestoreState restore => WithInstance(restore.InstanceId, i =>
        {
            var error = i.TryLoadState(restore.State);
            return new RestoreStateResult(i.Id, error is null, error);
        }),
        SetParameter set => WithInstance(set.InstanceId, i => i.TrySetParameter(set.ParameterId, set.Value)
            ? new Ack()
            : new ErrorReply(ErrorCode.InvalidRequest, $"Unknown parameter {set.ParameterId}.")),
        GetParameters get => WithInstance(get.InstanceId, i => new ParameterValues(i.Id, [.. i.GetParameters()])),
        _ => new ErrorReply(ErrorCode.InvalidRequest, $"{message.Type} is not a request this worker serves."),
    };

    private ProtocolMessage Create(CreateInstance request)
    {
        if (_instances.ContainsKey(request.InstanceId))
        {
            return new ErrorReply(ErrorCode.InstanceExists, $"Instance {request.InstanceId} already exists.");
        }

        var plugin = ReferencePluginCatalog.Create(request.Plugin);
        if (plugin is null)
        {
            return new ErrorReply(ErrorCode.PluginNotFound, $"Plugin {request.Plugin} is not available in this worker.");
        }

        var options = request.Exchange;
        try
        {
            plugin.Prepare(request.SampleRate, options.MaxFrames, options.InputChannels, options.OutputChannels);
        }
        catch (NotSupportedException ex)
        {
            return new ErrorReply(ErrorCode.NotSupported, ex.Message);
        }

        string? stateError = null;
        if (request.State is { } state)
        {
            try
            {
                plugin.LoadState(state);
            }
            catch (InvalidDataException ex)
            {
                stateError = ex.Message;
            }
        }

        WorkerBlockExchange exchange;
        try
        {
            var path = Path.Combine(request.DataPlaneDirectory, $"{request.InstanceId}-g{request.Generation}.cadence-exchange");
            exchange = WorkerBlockExchange.Create(path, options, request.Generation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new ErrorReply(ErrorCode.ExchangeFailed, $"Could not create the data-plane file: {ex.Message}");
        }

        var instance = new HostedInstance(request.InstanceId, plugin, exchange, request.SampleRate);
        _instances.Add(request.InstanceId, instance);
        instance.Start();
        return new InstanceCreated(request.InstanceId, plugin.LatencyFrames, plugin.Parameters, exchange.Path, request.Generation, stateError);
    }

    private ProtocolMessage Destroy(PluginInstanceId id)
    {
        if (!_instances.Remove(id, out var instance))
        {
            return new ErrorReply(ErrorCode.InstanceNotFound, $"Instance {id} does not exist.");
        }

        instance.Dispose();
        return new Ack();
    }

    private ProtocolMessage WithInstance(PluginInstanceId id, Func<HostedInstance, ProtocolMessage> action) =>
        _instances.TryGetValue(id, out var instance)
            ? action(instance)
            : new ErrorReply(ErrorCode.InstanceNotFound, $"Instance {id} does not exist.");
}
