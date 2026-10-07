using WindowsPartitionManager.Core.Abstractions;

namespace WindowsPartitionManager.Core.Operations;

/// <summary>
/// Applies chosen unblock steps and records what changed so <see cref="RestoreAsync"/> can undo it.
/// The record is written after every step, so a failure half-way still leaves an accurate record
/// of what was already changed.
/// </summary>
public sealed class UnblockRunner(IUnblockOperations operations)
{
    public async Task<UnblockState> ApplyAsync(char volume, IEnumerable<UnblockAction> actions, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var state = operations.LoadState() ?? new UnblockState { Volume = volume };
        state = state with { AppliedAt = DateTimeOffset.Now, Volume = volume };

        foreach (var action in actions.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (action)
            {
                case UnblockAction.DisableHibernation:
                    if (operations.IsHibernationEnabled())
                    {
                        progress?.Report("Turning hibernation off…");
                        await operations.SetHibernationAsync(false, cancellationToken).ConfigureAwait(false);
                        state = Record(state with { HibernationWasEnabled = true });
                    }
                    else
                    {
                        progress?.Report("Hibernation is already off.");
                    }

                    break;

                case UnblockAction.DisablePageFile:
                    progress?.Report($"Removing the page file from {volume}:…");
                    var wasAutomatic = operations.IsPageFileAutomatic();

                    // Record the intent first: if the change below half-succeeds, restore still knows what to undo.
                    state = Record(state with { PageFileChanged = true, PageFileWasAutomatic = state.PageFileWasAutomatic || wasAutomatic });
                    var change = await operations.RemovePageFileAsync(volume, cancellationToken).ConfigureAwait(false);
                    state = Record(state with
                    {
                        RemovedPageFile = change.Removed ?? state.RemovedPageFile,
                        TemporaryPageFileVolume = change.TemporaryVolume ?? state.TemporaryPageFileVolume,
                    });
                    progress?.Report(change.TemporaryVolume is { } temp
                        ? $"A system-managed page file will be used on {temp}: until you restore."
                        : "No other suitable drive was found: the computer will run without a page file until you restore.");
                    break;

                case UnblockAction.DeleteShadowCopies:
                    progress?.Report($"Deleting shadow copies on {volume}:…");
                    var deleted = await operations.DeleteShadowCopiesAsync(volume, cancellationToken).ConfigureAwait(false);
                    state = Record(state with { ShadowCopiesDeleted = state.ShadowCopiesDeleted + deleted });
                    break;
            }
        }

        return state;
    }

    public async Task RestoreAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var state = operations.LoadState();
        if (state is null)
        {
            progress?.Report("Nothing to restore.");
            return;
        }

        if (state.HibernationWasEnabled)
        {
            progress?.Report("Turning hibernation back on…");
            await operations.SetHibernationAsync(true, cancellationToken).ConfigureAwait(false);
            state = Record(state with { HibernationWasEnabled = false });
        }

        if (state.PageFileChanged)
        {
            progress?.Report("Restoring the page file setting…");
            await operations.RestorePageFileAsync(state.RemovedPageFile, state.PageFileWasAutomatic, state.TemporaryPageFileVolume, cancellationToken).ConfigureAwait(false);
            state = Record(state with { PageFileChanged = false, RemovedPageFile = null, TemporaryPageFileVolume = null, PageFileWasAutomatic = false });
        }

        operations.SaveState(null);
        progress?.Report("Previous settings restored. A reboot applies the page file change.");
    }

    /// <summary>Persists the state now (or clears it when nothing is left to restore) and returns it.</summary>
    private UnblockState Record(UnblockState state)
    {
        operations.SaveState(state.HasSomethingToRestore ? state : null);
        return state;
    }
}
