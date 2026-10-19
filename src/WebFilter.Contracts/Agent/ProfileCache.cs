using System.Text.Json;
using System.Text.Json.Serialization;
using WebFilter.Contracts.Hub;

namespace WebFilter.Contracts.Agent;

/// <summary>
/// What the native host may know about one account while the service is down (MVP-PLAN §4.2 ProfileCache):
/// written by the service to <c>%ProgramData%\WebFilter\profiles\{accountId}.json</c>, readable only by that account.
/// </summary>
public sealed record ProfileCache(int SettingsVersion, string AccountId, bool Restricted, IReadOnlyList<RuleDto> AlwaysAllowed);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProfileCache))]
public sealed partial class ProfileCacheJsonContext : JsonSerializerContext;

public static class ProfileCacheJson
{
    public static string Serialize(ProfileCache cache) => JsonSerializer.Serialize(cache, ProfileCacheJsonContext.Default.ProfileCache);

    public static ProfileCache? Deserialize(string json) => JsonSerializer.Deserialize(json, ProfileCacheJsonContext.Default.ProfileCache);

    public static string FileName(string accountId) => $"{accountId}.json";
}
