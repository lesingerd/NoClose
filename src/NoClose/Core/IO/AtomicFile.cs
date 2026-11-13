using System.Text;

namespace NoClose.Core.IO;

/// <summary>Write-to-temp-then-replace file writer.</summary>
public static class AtomicFile
{
    /// <param name="afterReplace">
    /// Called with the directory after the rename. A same-volume rename is committed lazily by NTFS (MOVEFILE_WRITE_THROUGH only applies to
    /// copy-and-delete moves), so callers that need durability pass a platform directory flush here.
    /// </param>
    public static void WriteAllText(string path, string content, bool writeThrough, int retries = 3, int retryDelayMs = 50, Action<string>? afterReplace = null)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        var options = writeThrough ? FileOptions.WriteThrough : FileOptions.None;
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, options))
        {
            var bytes = new UTF8Encoding(false).GetBytes(content);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(true);
        }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, path, overwrite: true);
                break;
            }
            catch (IOException) when (attempt < retries)
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (UnauthorizedAccessException) when (attempt < retries)
            {
                Thread.Sleep(retryDelayMs);
            }
        }
        if (writeThrough && afterReplace != null && !string.IsNullOrEmpty(dir))
        {
            try { afterReplace(dir); } catch { /* best effort */ }
        }
    }

    public static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
