using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Stops Windows from sleeping while a partition operation runs. Scoped to the calling thread,
/// so it must be created and disposed on the thread that performs the work.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class KeepAwake : IDisposable
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    private KeepAwake()
    {
    }

    /// <summary>Previous state as returned by Windows, 0 when the call failed (then we simply have no inhibitor).</summary>
    public uint PreviousState { get; private set; }

    public static KeepAwake Begin() => new() { PreviousState = SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED) };

    public void Dispose() => _ = SetThreadExecutionState(ES_CONTINUOUS);

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);
}
