using System.Diagnostics;
using System.Text;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

/// <summary>
/// Console output for a WinExe. Uses the inherited standard output when there is one (redirected to a file or pipe by a scheduler, CI,
/// Start-Process -RedirectStandardOutput, ...), otherwise attaches to the parent console, and/or writes to a file.
/// A WinExe is not waited for by cmd.exe: callers that need the exit code use <c>start /wait</c> or <c>Start-Process -Wait</c>.
/// </summary>
internal static class ConsoleAttach
{
    private static bool _ready;
    private static bool _isConsole;

    /// <summary>What to do about stdout; pure so it can be tested.</summary>
    internal enum Plan { UseInherited, AttachParent }

    /// <param name="stdoutHandleValid">GetStdHandle returned a real handle.</param>
    /// <param name="fileType">GetFileType of that handle (0 = unknown, 1 = disk, 2 = char/console, 3 = pipe).</param>
    internal static Plan Decide(bool stdoutHandleValid, uint fileType) =>
        stdoutHandleValid && fileType != NativeConstants.FILE_TYPE_UNKNOWN ? Plan.UseInherited : Plan.AttachParent;

    private static bool UsableStdout(out uint fileType)
    {
        fileType = NativeConstants.FILE_TYPE_UNKNOWN;
        var h = Kernel32.GetStdHandle(NativeConstants.STD_OUTPUT_HANDLE);
        if (h == IntPtr.Zero || h == NativeConstants.INVALID_HANDLE_VALUE) return false;
        fileType = Kernel32.GetFileType(h);
        return fileType != NativeConstants.FILE_TYPE_UNKNOWN;
    }

    public static bool TryAttachParent()
    {
        if (_ready) return true;
        bool valid = UsableStdout(out var type);
        if (Decide(valid, type) == Plan.AttachParent)
        {
            if (!Kernel32.AttachConsole(NativeConstants.ATTACH_PARENT_PROCESS))
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                Trace.TraceWarning($"NoClose: AttachConsole(ATTACH_PARENT_PROCESS) failed, error {err}; no console output");
                return false;
            }
            valid = UsableStdout(out type);
            if (!valid) return false;
        }
        _isConsole = type == NativeConstants.FILE_TYPE_CHAR;
        var w = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(w);
        var e = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetError(e);
        // The parent prompt was already printed on a real console; a redirected file gets no stray blank first line.
        if (_isConsole) Console.WriteLine();
        _ready = true;
        return true;
    }

    /// <summary>Writes text to standard output / the attached console (if any) and, when given, to <paramref name="outPath"/>.</summary>
    public static void Print(string text, string? outPath = null)
    {
        bool console = TryAttachParent();
        if (console) Console.WriteLine(text);
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                var full = Path.GetFullPath(outPath);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, text + Environment.NewLine, new UTF8Encoding(true));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (console) Console.Error.WriteLine("Cannot write " + outPath + ": " + ex.Message);
            }
        }
    }
}
