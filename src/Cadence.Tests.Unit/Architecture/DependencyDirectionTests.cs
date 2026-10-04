using System.Reflection;

namespace Cadence.Tests.Unit.Architecture;

/// <summary>Project references must follow the documented dependency direction (README, ADR 0002).</summary>
public sealed class DependencyDirectionTests
{
    public static TheoryData<string, string[]> AllowedReferences() => new()
    {
        { "Cadence.Domain", [] },
        { "Cadence.Midi", ["Cadence.Domain"] },
        { "Cadence.Profiles", ["Cadence.Domain"] },
        { "Cadence.Playback", ["Cadence.Domain", "Cadence.Midi"] },
        { "Cadence.Application", ["Cadence.Domain", "Cadence.Midi", "Cadence.Playback", "Cadence.Profiles"] },
        { "Cadence.Infrastructure", ["Cadence.Domain"] },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void Project_ReferencesOnlyAllowedCadenceAssemblies(string assembly, string[] allowed)
    {
        var references = Assembly.Load(assembly).GetReferencedAssemblies().Select(r => r.Name ?? string.Empty).ToList();

        var cadence = references.Where(name => name.StartsWith("Cadence", StringComparison.Ordinal));
        Assert.All(cadence, name => Assert.Contains(name, allowed));
    }

    [Fact]
    public void Domain_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = Assembly.Load("Cadence.Domain").GetReferencedAssemblies().Select(r => r.Name ?? string.Empty);

        Assert.All(references, name => Assert.True(
            name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Cadence.Domain must not reference {name}."));
    }
}
