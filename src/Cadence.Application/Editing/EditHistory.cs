using Cadence.Domain.Projects;

namespace Cadence.Application.Editing;

/// <summary>A named, reversible change to a project.</summary>
public interface IProjectCommand
{
    /// <summary>Short description for Undo/Redo menus, e.g. "Rename Track".</summary>
    string Label { get; }

    /// <summary>Returns the changed project, or the same instance when there is nothing to change.</summary>
    Project Apply(Project project);
}

public sealed class ProjectCommand(string label, Func<Project, Project> apply) : IProjectCommand
{
    public string Label { get; } = label;

    public Project Apply(Project project) => apply(project);
}

/// <summary>
/// Undo and redo over immutable project states. Each executed command records the state before it;
/// because projects are immutable and share structure, this costs little and can never drift from
/// the model the way replayed UI actions can.
/// </summary>
public sealed class EditHistory
{
    private readonly int _capacity;
    private readonly LinkedList<(string Label, Project Before)> _undo = new();
    private readonly Stack<(string Label, Project After)> _redo = new();

    public EditHistory(Project initial, int capacity = 500)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Current = initial ?? throw new ArgumentNullException(nameof(initial));
        _capacity = capacity;
    }

    public Project Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _undo.Last?.Value.Label;

    public string? RedoLabel => _redo.TryPeek(out var top) ? top.Label : null;

    /// <summary>Raised after every change to <see cref="Current"/>.</summary>
    public event EventHandler? Changed;

    /// <returns>True if the command changed the project.</returns>
    public bool Execute(IProjectCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var next = command.Apply(Current);
        if (ReferenceEquals(next, Current))
        {
            return false;
        }

        _undo.AddLast((command.Label, Current));
        if (_undo.Count > _capacity)
        {
            _undo.RemoveFirst();
        }

        _redo.Clear();
        Set(next);
        return true;
    }

    public void Undo()
    {
        if (_undo.Last is not { } last)
        {
            return;
        }

        _undo.RemoveLast();
        _redo.Push((last.Value.Label, Current));
        Set(last.Value.Before);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var next))
        {
            return;
        }

        _undo.AddLast((next.Label, Current));
        Set(next.After);
    }

    /// <summary>Replaces the project and forgets all history, e.g. after opening a file.</summary>
    public void Reset(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        _undo.Clear();
        _redo.Clear();
        Set(project);
    }

    private void Set(Project project)
    {
        Current = project;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
