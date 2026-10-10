using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Cadence.Plugins.Protocol;
using Cadence.PluginWorker.Plugins;

namespace Cadence.PluginWorker;

/// <summary>
/// Scan mode: reads one module file and reports the plugins it declares. A module is a small JSON manifest with the
/// <see cref="ModuleExtension"/> extension; every other file is <see cref="ScanFailureKind.Malformed"/>. Nothing is
/// ever loaded as code. Two documented first-line markers exist for tests only: <see cref="CrashMarker"/> makes the
/// scanner call <see cref="Environment.FailFast(string)"/>, and <see cref="HangMarker"/> makes it hang, so the host's
/// crash and timeout handling can be exercised with a real process.
/// </summary>
internal static class ModuleScanner
{
    public const string ModuleExtension = ".cadence-reference-plugin";
    public const string ManifestFormat = "cadence-reference-plugin";
    public const string CrashMarker = "#cadence-test: crash-scanner";
    public const string HangMarker = "#cadence-test: hang-scanner";
    public const int MaxModuleBytes = 64 * 1024;

    public static ScanResult Scan(string path)
    {
        if (!path.EndsWith(ModuleExtension, StringComparison.OrdinalIgnoreCase))
        {
            return Malformed(path, $"Not a Cadence reference plugin module (expected a {ModuleExtension} file).");
        }

        string text;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Malformed(path, "The module file does not exist.");
            }

            if (info.Length > MaxModuleBytes)
            {
                return Malformed(path, "The module file is too large to be a manifest.");
            }

            text = File.ReadAllText(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Malformed(path, $"The module file cannot be read: {ex.Message}");
        }

        var firstLine = text.Split('\n', 2)[0].TrimEnd('\r');
        if (firstLine == CrashMarker)
        {
            Environment.FailFast("Cadence plugin scanner: crash induced by the test marker in " + path);
        }

        if (firstLine == HangMarker)
        {
            Thread.Sleep(Timeout.Infinite);
        }

        try
        {
            return new ScanResult(path, ParseManifest(text), null, null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidDataException or ArgumentException or InvalidOperationException or FormatException)
        {
            return Malformed(path, $"The manifest is invalid: {ex.Message}");
        }
    }

    private static ImmutableArray<PluginIdentity> ParseManifest(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
        var root = document.RootElement;
        if (root.GetProperty("format").GetString() != ManifestFormat)
        {
            throw new InvalidDataException($"'format' must be '{ManifestFormat}'.");
        }

        if (root.GetProperty("version").GetInt32() != 1)
        {
            throw new InvalidDataException("Unsupported manifest version.");
        }

        var moduleId = Required(root, "moduleId");
        var plugins = root.GetProperty("plugins");
        if (plugins.GetArrayLength() is 0 or > ProtocolLimits.MaxPluginsPerModule)
        {
            throw new InvalidDataException("A module must declare between 1 and 1024 plugins.");
        }

        var result = ImmutableArray.CreateBuilder<PluginIdentity>();
        foreach (var plugin in plugins.EnumerateArray())
        {
            var kind = Enum.Parse<PluginKind>(Required(plugin, "kind"), ignoreCase: false);
            if (!Enum.IsDefined(kind))
            {
                throw new InvalidDataException("Unknown plugin kind.");
            }

            result.Add(new PluginIdentity(
                ReferencePlugin.Format,
                moduleId,
                Required(plugin, "pluginId"),
                Required(plugin, "name"),
                plugin.TryGetProperty("vendor", out var vendor) ? vendor.GetString() ?? string.Empty : string.Empty,
                kind,
                plugin.TryGetProperty("version", out var version) ? version.GetString() ?? string.Empty : string.Empty));
        }

        return result.ToImmutable();
    }

    private static string Required(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetString();
        return string.IsNullOrEmpty(value) ? throw new InvalidDataException($"'{name}' is required.") : value;
    }

    private static ScanResult Malformed(string path, string message) => new(path, [], ScanFailureKind.Malformed, message);
}
