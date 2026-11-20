using System.Net;
using System.Text;
using Gml.Web.Skin.Service.Core.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Gml.Web.Skin.Service.Tests;

/// <summary>
///     Player heads for players without an own skin come from Mojang. Every request for a player
///     without a Mojang account asked Mojang again (slow heads), and a refreshed skin was written
///     in place while other requests read it, which failed them with 500.
/// </summary>
public class MojangSkinCacheTests
{
    private string _dir = null!;
    private string _default = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "mojang-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _default = Path.Combine(_dir, "default.png");
        File.WriteAllBytes(_default, Png());
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    private static byte[] Png(int size = 64)
    {
        using var stream = new MemoryStream();
        using (var image = new Image<Rgba32>(size, size))
            image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private MojangSkinCache Cache(Func<HttpRequestMessage, HttpResponseMessage> respond, TimeSpan duration) =>
        new(new HttpClient(new StubHandler(respond)), Path.Combine(_dir, "cache"), _default, duration);

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Func<HttpRequestMessage, HttpResponseMessage> MojangWithSkin(Func<byte[]> skin) => request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("/users/profiles/minecraft/"))
            return Json("""{"id":"abc","name":"Notch"}""");
        if (url.Contains("/session/minecraft/profile/"))
        {
            var textures = Convert.ToBase64String(
                Encoding.UTF8.GetBytes("""{"textures":{"SKIN":{"url":"http://textures.test/skin.png"}}}"""));
            return Json($$"""{"properties":[{"name":"textures","value":"{{textures}}"}]}""");
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(skin()) };
    };

    [Test]
    public async Task PlayerWithoutMojangAccount_IsNotLookedUpOnEveryRequest()
    {
        var calls = 0;
        var cache = Cache(_ =>
        {
            Interlocked.Increment(ref calls);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }, TimeSpan.FromHours(1));

        var first = await cache.GetSkinPathAsync("OfflinePlayer");
        var second = await cache.GetSkinPathAsync("OfflinePlayer");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(_default));
            Assert.That(second, Is.EqualTo(_default));
            Assert.That(calls, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task SkinThatIsNotAnImage_IsNotCached()
    {
        var cache = Cache(MojangWithSkin(() => "<html>rate limited</html>"u8.ToArray()), TimeSpan.FromHours(1));

        var path = await cache.GetSkinPathAsync("Notch");

        Assert.That(path, Is.EqualTo(_default));
    }

    [Test]
    public async Task RefreshWhileOthersReadTheSkin_NeverBreaksAReader()
    {
        var a = Png(64);
        var b = Png(128);
        var flip = 0;
        var cache = Cache(MojangWithSkin(() => Interlocked.Increment(ref flip) % 2 == 0 ? a : b), TimeSpan.Zero);
        await cache.GetSkinPathAsync("Notch");

        var requests = Enumerable.Range(0, 60).Select(_ => Task.Run(async () =>
        {
            var path = await cache.GetSkinPathAsync("Notch");
            using var image = TextureFile.LoadImage(path);
            return image.Width;
        }));

        var widths = await Task.WhenAll(requests);

        Assert.That(widths, Is.All.EqualTo(64).Or.EqualTo(128));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
