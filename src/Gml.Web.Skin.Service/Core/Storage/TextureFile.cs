using SixLabors.ImageSharp.PixelFormats;

namespace Gml.Web.Skin.Service.Core.Storage;

/// <summary>
///     Texture files are read by many requests while uploads replace them. Uploads used to delete
///     the file and rewrite it in place under an exclusive lock, so a concurrent read failed with
///     "being used by another process", or found no file or half a file.
/// </summary>
/// <remarks>
///     An upload is written to a temp file and swapped in with one rename. Reads take the whole
///     file into memory (textures are a few KB, uploads are capped) under a per-file read lock that
///     the swap briefly takes for writing: Windows refuses to rename over an open file, and the lock
///     keeps the swap from racing an in-flight read on any OS.
/// </remarks>
public static class TextureFile
{
    private const string TempSuffix = ".upload";
    private static readonly ReaderWriterLockSlim[] Locks =
        Enumerable.Range(0, 64).Select(_ => new ReaderWriterLockSlim()).ToArray();

    private static ReaderWriterLockSlim LockFor(string path) =>
        Locks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(Path.GetFullPath(path)) % Locks.Length];

    /// <summary>
    ///     Writes <paramref name="source" /> to a temp file next to <paramref name="targetPath" />,
    ///     runs <paramref name="validate" /> on it and, if it returns null, swaps it in.
    /// </summary>
    /// <returns>The validation error, or null when the texture was replaced.</returns>
    public static async Task<string?> ReplaceAsync(Stream source, string targetPath, Func<string, string?> validate)
    {
        var directory = Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}{TempSuffix}");

        try
        {
            await using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await source.CopyToAsync(temp);

            var error = validate(tempPath);
            if (error is not null)
                return error;

            await SwapInAsync(tempPath, targetPath);
            return null;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    /// <summary>
    ///     On Windows a file that was just written can be held for a moment by an antivirus scan,
    ///     so the rename is retried briefly before giving up.
    /// </summary>
    private static async Task SwapInAsync(string tempPath, string targetPath)
    {
        const int attempts = 5;
        var gate = LockFor(targetPath);

        for (var attempt = 1; ; attempt++)
        {
            gate.EnterWriteLock();
            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
                return;
            }
            catch (Exception exception) when (attempt < attempts &&
                                              exception is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                gate.ExitWriteLock();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
        }
    }

    public static byte[] ReadAllBytes(string path)
    {
        var gate = LockFor(path);
        gate.EnterReadLock();
        try
        {
            return File.ReadAllBytes(path);
        }
        finally
        {
            gate.ExitReadLock();
        }
    }

    public static Image LoadImage(string path) => Image.Load(ReadAllBytes(path));

    public static Image<Rgba32> LoadRgba(string path) => Image.Load<Rgba32>(ReadAllBytes(path));
}
