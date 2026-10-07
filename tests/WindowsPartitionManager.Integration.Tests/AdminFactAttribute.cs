using System.Security.Principal;

namespace WindowsPartitionManager.Integration.Tests;

/// <summary>A test that touches real (virtual) disks and therefore only runs in an elevated process.</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only.";
            return;
        }

        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Requires administrator rights (run the test host elevated).";
        }
    }
}
