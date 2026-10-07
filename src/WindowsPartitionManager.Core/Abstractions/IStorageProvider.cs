using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>
/// Read-only access to the machine's disks. The platform layer implements this on top of the
/// Windows Storage Management API; tests can supply an in-memory implementation.
/// </summary>
public interface IStorageProvider
{
    Task<IReadOnlyList<Disk>> GetDisksAsync(CancellationToken cancellationToken = default);
}
