using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NoClose.Platform;

/// <summary>Commits a directory's metadata (e.g. a just-renamed journal file) to disk.</summary>
internal static partial class DirectoryFlush
{
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareAll = 0x7; // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);

    /// <summary>Opens the directory (FILE_FLAG_BACKUP_SEMANTICS) and flushes it. Best effort; never throws.</summary>
    public static bool Flush(string dir)
    {
        try
        {
            using var h = CreateFileW(dir, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            return !h.IsInvalid && FlushFileBuffers(h);
        }
        catch
        {
            return false;
        }
    }
}
