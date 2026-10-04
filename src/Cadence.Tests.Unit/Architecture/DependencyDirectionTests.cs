using Cadence.Domain.Midi;

namespace Cadence.Tests.Unit.Architecture;

public sealed class DependencyDirectionTests
{
    [Fact]
    public void Domain_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = typeof(NoteNumber).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty);

        var offenders = references.Where(name => !IsBaseClassLibrary(name)).ToList();

        Assert.Empty(offenders);
    }

    private static bool IsBaseClassLibrary(string assemblyName) =>
        assemblyName is "System" or "mscorlib" or "netstandard"
        || assemblyName.StartsWith("System.", StringComparison.Ordinal)
        || assemblyName.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);
}
