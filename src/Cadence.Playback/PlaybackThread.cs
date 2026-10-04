using System.Diagnostics;

namespace Cadence.Playback;

/// <summary>
/// Drives a <see cref="PlaybackEngine"/> on a dedicated high-priority thread: pump, then wait until
/// the next event or until a command arrives. Waits longer than <see cref="SpinThreshold"/> block on
/// the engine's signal; shorter ones spin so immediate-delivery endpoints are served on time.
/// </summary>
public sealed class PlaybackThread : IDisposable
{
    public static readonly TimeSpan SpinThreshold = TimeSpan.FromMilliseconds(1.5);

    private readonly PlaybackEngine _engine;
    private readonly Thread _thread;
    private volatile bool _stopping;

    public PlaybackThread(PlaybackEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Cadence playback",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Set if the engine threw; the thread stops pumping after a fault.</summary>
    public Exception? Fault { get; private set; }

    public void Dispose()
    {
        _stopping = true;
        try
        {
            ((EventWaitHandle)_engine.WorkSignal).Set();
        }
        catch (ObjectDisposedException)
        {
            // The engine was disposed first; the thread will notice _stopping on its next pass.
        }

        _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        try
        {
            while (!_stopping)
            {
                var wait = _engine.Pump();
                if (wait == Timeout.InfiniteTimeSpan)
                {
                    _engine.WorkSignal.WaitOne();
                }
                else if (wait > SpinThreshold)
                {
                    _engine.WorkSignal.WaitOne(wait - TimeSpan.FromMilliseconds(1));
                }
                else if (wait > TimeSpan.Zero)
                {
                    var until = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
                    var spinner = default(SpinWait);
                    while (Stopwatch.GetTimestamp() < until && !_stopping)
                    {
                        spinner.SpinOnce(sleep1Threshold: -1);
                    }
                }
            }
        }
        catch (ObjectDisposedException) when (_stopping)
        {
        }
#pragma warning disable CA1031 // A fault on the playback thread must be captured, not crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Fault = ex;
        }
    }
}
