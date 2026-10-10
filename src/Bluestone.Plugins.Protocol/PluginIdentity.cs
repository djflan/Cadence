using System.Collections.Immutable;
using System.Text;

namespace Bluestone.Plugins.Protocol;

public enum PluginKind : byte
{
    Instrument = 1,
    MidiEffect = 2,
    AudioEffect = 3,
}

/// <summary>
/// What a plugin is: its format, the module (file) it comes from, and its id inside that module. This identifies a
/// plugin type, never a running instance; see <see cref="PluginInstanceId"/>.
/// </summary>
public sealed record PluginIdentity
{
    public PluginIdentity(string format, string moduleId, string pluginId, string displayName, string vendor, PluginKind kind, string version)
    {
        Format = ProtocolText.Require(format, nameof(format), allowEmpty: false);
        ModuleId = ProtocolText.Require(moduleId, nameof(moduleId), allowEmpty: false);
        PluginId = ProtocolText.Require(pluginId, nameof(pluginId), allowEmpty: false);
        DisplayName = ProtocolText.Require(displayName, nameof(displayName), allowEmpty: true);
        Vendor = ProtocolText.Require(vendor, nameof(vendor), allowEmpty: true);
        Version = ProtocolText.Require(version, nameof(version), allowEmpty: true);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown plugin kind.");
        }

        Kind = kind;
    }

    /// <summary>The plugin format, for example <c>bluestone-reference</c> for the built-in reference plugins.</summary>
    public string Format { get; }

    public string ModuleId { get; }

    public string PluginId { get; }

    public string DisplayName { get; }

    public string Vendor { get; }

    public PluginKind Kind { get; }

    public string Version { get; }

    public override string ToString() => $"{DisplayName} ({Format}:{PluginId})";
}

/// <summary>Identity of one running plugin instance. Stable across worker restarts of that instance.</summary>
public readonly record struct PluginInstanceId(Guid Value)
{
    public static PluginInstanceId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("N");
}

/// <summary>A plugin parameter. Values are normalized to [0, 1]; <see cref="StepCount"/> 0 means continuous.</summary>
public sealed record ParameterDescriptor
{
    public ParameterDescriptor(uint id, string name, double defaultValue, int stepCount = 0)
    {
        if (!NormalizedValue.IsValid(defaultValue))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultValue), defaultValue, "A default value must be normalized to [0, 1].");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(stepCount);
        Id = id;
        Name = ProtocolText.Require(name, nameof(name), allowEmpty: false);
        DefaultValue = defaultValue;
        StepCount = stepCount;
    }

    public uint Id { get; }

    public string Name { get; }

    public double DefaultValue { get; }

    public int StepCount { get; }
}

/// <summary>A parameter id with its normalized value.</summary>
public readonly record struct ParameterValue(uint Id, double Value);

public static class NormalizedValue
{
    /// <summary>True for a finite value in [0, 1]; NaN and infinities are rejected.</summary>
    public static bool IsValid(double value) => value is >= 0.0 and <= 1.0;
}

/// <summary>Opaque plugin state as the plugin produced it, tagged with a format string the plugin understands.</summary>
public sealed class PluginStateData : IEquatable<PluginStateData>
{
    public PluginStateData(string format, ImmutableArray<byte> data)
    {
        Format = ProtocolText.Require(format, nameof(format), allowEmpty: false);
        if (data.IsDefault)
        {
            throw new ArgumentException("State data must not be default.", nameof(data));
        }

        if (data.Length > ProtocolLimits.MaxStateBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(data), data.Length, "Plugin state is larger than the protocol allows.");
        }

        Data = data;
    }

    public string Format { get; }

    public ImmutableArray<byte> Data { get; }

    public bool Equals(PluginStateData? other) =>
        other is not null && Format == other.Format && Data.AsSpan().SequenceEqual(other.Data.AsSpan());

    public override bool Equals(object? obj) => Equals(obj as PluginStateData);

    public override int GetHashCode() => HashCode.Combine(Format, Data.Length);
}

internal static class ProtocolText
{
    public static string Require(string value, string parameterName, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!allowEmpty && value.Length == 0)
        {
            throw new ArgumentException("The value must not be empty.", parameterName);
        }

        if (Encoding.UTF8.GetByteCount(value) > ProtocolLimits.MaxStringBytes)
        {
            throw new ArgumentException($"The value is longer than {ProtocolLimits.MaxStringBytes} UTF-8 bytes.", parameterName);
        }

        return value;
    }
}
