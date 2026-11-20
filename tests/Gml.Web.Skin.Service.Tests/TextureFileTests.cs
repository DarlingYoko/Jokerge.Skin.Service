using System.Text;
using Gml.Web.Skin.Service.Core.Storage;

namespace Gml.Web.Skin.Service.Tests;

/// <summary>
///     Uploads deleted the old texture and rewrote it in place with an exclusive lock, so a request
///     reading the same file at that moment failed with "being used by another process" (or found
///     no file, or half a file). Writes now go to a temp file that replaces the texture in one rename.
/// </summary>
public class TextureFileTests
{
    private string _dir = null!;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "skin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Test]
    public async Task Replace_ExistingTexture_SwapsContentAndLeavesNoTempFiles()
    {
        var target = Path.Combine(_dir, "player.png");
        await File.WriteAllTextAsync(target, "old");

        var error = await TextureFile.ReplaceAsync(Bytes("new"), target, _ => null);

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.Null);
            Assert.That(Encoding.UTF8.GetString(TextureFile.ReadAllBytes(target)), Is.EqualTo("new"));
            Assert.That(Directory.GetFiles(_dir), Is.EqualTo(new[] { target }));
        });
    }

    [Test]
    public async Task Replace_RejectedUpload_KeepsExistingTextureAndLeavesNoTempFiles()
    {
        var target = Path.Combine(_dir, "player.png");
        await File.WriteAllTextAsync(target, "old");

        var error = await TextureFile.ReplaceAsync(Bytes("bad"), target, _ => "not an image");

        Assert.Multiple(() =>
        {
            Assert.That(error, Is.EqualTo("not an image"));
            Assert.That(File.ReadAllText(target), Is.EqualTo("old"));
            Assert.That(Directory.GetFiles(_dir), Is.EqualTo(new[] { target }));
        });
    }

    [Test]
    public async Task Replace_ConcurrentWithReads_NeverFailsAndReadsWholeFiles()
    {
        var target = Path.Combine(_dir, "player.png");
        var a = new string('a', 64 * 1024);
        var b = new string('b', 64 * 1024);
        await File.WriteAllTextAsync(target, a);

        var writers = Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => TextureFile.ReplaceAsync(Bytes(i % 2 == 0 ? a : b), target, _ => null)));
        var readers = Enumerable.Range(0, 200).Select(_ =>
            Task.Run(() => Encoding.UTF8.GetString(TextureFile.ReadAllBytes(target))));

        var errors = await Task.WhenAll(writers);
        var reads = await Task.WhenAll(readers);

        Assert.Multiple(() =>
        {
            Assert.That(errors, Is.All.Null);
            Assert.That(reads, Is.All.EqualTo(a).Or.EqualTo(b));
            Assert.That(Directory.GetFiles(_dir), Is.EqualTo(new[] { target }));
        });
    }
}
