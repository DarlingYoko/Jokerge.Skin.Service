using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gml.Web.Skin.Service.Core.Storage;

/// <summary>
///     Resolves a locally cached copy of a player's official Mojang skin (api.mojang.com +
///     sessionserver.mojang.com), falling back to the bundled default skin if the player has no
///     Mojang account, has no skin set, or the lookup fails.
/// </summary>
/// <remarks>
///     Most players of an offline-mode server have no Mojang account, so a miss is remembered
///     for the cache duration instead of asking Mojang on every head request. A downloaded skin
///     is validated and swapped in with <see cref="TextureFile" />, because requests read the
///     cached file while it is being refreshed.
/// </remarks>
internal sealed class MojangSkinCache(
    HttpClient http,
    string cacheDirectory,
    string defaultSkinPath,
    TimeSpan cacheDuration)
{
    private readonly ConcurrentDictionary<string, DateTime> _missesUntil = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string> GetSkinPathAsync(string userName)
    {
        var cachedPath = Path.Combine(cacheDirectory, $"{userName}.png");
        var hasCached = File.Exists(cachedPath);

        if (hasCached && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachedPath) < cacheDuration)
            return cachedPath;

        if (_missesUntil.TryGetValue(userName, out var until) && DateTime.UtcNow < until)
            return hasCached ? cachedPath : defaultSkinPath;

        if (await TryDownloadAsync(userName, cachedPath))
        {
            _missesUntil.TryRemove(userName, out _);
            return cachedPath;
        }

        // No account, no skin, junk from Mojang or Mojang unreachable/rate-limited: keep serving
        // the last cached copy if we have one, otherwise the bundled default skin.
        _missesUntil[userName] = DateTime.UtcNow + cacheDuration;
        return File.Exists(cachedPath) ? cachedPath : defaultSkinPath;
    }

    private async Task<bool> TryDownloadAsync(string userName, string cachedPath)
    {
        try
        {
            using var profileResponse = await http.GetAsync(
                $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(userName)}");
            if (profileResponse.StatusCode != HttpStatusCode.OK)
                return false;

            var profile = await profileResponse.Content.ReadFromJsonAsync<MojangProfileDto>();
            if (string.IsNullOrEmpty(profile?.Id))
                return false;

            var session = await http.GetFromJsonAsync<MojangSessionDto>(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{profile.Id}");

            var texturesProperty = session?.Properties?.FirstOrDefault(p => p.Name == "textures");
            if (texturesProperty is null)
                return false;

            var decodedJson = Convert.FromBase64String(texturesProperty.Value);
            var payload = JsonSerializer.Deserialize<MojangTexturesPayload>(decodedJson);
            var skinUrl = payload?.Textures?.Skin?.Url;
            if (string.IsNullOrEmpty(skinUrl))
                return false;

            await using var skin = new MemoryStream(await http.GetByteArrayAsync(skinUrl));
            return await TextureFile.ReplaceAsync(skin, cachedPath, ValidateImage) is null;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or FormatException
                                              or NotSupportedException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private static string? ValidateImage(string path)
    {
        try
        {
            Image.Identify(path);
            return null;
        }
        catch (Exception exception) when (exception is ImageFormatException or UnknownImageFormatException)
        {
            return "Mojang returned a skin that is not an image.";
        }
    }

    private sealed class MojangProfileDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    private sealed class MojangSessionDto
    {
        [JsonPropertyName("properties")] public MojangSessionProperty[]? Properties { get; set; }
    }

    private sealed class MojangSessionProperty
    {
        [JsonPropertyName("name")] public string Name { get; set; } = null!;

        [JsonPropertyName("value")] public string Value { get; set; } = null!;
    }

    private sealed class MojangTexturesPayload
    {
        [JsonPropertyName("textures")] public MojangTextureMap? Textures { get; set; }
    }

    private sealed class MojangTextureMap
    {
        [JsonPropertyName("SKIN")] public MojangTextureEntry? Skin { get; set; }
    }

    private sealed class MojangTextureEntry
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
