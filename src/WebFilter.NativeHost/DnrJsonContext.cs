using System.Text.Json.Serialization;
using WebFilter.Core.Dnr;

namespace WebFilter.NativeHost;

/// <summary>Source-generated DNR rule serialization (the host is published with NativeAOT).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DnrRule))]
internal sealed partial class DnrJsonContext : JsonSerializerContext;
