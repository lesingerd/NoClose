using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using NoClose.Core.Guards;
using NoClose.Core.IO;
using NoClose.Core.Journal;
using NoClose.Logging;

namespace NoClose.Journal;

/// <summary>
/// On-disk restore journal. Entries are written (atomically, write-through) BEFORE the change they describe is made.
/// All operations are serialised by one lock.
/// </summary>
internal sealed class JournalStore
{
    private readonly object _gate = new();
    private readonly ILogSink _log;
    private RestoreJournal? _current;

    public JournalStore(string path, ILogSink log, bool protectDirectory = false)
    {
        Path = path;
        _log = log;
        ProtectDirectory = protectDirectory;
    }

    public string Path { get; }
    private bool ProtectDirectory { get; }

    public static string DefaultPath(bool elevated)
    {
        var root = Environment.GetFolderPath(elevated ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(root, "NoClose", "restore-journal.json");
    }

    public static JournalStore ForCurrentProcess(bool elevated, ILogSink log) =>
        new(DefaultPath(elevated), log, protectDirectory: elevated);

    // ---- loading ---------------------------------------------------------------------------------------------

    public static RestoreJournal? TryLoad(string path, out string? error) => TryLoad(path, out error, out _);

    /// <summary>
    /// Loads the journal. If the main file is missing or older than a complete, parseable leftover <c>.tmp</c> (a rename that was lost
    /// to a power failure) the .tmp content wins. <paramref name="accessDenied"/> is set when the file exists but cannot be read.
    /// </summary>
    public static RestoreJournal? TryLoad(string path, out string? error, out bool accessDenied)
    {
        error = null;
        accessDenied = false;
        RestoreJournal? main = null;
        string? mainError = null;
        try
        {
            if (File.Exists(path)) main = JournalSerializer.Deserialize(File.ReadAllText(path));
        }
        catch (UnauthorizedAccessException ex)
        {
            accessDenied = true;
            mainError = ex.Message;
        }
        catch (Exception ex) when (ex is JournalFormatException or IOException)
        {
            mainError = ex.Message;
        }

        RestoreJournal? tmp = null;
        try
        {
            var tmpPath = path + ".tmp";
            if (File.Exists(tmpPath)) tmp = JournalSerializer.Deserialize(File.ReadAllText(tmpPath));
        }
        catch (Exception ex) when (ex is JournalFormatException or IOException or UnauthorizedAccessException)
        {
            tmp = null; // a partially written temp file is simply ignored
        }

        if (tmp != null && (main == null || tmp.Sequence > main.Sequence))
        {
            error = null;
            accessDenied = false;
            return tmp;
        }
        if (main == null) error = mainError;
        return main;
    }

    /// <summary>True when the path exists but access is denied (File.Exists reports false for that case).</summary>
    public static bool ExistsButDenied(string path)
    {
        try
        {
            File.GetAttributes(path);
            return false; // readable attributes: File.Exists already told the truth
        }
        catch (UnauthorizedAccessException) { return true; }
        catch (Exception) { return false; }
    }

    // ---- recording ------------------------------------------------------------------------------------------

    private RestoreJournal Ensure()
    {
        if (_current != null) return _current;
        using var id = WindowsIdentity.GetCurrent();
        var p = Process.GetCurrentProcess();
        _current = new RestoreJournal
        {
            SessionId = p.SessionId,
            ProcessId = p.Id,
            ProcessStartUtc = p.StartTime.ToUniversalTime(),
            Machine = Environment.MachineName,
            UserSid = id.User?.Value ?? "",
            CreatedUtc = DateTime.UtcNow,
        };
        return _current;
    }

    /// <summary>Marks the entry Pending, stores it, and flushes the journal to disk. Call BEFORE making the change.</summary>
    public void Record(JournalEntry entry)
    {
        lock (_gate)
        {
            var j = Ensure();
            entry.Status = JournalEntryStatus.Pending;
            if (entry.RecordedUtc == default) entry.RecordedUtc = DateTime.UtcNow;
            j.Entries.Add(entry);
            SaveLocked();
            _log.Info(LogCategory.Journal, $"Recorded {entry.Kind} for {entry.Guard} ({Describe(entry)}) [{entry.Id[..8]}]");
        }
    }

    public void MarkApplied(string id, JournalValue? applied = null)
    {
        lock (_gate)
        {
            var e = Find(id);
            if (e == null) return;
            if (applied != null) e.Applied = applied;
            e.Status = JournalEntryStatus.Applied;
            SaveLocked();
        }
    }

    public void MarkRestored(string id)
    {
        lock (_gate)
        {
            var e = Find(id);
            if (e == null) return;
            e.Status = JournalEntryStatus.Restored;
            SaveLocked();
            _log.Info(LogCategory.Journal, $"Restored {e.Kind} for {e.Guard} [{e.Id[..8]}]");
        }
    }

    public void MarkFailed(string id)
    {
        lock (_gate)
        {
            var e = Find(id);
            if (e == null) return;
            e.Status = JournalEntryStatus.Failed;
            SaveLocked();
        }
    }

    /// <summary>Updates a recorded entry and flushes (used to add the temporary scheme GUID).</summary>
    public void Update(string id, Action<JournalEntry> change)
    {
        lock (_gate)
        {
            var e = Find(id);
            if (e == null) return;
            change(e);
            SaveLocked();
        }
    }

    public IReadOnlyList<JournalEntry> Entries(GuardId? guard = null)
    {
        lock (_gate)
        {
            if (_current == null) return Array.Empty<JournalEntry>();
            return _current.Entries.Where(e => guard == null || e.Guard == guard).ToList();
        }
    }

    public IReadOnlyList<JournalEntry> OpenEntries(GuardId? guard = null) =>
        Entries(guard).Where(e => e.Status != JournalEntryStatus.Restored).ToList();

    public bool HasOpenEntries => OpenEntries().Count > 0;

    /// <summary>Snapshot for a restore pass (a deep-enough copy is not needed: planner only reads).</summary>
    public RestoreJournal Snapshot()
    {
        lock (_gate) return _current ?? Ensure();
    }

    /// <summary>Replaces the in-memory journal with one loaded from disk (stale recovery).</summary>
    public void Adopt(RestoreJournal journal)
    {
        lock (_gate) _current = journal;
    }

    /// <summary>Deletes the file (and tmp) if every entry is restored or there are no entries.</summary>
    public void DeleteIfEmpty()
    {
        lock (_gate)
        {
            if (_current == null || _current.AllRestored)
            {
                AtomicFile.DeleteQuietly(Path);
                AtomicFile.DeleteQuietly(Path + ".tmp");
                _current = null;
            }
        }
    }

    private JournalEntry? Find(string id) => _current?.Entries.FirstOrDefault(e => e.Id == id);

    private void SaveLocked()
    {
        var j = _current;
        if (j == null) return;
        if (j.AllRestored)
        {
            AtomicFile.DeleteQuietly(Path);
            AtomicFile.DeleteQuietly(Path + ".tmp");
            return;
        }
        EnsureDirectory();
        j.Sequence++;
        AtomicFile.WriteAllText(Path, JournalSerializer.Serialize(j), writeThrough: true, afterReplace: d => Platform.DirectoryFlush.Flush(d));
    }

    private bool _aclChecked;

    /// <summary>
    /// DACL of %ProgramData%\NoClose: Administrators and SYSTEM have full control; BUILTIN\Users may only read and list (inherited), so a
    /// non-elevated NoClose can see and parse an elevated journal and report "needs elevation" instead of silently ignoring it.
    /// </summary>
    public static DirectorySecurity BuildProtectedSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private void EnsureDirectory()
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (string.IsNullOrEmpty(dir)) return;
        if (!ProtectDirectory)
        {
            Directory.CreateDirectory(dir);
            return;
        }
        var security = BuildProtectedSecurity();
        if (!Directory.Exists(dir))
        {
            try
            {
                new DirectoryInfo(dir).Create(security);
                _aclChecked = true;
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                _log.Warn(LogCategory.Journal, "Could not create protected journal directory: " + ex.Message);
                Directory.CreateDirectory(dir);
                return;
            }
        }
        if (_aclChecked) return;
        _aclChecked = true;
        try
        {
            // A directory created by an older build had no read access for Users: bring it up to the current DACL (once per process).
            new DirectoryInfo(dir).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _log.Warn(LogCategory.Journal, "Could not update the journal directory permissions: " + ex.Message);
        }
    }

    public static string Describe(JournalEntry e) => e.Kind switch
    {
        JournalKind.RegistryValue => $"{e.Target.Hive}\\{e.Target.Key} [{e.Target.Name}]",
        JournalKind.PowerActiveScheme => $"scheme {e.Target.OriginalScheme}",
        JournalKind.SpiScreenSaveActive => "screen saver active flag",
        _ => e.Kind.ToString()
    };
}
