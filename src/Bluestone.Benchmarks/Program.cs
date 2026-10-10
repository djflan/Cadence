using BenchmarkDotNet.Running;
using Cadence.Benchmarks;
using Cadence.Midi.Endpoints;

if (args is ["--jitter", ..])
{
    var seconds = args.Length > 1 ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 10;
    var mode = args.Contains("--realtime") ? "realtime" : args.Contains("--promote") ? "promote" : args.Contains("--timer") ? "timer" : "default";
    Action? setup = mode switch { "realtime" => Realtime, "promote" => Promote, "timer" => Timer, _ => null };
    Console.WriteLine($"Playback thread: {mode}");
    JitterProbe.Run(TimeSpan.FromSeconds(seconds), EndpointCapabilities.None, setup);
    Console.WriteLine();
    JitterProbe.Run(TimeSpan.FromSeconds(seconds), EndpointCapabilities.ScheduledDelivery, setup);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Workloads).Assembly).Run(args);

static void Realtime()
{
    if (OperatingSystem.IsMacOS())
    {
        Cadence.Platform.CoreMidi.ThreadScheduling.MakeCurrentThreadRealtime(TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(5));
    }
    else if (OperatingSystem.IsWindows())
    {
        Cadence.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
        Cadence.Platform.Windows.ThreadScheduling.JoinProAudioTask();
    }
}

static void Promote()
{
    if (OperatingSystem.IsMacOS())
    {
        Cadence.Platform.CoreMidi.ThreadScheduling.PromoteCurrentThread();
    }
    else if (OperatingSystem.IsWindows())
    {
        Cadence.Platform.Windows.ThreadScheduling.JoinProAudioTask();
    }
}

static void Timer()
{
    if (OperatingSystem.IsWindows())
    {
        Cadence.Platform.Windows.ThreadScheduling.RequestHighTimerResolution();
    }
}
