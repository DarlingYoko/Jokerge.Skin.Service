using AutoMapper;
using Gml.Web.Skin.Service.Core.Cloudflare;
using Gml.Web.Skin.Service.Models.Dto;
using Microsoft.AspNetCore.Mvc;

namespace Gml.Web.Skin.Service.Core.Requests;

internal abstract class TextureRequests
{
    // Generous headroom over the largest legitimate skin (SkinFullHD, >512px): a decoded
    // 4096x4096 32bpp bitmap is ~64MB in memory, which is still cheap, but blocks a crafted
    // tiny-file/huge-dimension PNG from ballooning far past that on decode.
    private const long MaxUploadBytes = 10 * 1024 * 1024;
    private const int MaxImageDimension = 4096;

    /// <summary>
    /// Reads just the image header (no full decode) to reject an oversized skin/cloak
    /// before it's ever fully decoded — a decompression-bomb guard. Deletes filePath and
    /// returns a validation error message on rejection, otherwise null.
    /// </summary>
    private static string? ValidateImageDimensionsOrDelete(string filePath)
    {
        try
        {
            var info = Image.Identify(filePath);
            if (info is null)
            {
                File.Delete(filePath);
                return "Uploaded file is not a valid image.";
            }

            if (info.Width > MaxImageDimension || info.Height > MaxImageDimension)
            {
                File.Delete(filePath);
                return $"Image dimensions {info.Width}x{info.Height} exceed the {MaxImageDimension}x{MaxImageDimension} limit.";
            }

            return null;
        }
        catch (Exception)
        {
            File.Delete(filePath);
            return "Uploaded file is not a valid image.";
        }
    }

    internal static async Task<IResult> LoadSkin(HttpRequest request, [FromForm] IFormFile file, IMapper mapper,
        string userName)
    {
        if (file.Length == 0)
            return Results.BadRequest();

        if (file.Length > MaxUploadBytes)
            return Results.BadRequest($"File exceeds the {MaxUploadBytes / (1024 * 1024)}MB upload limit.");

        try
        {
            userName = SkinHelper.SanitizeUserName(userName);
            var tempFile = Path.Combine(SkinHelper.SkinTextureDirectory, $"{userName}.png");

            // Если файл существует, удаляем его
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            // Создаем новый файл и копируем в него данные
            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(fileStream);
            }

            var dimensionError = ValidateImageDimensionsOrDelete(tempFile);
            if (dimensionError is not null)
                return Results.BadRequest(dimensionError);

            var texture = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);
            return Results.Ok(mapper.Map<UserTextureReadDto>(texture));
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error saving skin: {ex.Message}");
        }
    }

    internal static async Task<IResult> DeleteSkin(HttpRequest request, IMapper mapper,
        string userName)
    {
        try
        {
            userName = SkinHelper.SanitizeUserName(userName);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        var filePath = Path.Combine(SkinHelper.SkinTextureDirectory, $"{userName}.png");

        if (!File.Exists(filePath))
            return Results.NotFound();

        File.Delete(filePath);
        return Results.Ok();

    }

    internal static async Task<IResult> LoadCloak(HttpRequest request, [FromForm] IFormFile file, IMapper mapper,
        string userName)
    {
        if (file.Length == 0)
            return Results.BadRequest();

        if (file.Length > MaxUploadBytes)
            return Results.BadRequest($"File exceeds the {MaxUploadBytes / (1024 * 1024)}MB upload limit.");

        try
        {
            userName = SkinHelper.SanitizeUserName(userName);
            var tempFile = Path.Combine(SkinHelper.CloakTextureDirectory, $"{userName}.png");

            // Если файл существует, удаляем его
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }

            // Создаем новый файл и копируем в него данные
            using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await file.CopyToAsync(fileStream);
            }

            var dimensionError = ValidateImageDimensionsOrDelete(tempFile);
            if (dimensionError is not null)
                return Results.BadRequest(dimensionError);

            var texture = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);
            return Results.Ok(mapper.Map<UserTextureReadDto>(texture));
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Error saving cloak: {ex.Message}");
        }
    }

    internal static async Task<IResult> DeleteCloak(HttpRequest request, IMapper mapper,
        string userName)
    {
        try
        {
            userName = SkinHelper.SanitizeUserName(userName);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        var filePath = Path.Combine(SkinHelper.CloakTextureDirectory, $"{userName}.png");

        if (!File.Exists(filePath))
            return Results.NotFound();

        File.Delete(filePath);
        return Results.Ok();
    }

    internal static async Task<IResult> GetSkin(HttpRequest request, string userName, string? uuid)
    {
        try
        {
            userName = SkinHelper.SanitizeUserName(userName);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        return Results.File(user.SkinFullPath, "image/png");
    }

    internal static async Task<IResult> GetUserTexture(HttpRequest request, IMapper mapper, string userName)
    {
        var texture = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        return Results.Ok(mapper.Map<UserTextureReadDto>(texture));
    }

    internal static async Task<IResult> RefreshCache(HttpRequest request, IMapper mapper, string userName)
    {
        var enpoints = new[]
        {
            $"/skin/{userName}",
            $"/skin/{userName}/head",
            $"/skin/{userName}/front",
            $"/skin/{userName}/back",
            $"/skin/{userName}/full-back",
            $"/cloak/{userName}"
        };

        await CloudflareUtil.ClearCacheAsync(enpoints);

        return Results.Ok();
    }

    internal static async Task<IResult> GetCloak(HttpRequest request, string userName, int? size = 128)
    {
        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        if (!user.HasCloak) return Results.NotFound("Cloak not exists");

        var image = SkinViewer.SkinViewer.GetCloak(user.CloakFullPath, user, size ?? 128);

        return Results.File(image, "image/png");
    }

    internal static async Task<IResult> GetCloakTexture(HttpRequest request, string userName)
    {
        try
        {
            userName = SkinHelper.SanitizeUserName(userName);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(ex.Message);
        }

        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        if (!user.HasCloak) return Results.NotFound("Cloak not exists");

        return Results.File(user.CloakFullPath, "image/png");
    }

    internal static async Task<IResult> GetSkinHead(HttpRequest request, string userName, int size = 128)
    {
        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        var image = SkinViewer.SkinViewer.GetHead(user.SkinFullPath, user, size);

        return Results.File(image, "image/png");
    }

    internal static async Task<IResult> GetSkinFront(HttpRequest request, string userName, int size = 128)
    {
        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        var image = SkinViewer.SkinViewer.GetFront(user.SkinFullPath, user, size);

        return Results.File(image, "image/png");
    }

    internal static async Task<IResult> GetSkinBack(HttpRequest request, string userName, int size = 128)
    {
        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        var image = SkinViewer.SkinViewer.GetBack(user, size);

        return Results.File(image, "image/png");
    }

    internal static async Task<IResult> GetSkinAndCloakBack(HttpRequest request, string userName, int size = 128)
    {
        var user = await SkinHelper.CreateAsync($"{request.Scheme}://{request.Host.Value}", userName);

        var image = SkinViewer.SkinViewer.GetBack(user, size, true);

        return Results.File(image, "image/png");
    }
}
