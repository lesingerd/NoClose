using System.Runtime.InteropServices;
using NoClose.Core.Guards;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

internal sealed class PowerApiException : Exception
{
    public uint Code { get; }
    public PowerApiException(string api, uint code) : base($"{api} failed with Win32 error {code}") => Code = code;
    public bool IsAccessDenied => Code == NativeConstants.ERROR_ACCESS_DENIED;
}

/// <summary>Thin wrapper over the PowrProf scheme APIs. Functions return Win32 error codes; failures throw <see cref="PowerApiException"/>.</summary>
internal unsafe class PowerSchemeAccessor
{
    private static void Check(string api, uint rc)
    {
        if (rc != 0) throw new PowerApiException(api, rc);
    }

    private static Guid TakeGuid(IntPtr p)
    {
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { Kernel32.LocalFree(p); }
    }

    public virtual Guid GetActiveScheme()
    {
        Check(nameof(PowrProf.PowerGetActiveScheme), PowrProf.PowerGetActiveScheme(IntPtr.Zero, out var p));
        return TakeGuid(p);
    }

    public virtual Guid? TryGetActiveScheme()
    {
        try { return GetActiveScheme(); } catch (PowerApiException) { return null; }
    }

    public virtual void SetActiveScheme(Guid scheme) =>
        Check(nameof(PowrProf.PowerSetActiveScheme), PowrProf.PowerSetActiveScheme(IntPtr.Zero, in scheme));

    public virtual Guid DuplicateScheme(Guid source)
    {
        IntPtr dest = IntPtr.Zero;
        Check(nameof(PowrProf.PowerDuplicateScheme), PowrProf.PowerDuplicateScheme(IntPtr.Zero, in source, ref dest));
        return TakeGuid(dest);
    }

    public virtual void DeleteScheme(Guid scheme) =>
        Check(nameof(PowrProf.PowerDeleteScheme), PowrProf.PowerDeleteScheme(IntPtr.Zero, in scheme));

    public virtual uint? TryReadValue(Guid scheme, Guid sub, Guid setting, bool ac)
    {
        uint rc = ac
            ? PowrProf.PowerReadACValueIndex(IntPtr.Zero, in scheme, in sub, in setting, out var v)
            : PowrProf.PowerReadDCValueIndex(IntPtr.Zero, in scheme, in sub, in setting, out v);
        return rc == 0 ? v : null;
    }

    public virtual void WriteValue(Guid scheme, Guid sub, Guid setting, bool ac, uint value)
    {
        uint rc = ac
            ? PowrProf.PowerWriteACValueIndex(IntPtr.Zero, in scheme, in sub, in setting, value)
            : PowrProf.PowerWriteDCValueIndex(IntPtr.Zero, in scheme, in sub, in setting, value);
        Check(ac ? "PowerWriteACValueIndex" : "PowerWriteDCValueIndex", rc);
    }

    /// <summary>Friendly name of a scheme; null if the scheme does not exist (ERROR_FILE_NOT_FOUND) or has no name.</summary>
    public virtual string? TryReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        uint rc = PowrProf.PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (rc != 0 && rc != 234 /* ERROR_MORE_DATA */) return null;
        if (size == 0) return null;
        var buf = new byte[size];
        fixed (byte* p = buf)
        {
            rc = PowrProf.PowerReadFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, p, ref size);
        }
        if (rc != 0) return null;
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)Math.Max(0, size - 2));
    }

    /// <summary>All power schemes with their friendly names (PowerEnumerate ACCESS_SCHEME).</summary>
    public virtual IReadOnlyList<(Guid Id, string? Name)> EnumerateSchemes()
    {
        var list = new List<(Guid, string?)>();
        for (uint index = 0; index < 256; index++)
        {
            Guid g;
            uint size = (uint)sizeof(Guid);
            uint rc = PowrProf.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, PowrProf.AccessScheme, index, (byte*)&g, ref size);
            if (rc != 0) break; // ERROR_NO_MORE_ITEMS (259) ends the list
            list.Add((g, TryReadFriendlyName(g)));
        }
        return list;
    }

    public virtual bool SchemeExists(Guid scheme) => TryReadFriendlyName(scheme) != null;

    public virtual void WriteFriendlyName(Guid scheme, string name)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            Check(nameof(PowrProf.PowerWriteFriendlyName),
                PowrProf.PowerWriteFriendlyName(IntPtr.Zero, in scheme, IntPtr.Zero, IntPtr.Zero, p, (uint)bytes.Length));
        }
    }

    public static Guid Balanced => PowerGuids.Balanced;
}
