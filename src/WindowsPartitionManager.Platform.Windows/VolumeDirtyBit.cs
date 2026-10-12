using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindowsPartitionManager.Platform.Windows.Interop;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Asks the file system directly whether a volume is marked dirty (FSCTL_IS_VOLUME_DIRTY).
/// The Storage Management API caches this flag and can lag behind; this does not.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class VolumeDirtyBit
{
    /// <summary>True/false when the query succeeded; null when the volume could not be opened (e.g. not elevated).</summary>
    public static bool? Query(char driveLetter) => Query(driveLetter, out _);

    /// <summary>Same as <see cref="Query(char)"/> but also reports the Win32 error when the answer is null.</summary>
    public static bool? Query(char driveLetter, out int win32Error)
    {
        win32Error = 0;

        // Volume handles need GENERIC_READ for this control code, which Windows only grants to elevated callers.
        using var volume = Kernel32.CreateFile(
            $@"\\.\{char.ToUpperInvariant(driveLetter)}:",
            Kernel32.GENERIC_READ,
            Kernel32.FILE_SHARE_READ | Kernel32.FILE_SHARE_WRITE,
            nint.Zero,
            Kernel32.OPEN_EXISTING,
            0,
            nint.Zero);

        if (volume.IsInvalid)
        {
            win32Error = Marshal.GetLastPInvokeError();
            return null;
        }

        uint flags;
        if (!Kernel32.DeviceIoControl(volume, Kernel32.FSCTL_IS_VOLUME_DIRTY, null, 0, &flags, sizeof(uint), out _, nint.Zero))
        {
            win32Error = Marshal.GetLastPInvokeError();
            return null;
        }

        return (flags & Kernel32.VOLUME_IS_DIRTY) != 0;
    }
}
