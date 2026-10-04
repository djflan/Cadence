using System.Collections.Immutable;
using Cadence.Application.Routing;
using Cadence.Domain.Sequencing;
using Cadence.Domain.Time;
using Cadence.Midi.Endpoints;
using Cadence.Midi.Timing;
using Cadence.Playback;
using Cadence.Profiles;

namespace Cadence.Application.Sessions;

/// <summary>
/// Connects a <see cref="ProjectSession"/> to MIDI outputs and the playback engine. It keeps routes
/// resolved as endpoints come and go, keeps outputs open for the endpoints in use, and recompiles the
/// plan after every edit, including while playing.
/// </summary>
/// <remarks>Methods are intended to be called from one (UI) thread; endpoint notifications from other threads are marshalled through <see cref="RefreshAsync"/>.</remarks>
public sealed class PlaybackController : IAsyncDisposable
{
    private readonly ProjectSession _session;
    private readonly EndpointDirectory _endpoints;
    private readonly PlaybackThread? _thread;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<EndpointId, IMidiOutput> _open = [];
    private ImmutableArray<EndpointId> _slots = [];

    public PlaybackController(ProjectSession session, EndpointDirectory endpoints, ProfileCatalog profiles, IMonotonicClock clock, PlaybackOptions? options = null, bool startThread = true)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        Engine = new PlaybackEngine(clock, session.Project.Sequence.TempoMap, options);
        _thread = startThread ? new PlaybackThread(Engine) : null;
    }

    public PlaybackEngine Engine { get; }

    public ProfileCatalog Profiles { get; private set; }

    /// <summary>Every track's route as last resolved, in track order.</summary>
    public ImmutableArray<ResolvedRoute> Routes { get; private set; } = [];

    /// <summary>Problems opening outputs during the last refresh.</summary>
    public ImmutableArray<string> OutputProblems { get; private set; } = [];

    public ImmutableArray<PlanDiagnostic> PlanDiagnostics { get; private set; } = [];

    /// <summary>Raised after <see cref="Routes"/> or the prepared plan changes.</summary>
    public event EventHandler? Refreshed;

    public void UseProfiles(ProfileCatalog profiles) => Profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));

    /// <summary>
    /// Re-resolves routes against the current endpoints, opens and closes outputs as needed, and loads
    /// a freshly compiled plan into the engine. Call after edits and when endpoints change.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = _session.Project;
            Routes = RouteResolver.ResolveAll(project.Sequence, project.Routing, Profiles, _endpoints.GetEndpoints());
            var prepared = PlaybackRouting.Prepare(project.Sequence, Routes);
            var problems = ImmutableArray.CreateBuilder<string>();

            var stale = _slots.Any(id => !_open.TryGetValue(id, out var output) || output.State != EndpointState.Open);
            if (stale || !prepared.Slots.SequenceEqual(_slots))
            {
                var outputs = new IMidiOutput?[prepared.Slots.Length];
                for (var i = 0; i < outputs.Length; i++)
                {
                    outputs[i] = await GetOrOpenAsync(prepared.Slots[i], problems, cancellationToken).ConfigureAwait(false);
                }

                Engine.SetOutputs(outputs);
                CloseUnused(prepared.Slots);
                _slots = prepared.Slots;
            }

            var plan = PlaybackPlanCompiler.Compile(project.Sequence, prepared.Bindings);
            Engine.Load(plan);
            Engine.SetLoop(project.Loop is { } loop ? new LoopRegion(loop.Start, loop.End) : null);
            PlanDiagnostics = [.. plan.Diagnostics];
            OutputProblems = problems.ToImmutable();
        }
        finally
        {
            _gate.Release();
        }

        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    public async Task PlayAsync(Tick? from = null, CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        Engine.Play(from);
    }

    public void Stop() => Engine.Stop();

    public void Seek(Tick position) => Engine.Seek(position);

    public void Panic() => Engine.Panic();

    /// <summary>
    /// Sends a track's profile initialization messages (for example a System On) to its output, after
    /// asking <paramref name="confirm"/> for each template that resets or overwrites instrument state.
    /// Nothing is sent unless the user asks; opening a project never does this.
    /// </summary>
    /// <returns>The number of messages sent.</returns>
    public async Task<int> InitializeInstrumentAsync(TrackId track, Func<SysExTemplate, Task<bool>> confirm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        var route = Routes.FirstOrDefault(r => r.Track == track);
        if (route?.Profile.Profile is not { } profile || !route.CanPlay)
        {
            return 0;
        }

        var problems = ImmutableArray.CreateBuilder<string>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var output = await GetOrOpenAsync(route.Endpoint.Endpoint!.Id, problems, cancellationToken).ConfigureAwait(false);
            if (output is null)
            {
                return 0;
            }

            var sent = 0;
            foreach (var step in profile.Initialization)
            {
                var template = profile.FindTemplate(step.TemplateId)!;
                if (template.RequiresConfirmation && !await confirm(template).ConfigureAwait(false))
                {
                    continue;
                }

                if (output.Send(template.Render().Bytes.Span, MidiTimestamp.Immediate) == SendResult.Sent)
                {
                    sent++;
                }

                await Task.Delay(step.DelayAfter, cancellationToken).ConfigureAwait(false);
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Engine.Stop();
        Engine.Pump();
        _thread?.Dispose();
        foreach (var output in _open.Values)
        {
            output.Dispose();
        }

        _open.Clear();
        Engine.Dispose();
        _gate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<IMidiOutput?> GetOrOpenAsync(EndpointId id, ImmutableArray<string>.Builder problems, CancellationToken cancellationToken)
    {
        if (_open.TryGetValue(id, out var existing) && existing.State == EndpointState.Open)
        {
            return existing;
        }

        existing?.Dispose();
        _open.Remove(id);
        try
        {
            var output = await _endpoints.OpenOutputAsync(id, cancellationToken).ConfigureAwait(false);
            _open[id] = output;
            return output;
        }
        catch (EndpointUnavailableException ex)
        {
            problems.Add(ex.Message);
            return null;
        }
    }

    private void CloseUnused(ImmutableArray<EndpointId> inUse)
    {
        foreach (var id in _open.Keys.Except(inUse).ToList())
        {
            _open[id].Dispose();
            _open.Remove(id);
        }
    }
}
