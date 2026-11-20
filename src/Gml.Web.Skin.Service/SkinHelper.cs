using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Gml.Web.Skin.Service.Core.Storage;
using Gml.Web.Skin.Service.Models;
using Gml.Web.Skin.Service.Models.Dto;
using SixLabors.ImageSharp.PixelFormats;

namespace Gml.Web.Skin.Service;

public abstract class SkinHelper
{
    private const string DefaultSkinName = "default.png";
    private static readonly HttpClient MojangHttpClient = new();
    private static readonly TimeSpan MojangCacheDuration = TimeSpan.FromHours(1);

    // Minecraft usernames are 1-16 chars of [A-Za-z0-9_]; the extra couple chars and
    // hyphen give slack for the "s-"/"c-" texture-endpoint prefixes without opening up
    // path separators or traversal sequences.
    private static readonly Regex ValidUserNamePattern = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    /// <summary>
    /// Strips a leading "s-"/"c-" texture-endpoint prefix if present, then validates the
    /// result against a strict username charset. Every code path that turns a caller-supplied
    /// userName into a filesystem path (read, write, or delete) must go through this first —
    /// skipping it is a path-traversal / arbitrary-file vulnerability.
    /// </summary>
    public static string SanitizeUserName(string userName)
    {
        if (string.IsNullOrEmpty(userName))
            throw new ArgumentException("Username is required.", nameof(userName));

        if (userName.Length > 2 &&
            (userName.StartsWith("s-", StringComparison.Ordinal) ||
             userName.StartsWith("c-", StringComparison.Ordinal)))
            userName = userName[2..];

        if (!ValidUserNamePattern.IsMatch(userName))
            throw new ArgumentException("Invalid username.", nameof(userName));

        return userName;
    }

    public static string SkinTextureDirectory { get; } =
        Path.Combine(Environment.CurrentDirectory, "Storage", "Skins");

    public static string CloakTextureDirectory { get; } =
        Path.Combine(Environment.CurrentDirectory, "Storage", "Cloak");

    public static string MojangCacheDirectory { get; } =
        Path.Combine(Environment.CurrentDirectory, "Storage", "MojangCache");

    public static async Task<UserTexture> CreateAsync(string requestPathBase, string userName)
    {
        userName = SanitizeUserName(userName);
        var skinPath = Path.Combine(SkinTextureDirectory, $"{userName}.png");
        var cloakPath = Path.Combine(CloakTextureDirectory, $"{userName}.png");
        var hasCustomSkin = File.Exists(skinPath);

        if (!hasCustomSkin)
            skinPath = await GetMojangSkinPathAsync(userName);

        if (!File.Exists(cloakPath))
            cloakPath = string.Empty;

        var texture = new UserTexture
        {
            UserName = userName,
            HasSkin = hasCustomSkin,
            HasCloak = !string.IsNullOrEmpty(cloakPath),
            SkinUrl = $"{requestPathBase}/skin/s-{userName}",
            SkinFullPath = skinPath,
            CloakFullPath = string.IsNullOrEmpty(cloakPath)
                ? string.Empty
                : Path.Combine(Environment.CurrentDirectory, CloakTextureDirectory, $"{userName}.png"),
            ClockUrl = $"{requestPathBase}/cloak/c-{userName}",
            Texture = new SkinPartialsDto
            {
                Head = $"{requestPathBase}/skin/{userName}/head/128",
                Front = $"{requestPathBase}/skin/{userName}/front/128",
                Back = $"{requestPathBase}/skin/{userName}/back/128",
                CloakBack = $"{requestPathBase}/skin/{userName}/full-back/128",
                Cloak = $"{requestPathBase}/cloak/{userName}/128"
            }
        };

        using var inputImage = TextureFile.LoadRgba(texture.SkinFullPath);

        texture.SkinFormat = GetSkinFormat(inputImage.Width);
        texture.SkinType = SkinModelDetector.IsSlimModel(inputImage) ? SkinType.Slim : SkinType.Default;

        return texture;
    }

    /// <summary>
    /// Resolves a locally cached copy of the player's official Mojang skin (fetched via
    /// api.mojang.com + sessionserver.mojang.com), falling back to the bundled default skin
    /// if the player has no Mojang account, has no skin set, or the lookup fails.
    /// </summary>
    private static async Task<string> GetMojangSkinPathAsync(string userName)
    {
        var defaultPath = Path.Combine(SkinTextureDirectory, DefaultSkinName);
        var cachedPath = Path.Combine(MojangCacheDirectory, $"{userName}.png");

        if (File.Exists(cachedPath) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cachedPath) < MojangCacheDuration)
            return cachedPath;

        try
        {
            var profile = await MojangHttpClient.GetFromJsonAsync<MojangProfileDto>(
                $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(userName)}");

            if (string.IsNullOrEmpty(profile?.Id))
                return File.Exists(cachedPath) ? cachedPath : defaultPath;

            var session = await MojangHttpClient.GetFromJsonAsync<MojangSessionDto>(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{profile.Id}");

            var texturesProperty = session?.Properties?.FirstOrDefault(p => p.Name == "textures");

            if (texturesProperty is null)
                return File.Exists(cachedPath) ? cachedPath : defaultPath;

            var decodedJson = Convert.FromBase64String(texturesProperty.Value);
            var payload = JsonSerializer.Deserialize<MojangTexturesPayload>(decodedJson);
            var skinUrl = payload?.Textures?.Skin?.Url;

            if (string.IsNullOrEmpty(skinUrl))
                return File.Exists(cachedPath) ? cachedPath : defaultPath;

            var skinBytes = await MojangHttpClient.GetByteArrayAsync(skinUrl);

            if (!Directory.Exists(MojangCacheDirectory))
                Directory.CreateDirectory(MojangCacheDirectory);

            await File.WriteAllBytesAsync(cachedPath, skinBytes);

            return cachedPath;
        }
        catch
        {
            // Mojang API unreachable/rate-limited/user not found — keep serving the last
            // cached copy if we have one, otherwise fall back to the bundled default skin.
            return File.Exists(cachedPath) ? cachedPath : defaultPath;
        }
    }

    private static SkinFormat GetSkinFormat(double width)
    {
        return width switch
        {
            > 64 and < 1024 => SkinFormat.SkinHD,
            > 512 => SkinFormat.SkinFullHD,
            _ => SkinFormat.SkinSD
        };
    }

    public static string GetActualSkinPath(string userName, UserTexture user)
    {
        userName = SanitizeUserName(userName);
        var imagePath = Path.Combine(SkinTextureDirectory, $"{userName}.png");

        if (!user.HasSkin) imagePath = Path.Combine(SkinTextureDirectory, "default.png");

        return imagePath;
    }

    private class MojangProfileDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private class MojangSessionDto
    {
        [JsonPropertyName("properties")]
        public MojangSessionProperty[]? Properties { get; set; }
    }

    private class MojangSessionProperty
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = null!;

        [JsonPropertyName("value")]
        public string Value { get; set; } = null!;
    }

    private class MojangTexturesPayload
    {
        [JsonPropertyName("textures")]
        public MojangTextureMap? Textures { get; set; }
    }

    private class MojangTextureMap
    {
        [JsonPropertyName("SKIN")]
        public MojangTextureEntry? Skin { get; set; }
    }

    private class MojangTextureEntry
    {
        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}
