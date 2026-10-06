using System.Text;
using System.Text.Json.Nodes;
using Cadence.Domain.Projects;
using Cadence.Infrastructure.Projects;
using CsCheck;

namespace Cadence.Tests.Unit.Infrastructure;

public sealed class ProjectSerializerTests
{
    private static readonly ProjectSerializer Serializer = ProjectSerializer.Default;

    private static JsonObject ToJson(Project project) => JsonNode.Parse(Serializer.Serialize(new ProjectDocument(project)))!.AsObject();

    private static ProjectDocument FromJson(JsonObject json) => Serializer.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

    [Fact]
    public void RoundTrip_PreservesEverything()
    {
        var project = ProjectSamples.Full();

        var restored = Serializer.Deserialize(Serializer.Serialize(new ProjectDocument(project))).Project;

        Assert.Equal(ProjectSamples.Describe(project), ProjectSamples.Describe(restored));
    }

    [Fact]
    public void Serialize_IsDeterministicAndHumanReadable()
    {
        var document = new ProjectDocument(ProjectSamples.Full());

        var first = Serializer.Serialize(document);
        var text = Encoding.UTF8.GetString(first);

        Assert.Equal(first, Serializer.Serialize(document));
        Assert.StartsWith("{\n  \"format\": \"cadence-project\",\n  \"formatVersion\": 2,", text, StringComparison.Ordinal);
        Assert.Contains("\"bytes\": \"F0 43 10 4C 00 00 7E 00 F7\"", text, StringComparison.Ordinal);
        Assert.Contains("\"channel\": 2", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownTopLevelDataAndExtensions_AreKept()
    {
        var json = ToJson(Project.CreateNew());
        json["extensions"] = JsonNode.Parse("""{ "org.example.ui": { "zoom": 2 } }""");
        json["futureThing"] = 42;

        var document = FromJson(json);
        var again = JsonNode.Parse(Serializer.Serialize(document))!.AsObject();

        Assert.Equal(2, (int)again["extensions"]!["org.example.ui"]!["zoom"]!);
        Assert.Equal(42, (int)again["futureThing"]!);
    }

    [Fact]
    public void NewerFormat_IsRefusedWithAClearMessage()
    {
        var json = ToJson(Project.CreateNew());
        json["formatVersion"] = 9;

        var error = Assert.Throws<ProjectVersionException>(() => FromJson(json));

        Assert.Equal(9, error.FileVersion);
        Assert.Contains("newer version of Cadence", error.Message, StringComparison.Ordinal);
    }

    private sealed class RenameTitleMigration : IProjectMigration
    {
        public int FromVersion => ProjectSerializer.CurrentFormatVersion;

        public void Migrate(JsonObject root)
        {
            var project = root["project"]!.AsObject();
            project["name"] = (string)project["title"]!;
            project.Remove("title");
        }
    }

    [Fact]
    public void OlderFormats_AreMigratedBeforeReading()
    {
        var json = ToJson(Project.CreateNew("ignored"));
        var project = json["project"]!.AsObject();
        project.Remove("name");
        project["title"] = "From the current format";
        var next = ProjectSerializer.CurrentFormatVersion + 1;
        var serializerWithNext = new ProjectSerializer(next, [new RenameTitleMigration()]);

        var document = serializerWithNext.Deserialize(Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Equal("From the current format", document.Project.Name);
        Assert.Contains($"\"formatVersion\": {next}", Encoding.UTF8.GetString(serializerWithNext.Serialize(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMigration_IsReported()
    {
        var json = ToJson(Project.CreateNew());
        json["formatVersion"] = 0;

        Assert.Contains("format 0 is not supported", Assert.Throws<ProjectFormatException>(() => FromJson(json)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$.project.sequence.ppqn", "0")]
    [InlineData("$.project.sequence.tracks[0].events[2].velocity", "0")]
    [InlineData("$.project.sequence.tracks[0].events[2].channel", "17")]
    [InlineData("$.project.sequence.tracks[0].events[0].type", "\"lyric\"")]
    [InlineData("$.project.sequence.tracks[0].events[1].program", "0")]
    [InlineData("$.project.sequence.tracks[0].events[3].value", "4294967296")]
    [InlineData("$.project.sequence.tracks[0].events[0].bytes", "\"F0 43 99 F7\"")]
    [InlineData("$.project.sequence.tracks[0].id", "\"not-a-guid\"")]
    [InlineData("$.project.sequence.meter[0].denominator", "3")]
    [InlineData("$.project.routing[0].transpose", "99")]
    [InlineData("$.project.loop.end", "0")]
    public void InvalidValues_AreReportedWithTheirPath(string path, string replacement)
    {
        var json = ToJson(ProjectSamples.Full());
        Set(json, path, JsonNode.Parse(replacement));

        var error = Assert.Throws<ProjectFormatException>(() => FromJson(json));

        Assert.StartsWith(path[..path.LastIndexOf('.')], error.JsonPath, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePropertyNames_AreFormatErrors() =>
        Assert.Throws<ProjectFormatException>(() => Serializer.Deserialize("{ \"format\": \"cadence-project\", \"format\": \"x\" }"u8));

    [Fact]
    public void DuplicateIds_AreRejected()
    {
        var json = ToJson(ProjectSamples.Full());
        var events = json["project"]!["sequence"]!["tracks"]![0]!["events"]!.AsArray();
        events[1]!["id"] = (string)events[0]!["id"]!;

        Assert.Contains("more than once", Assert.Throws<ProjectFormatException>(() => FromJson(json)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{ \"format\": \"cadence-device-profile\", \"formatVersion\": 1 }")]
    [InlineData("{ \"format\": \"cadence-project\" }")]
    public void NonProjects_AreRejected(string text) =>
        Assert.Throws<ProjectFormatException>(() => Serializer.Deserialize(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void MutatedFiles_FailOnlyWithFormatErrors()
    {
        var bytes = Serializer.Serialize(new ProjectDocument(ProjectSamples.Full()));
        Gen.Select(Gen.Int[0, bytes.Length - 1], Gen.Byte).Array[1, 4].Sample(edits =>
        {
            var copy = (byte[])bytes.Clone();
            foreach (var (index, value) in edits)
            {
                copy[index] = value;
            }

            try
            {
                Serializer.Deserialize(copy);
            }
            catch (ProjectFormatException)
            {
            }

            return true;
        }, iter: 2000);
    }

    private static void Set(JsonObject root, string path, JsonNode? value)
    {
        JsonNode node = root;
        var parts = path[2..].Replace("[", ".[", StringComparison.Ordinal).Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            node = parts[i].StartsWith('[') ? node[int.Parse(parts[i][1..^1], System.Globalization.CultureInfo.InvariantCulture)]! : node[parts[i]]!;
        }

        node.AsObject()[parts[^1]] = value;
    }
}
