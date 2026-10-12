using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsPartitionManager.Platform.Windows.Interop;

/// <summary>
/// Enables token privileges for the current process. Administrators hold privileges such as
/// SeManageVolumePrivilege, but Windows keeps them disabled until a program asks; some APIs
/// (permanent VHD attach, for one) fail with ERROR_PRIVILEGE_NOT_HELD otherwise.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class Privileges
{
    public const string SeManageVolumePrivilege = "SeManageVolumePrivilege";

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    /// <summary>Returns false when the privilege is not in the token at all (not elevated).</summary>
    public static bool TryEnable(string privilegeName)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return false;
            }

            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, in privileges, 0, nint.Zero, nint.Zero))
            {
                return false;
            }

            return Marshal.GetLastPInvokeError() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        in TOKEN_PRIVILEGES newState,
        uint bufferLength,
        nint previousState,
        nint returnLength);
}
