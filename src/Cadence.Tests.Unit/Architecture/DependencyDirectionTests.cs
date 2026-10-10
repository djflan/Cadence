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
        { "Cadence.Signal", ["Cadence.Domain"] },
        { "Cadence.Playback", ["Cadence.Domain", "Cadence.Midi"] },
        { "Cadence.Application", ["Cadence.Domain", "Cadence.Infrastructure", "Cadence.Midi", "Cadence.Playback", "Cadence.Profiles"] },
        { "Cadence.Infrastructure", ["Cadence.Domain"] },
        { "Cadence.Presentation", ["Cadence.Application", "Cadence.Domain", "Cadence.Infrastructure", "Cadence.Midi", "Cadence.Playback", "Cadence.Profiles"] },
        { "Cadence.Plugins.Protocol", [] },
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
    public void Presentation_DoesNotDependOnAUiFramework() =>
        Assert.DoesNotContain(
            Assembly.Load("Cadence.Presentation").GetReferencedAssemblies(),
            r => r.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);

    [Fact]
    public void Domain_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = Assembly.Load("Cadence.Domain").GetReferencedAssemblies().Select(r => r.Name ?? string.Empty);

        Assert.All(references, name => Assert.True(
            name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Cadence.Domain must not reference {name}."));
    }

    [Fact]
    public void PluginsProtocol_ReferencesOnlyTheBaseClassLibrary()
    {
        var references = Assembly.Load("Cadence.Plugins.Protocol").GetReferencedAssemblies().Select(r => r.Name ?? string.Empty);

        Assert.All(references, name => Assert.True(
            name is "System" or "mscorlib" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Cadence.Plugins.Protocol must not reference {name}."));
    }
}
