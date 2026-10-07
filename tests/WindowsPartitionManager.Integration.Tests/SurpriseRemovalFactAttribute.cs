namespace WindowsPartitionManager.Integration.Tests;

/// <summary>
/// For tests that pull a virtual disk out from under mounted volumes on purpose. That path
/// exercises kernel code that has crashed this development machine (a bugcheck right after a
/// surprise removal followed by a new attach), so these tests only run when explicitly asked for:
/// set WPM_RUN_SURPRISE_REMOVAL=1, save your work first, and preferably run them in a VM.
/// </summary>
public sealed class SurpriseRemovalFactAttribute : FactAttribute
{
    public SurpriseRemovalFactAttribute()
    {
        var admin = new AdminFactAttribute();
        if (admin.Skip is not null)
        {
            Skip = admin.Skip;
            return;
        }

        if (Environment.GetEnvironmentVariable("WPM_RUN_SURPRISE_REMOVAL") != "1")
        {
            Skip = "Surprise-removal test: opt in with WPM_RUN_SURPRISE_REMOVAL=1 (it can destabilise the host; prefer a VM).";
        }
    }
}
