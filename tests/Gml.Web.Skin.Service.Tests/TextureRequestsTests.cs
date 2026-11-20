using AutoMapper;
using Gml.Web.Skin.Service.Core.Mapper;
using Gml.Web.Skin.Service.Core.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gml.Web.Skin.Service.Tests;

/// <summary>
///     Upload and texture-info handlers against a real storage folder. SkinHelper resolves its
///     folders from the current directory once, so the whole fixture shares one temp folder.
/// </summary>
[NonParallelizable]
public class TextureRequestsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "skin-requests-" + Guid.NewGuid().ToString("N"));
    private IMapper _mapper = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Directory.CreateDirectory(Root);
        Environment.CurrentDirectory = Root;
        Directory.CreateDirectory(SkinHelper.SkinTextureDirectory);
        Directory.CreateDirectory(SkinHelper.CloakTextureDirectory);
        _mapper = new MapperConfiguration(cfg => cfg.AddProfile<TextureMapper>(), NullLoggerFactory.Instance)
            .CreateMapper();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        Environment.CurrentDirectory = Path.GetTempPath();
        Directory.Delete(Root, true);
    }

    private static HttpRequest Request()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("skins.test");
        return context.Request;
    }

    private static IFormFile Png(int width, int height)
    {
        var stream = new MemoryStream();
        using (var image = new Image<Rgba32>(width, height))
            image.SaveAsPng(stream);
        stream.Position = 0;
        return new FormFile(stream, 0, stream.Length, "file", "texture.png");
    }

    [Test]
    public async Task LoadCloak_WhileSkinIsBeingReplaced_Succeeds()
    {
        await TextureRequests.LoadSkin(Request(), Png(64, 64), _mapper, "Racer");

        // A cloak upload re-reads the player's skin; a skin upload for the same player at that
        // moment used to hold the skin file exclusively.
        var uploads = Enumerable.Range(0, 20).SelectMany(_ => new[]
        {
            Task.Run(() => TextureRequests.LoadSkin(Request(), Png(64, 64), _mapper, "Racer")),
            Task.Run(() => TextureRequests.LoadCloak(Request(), Png(64, 32), _mapper, "Racer"))
        });

        var results = await Task.WhenAll(uploads);

        Assert.Multiple(() =>
        {
            Assert.That(results.OfType<BadRequest<string>>().Select(r => r.Value), Is.Empty);
            Assert.That(results.Select(r => r.GetType().Name), Is.All.StartsWith("Ok"));
        });
    }

    [Test]
    public async Task LoadSkin_NotAnImage_KeepsPreviousSkin()
    {
        await TextureRequests.LoadSkin(Request(), Png(64, 64), _mapper, "Keeper");
        var skin = Path.Combine(SkinHelper.SkinTextureDirectory, "Keeper.png");
        var before = await File.ReadAllBytesAsync(skin);

        var junk = new MemoryStream("not a png"u8.ToArray());
        var result = await TextureRequests.LoadSkin(Request(), new FormFile(junk, 0, junk.Length, "file", "x.png"),
            _mapper, "Keeper");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.TypeOf<BadRequest<string>>());
            Assert.That(File.ReadAllBytes(skin), Is.EqualTo(before));
        });
    }

    [Test]
    public async Task GetUserTexture_InvalidUserName_IsBadRequestNotServerError()
    {
        var result = await TextureRequests.GetUserTexture(Request(), _mapper, "bad name!");

        Assert.That(result, Is.TypeOf<BadRequest<string>>());
    }
}
