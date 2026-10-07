namespace WindowsPartitionManager.Core.Abstractions;

/// <summary>Page file configuration for one volume, as Windows stores it.</summary>
/// <param name="InitialMb">0 together with <paramref name="MaximumMb"/> 0 means "system managed".</param>
public sealed record PageFileSetting(char Volume, uint InitialMb, uint MaximumMb);

/// <summary>What removing the page file from a volume actually did.</summary>
/// <param name="Removed">The setting that was removed from the volume, if there was one.</param>
/// <param name="TemporaryVolume">Volume where a system-managed page file was set up instead, or null if none was suitable.</param>
public sealed record PageFileChange(PageFileSetting? Removed, char? TemporaryVolume);

/// <summary>What the unblock steps changed, so they can be undone later.</summary>
public sealed record UnblockState
{
    public DateTimeOffset AppliedAt { get; init; }

    public char Volume { get; init; }

    /// <summary>True when hibernation was on before we turned it off.</summary>
    public bool HibernationWasEnabled { get; init; }

    /// <summary>True when Windows managed page files automatically before we took over.</summary>
    public bool PageFileWasAutomatic { get; init; }

    /// <summary>The page file setting we removed from the volume, if any.</summary>
    public PageFileSetting? RemovedPageFile { get; init; }

    /// <summary>Volume that received a temporary page file while the original one was removed.</summary>
    public char? TemporaryPageFileVolume { get; init; }

    /// <summary>True once the page file step ran; restore must then put the page file configuration back.</summary>
    public bool PageFileChanged { get; init; }

    public int ShadowCopiesDeleted { get; init; }

    public bool HasSomethingToRestore => HibernationWasEnabled || PageFileChanged;
}

/// <summary>System settings that stand between a volume and its shrink target.</summary>
public interface IUnblockOperations
{
    bool IsHibernationEnabled();

    Task SetHibernationAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>True when Windows picks page file locations itself (the default).</summary>
    bool IsPageFileAutomatic();

    IReadOnlyList<PageFileSetting> GetPageFiles();

    /// <summary>
    /// Removes the page file from the volume. When no other page file would remain, a
    /// system-managed one is set up on another internal NTFS volume so the computer is not left
    /// without one. Takes effect after a reboot.
    /// </summary>
    Task<PageFileChange> RemovePageFileAsync(char volume, CancellationToken cancellationToken = default);

    /// <summary>Puts back what <see cref="RemovePageFileAsync"/> changed and removes the temporary page file.</summary>
    Task RestorePageFileAsync(PageFileSetting? removed, bool automatic, char? temporaryVolume, CancellationToken cancellationToken = default);

    int CountShadowCopies(char volume);

    Task<int> DeleteShadowCopiesAsync(char volume, CancellationToken cancellationToken = default);

    UnblockState? LoadState();

    void SaveState(UnblockState? state);
}
