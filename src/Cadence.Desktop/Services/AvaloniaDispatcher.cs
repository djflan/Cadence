using Avalonia.Threading;
using Cadence.Presentation;

namespace Cadence.Desktop.Services;

internal sealed class AvaloniaDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
