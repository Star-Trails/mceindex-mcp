using System.Text.Json.Serialization;
using MceIndex.Mcp.Domain;

namespace MceIndex.Mcp.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(PageSnapshot))]
[JsonSerializable(typeof(PageSnapshot[]))]
[JsonSerializable(typeof(ChartData[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
internal sealed partial class MceJsonContext : JsonSerializerContext;
