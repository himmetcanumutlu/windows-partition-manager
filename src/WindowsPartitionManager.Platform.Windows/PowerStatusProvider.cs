using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>GetSystemPowerStatus: AC line state and battery charge.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class PowerStatusProvider : IPowerStatusProvider
{
    public PowerStatus? GetPowerStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return null;
        }

        bool? onAc = status.ACLineStatus switch
        {
            0 => false,
            1 => true,
            _ => null,
        };

        // 128 = no system battery (desktop): treat as mains power.
        if ((status.BatteryFlag & 0x80) != 0)
        {
            onAc = true;
        }

        int? percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
        return new PowerStatus(onAc, percent);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
