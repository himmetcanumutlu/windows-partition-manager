using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.Core.Tests;

public class UnblockTests
{
    private const uint ClusterSize = 4096;

    private static ShrinkReport ReportWith(params ShrinkBlocker[] blockers) => new()
    {
        DriveLetter = 'C',
        Geometry = new VolumeGeometry
        {
            BytesPerSector = 512, BytesPerCluster = ClusterSize, TotalClusters = 1000, FreeClusters = 500,
            BytesPerFileRecordSegment = 1024, MftStartLcn = 4, MftValidDataLength = 4096, MftMirrorStartLcn = 2, MftZoneStartLcn = 4, MftZoneEndLcn = 100,
        },
        UsedBytes = 100 * ClusterSize,
        TargetSizeBytes = 200 * ClusterSize,
        TargetWasExplicit = true,
        MinimumSizeWithoutMovingBytes = 900 * ClusterSize,
        MinimumSizeWithBlockersBytes = 800 * ClusterSize,
        MinimumSizeMetadataOnlyBytes = 110 * ClusterSize,
        BlockersPastTarget = blockers,
        BlockersBelowTarget = 0,
        MovableFilesBeyondTarget = 0,
        MovableBytesBeyondTarget = 0,
        UnattributedBytesBeyondTarget = 0,
        Advice = [],
    };

    [Fact]
    public void Plan_MapsBlockerKindsToSteps_AndSkipsMetadata()
    {
        var report = ReportWith(
            new ShrinkBlocker("pagefile.sys", BlockerKind.PageFile, "r", 800, 100, 100),
            new ShrinkBlocker("hiberfil.sys", BlockerKind.HibernationFile, "r", 700, 50, 50),
            new ShrinkBlocker("System Volume Information\\{a}", BlockerKind.ShadowCopyStorage, "r", 600, 10, 10),
            new ShrinkBlocker("System Volume Information\\{b}", BlockerKind.ShadowCopyStorage, "r", 500, 10, 10),
            new ShrinkBlocker("$LogFile", BlockerKind.NtfsMetadata, "r", 400, 5, 5));

        var steps = UnblockPlanner.Plan(report);

        Assert.Equal(3, steps.Count);
        var pageFile = Assert.Single(steps, s => s.Action == UnblockAction.DisablePageFile);
        Assert.True(pageFile.RequiresReboot);
        Assert.Equal(100UL * ClusterSize, pageFile.BytesPastTarget);
        var shadows = Assert.Single(steps, s => s.Action == UnblockAction.DeleteShadowCopies);
        Assert.Equal(2, shadows.Files.Count);
        Assert.False(shadows.Reversible);
        Assert.Contains("reboot", UnblockPlanner.Summarize(steps), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_WithNoRemovableBlockers_IsEmpty()
    {
        var steps = UnblockPlanner.Plan(ReportWith(new ShrinkBlocker("$MFT", BlockerKind.NtfsMetadata, "r", 400, 5, 5)));

        Assert.Empty(steps);
        Assert.Contains("Nothing", UnblockPlanner.Summarize(steps), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runner_RecordsWhatItChanged_AndRestoreUndoesIt()
    {
        var fake = new FakeUnblockOperations { Hibernation = true, Automatic = true, PageFiles = [new PageFileSetting('C', 0, 0)], Shadows = 3 };
        var runner = new UnblockRunner(fake);

        var state = await runner.ApplyAsync('C', [UnblockAction.DisableHibernation, UnblockAction.DisablePageFile, UnblockAction.DeleteShadowCopies]);

        Assert.False(fake.Hibernation);
        Assert.False(fake.Automatic);
        Assert.Equal([new PageFileSetting('D', 0, 0)], fake.PageFiles);
        Assert.Equal('D', state.TemporaryPageFileVolume);
        Assert.Equal(0, fake.Shadows);
        Assert.True(state.HibernationWasEnabled);
        Assert.True(state.PageFileWasAutomatic);
        Assert.Equal(3, state.ShadowCopiesDeleted);
        Assert.NotNull(fake.Saved);

        await runner.RestoreAsync();

        Assert.True(fake.Hibernation);
        Assert.True(fake.Automatic);
        Assert.Empty(fake.PageFiles); // the temporary page file on D: is gone again
        Assert.Null(fake.Saved);
    }

    [Fact]
    public async Task Runner_FailureHalfway_StillRecordsWhatWasAlreadyChanged()
    {
        var fake = new FakeUnblockOperations { Hibernation = true, Automatic = true, Shadows = 2, FailShadowDeletion = true };
        var runner = new UnblockRunner(fake);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.ApplyAsync('C', [UnblockAction.DisableHibernation, UnblockAction.DisablePageFile, UnblockAction.DeleteShadowCopies]));

        // Hibernation and the page file were changed before the failure; both must be restorable.
        Assert.NotNull(fake.Saved);
        Assert.True(fake.Saved!.HibernationWasEnabled);
        Assert.True(fake.Saved.PageFileChanged);

        await runner.RestoreAsync();

        Assert.True(fake.Hibernation);
        Assert.True(fake.Automatic);
        Assert.Null(fake.Saved);
    }

    [Fact]
    public async Task Runner_WithoutAnotherDrive_ReportsThatNoPageFileRemains()
    {
        var fake = new FakeUnblockOperations { Automatic = true, Alternate = null };
        var messages = new List<string>();

        var state = await new UnblockRunner(fake).ApplyAsync('C', [UnblockAction.DisablePageFile], new SyncProgress(messages.Add));

        Assert.Null(state.TemporaryPageFileVolume);
        Assert.Contains(messages, m => m.Contains("without a page file", StringComparison.Ordinal));
    }

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [Fact]
    public async Task Runner_WithNothingReversible_LeavesNoState()
    {
        var fake = new FakeUnblockOperations { Hibernation = false, Shadows = 1 };

        await new UnblockRunner(fake).ApplyAsync('C', [UnblockAction.DisableHibernation, UnblockAction.DeleteShadowCopies]);

        Assert.Null(fake.Saved);
    }

    private sealed class FakeUnblockOperations : IUnblockOperations
    {
        public bool Hibernation { get; set; }

        public bool Automatic { get; set; }

        public List<PageFileSetting> PageFiles { get; set; } = [];

        public int Shadows { get; set; }

        public UnblockState? Saved { get; private set; }

        public bool IsHibernationEnabled() => Hibernation;

        public Task SetHibernationAsync(bool enabled, CancellationToken cancellationToken = default)
        {
            Hibernation = enabled;
            return Task.CompletedTask;
        }

        public bool IsPageFileAutomatic() => Automatic;

        public IReadOnlyList<PageFileSetting> GetPageFiles() => PageFiles;

        /// <summary>Volume that RemovePageFileAsync moves the page file to, or null for "no other drive".</summary>
        public char? Alternate { get; set; } = 'D';

        public bool FailShadowDeletion { get; set; }

        public Task<PageFileChange> RemovePageFileAsync(char volume, CancellationToken cancellationToken = default)
        {
            Automatic = false;
            var removed = PageFiles.FirstOrDefault(p => p.Volume == volume);
            PageFiles.RemoveAll(p => p.Volume == volume);
            char? temporary = null;
            if (PageFiles.Count == 0 && Alternate is { } alternate)
            {
                PageFiles.Add(new PageFileSetting(alternate, 0, 0));
                temporary = alternate;
            }

            return Task.FromResult(new PageFileChange(removed, temporary));
        }

        public Task RestorePageFileAsync(PageFileSetting? removed, bool automatic, char? temporaryVolume, CancellationToken cancellationToken = default)
        {
            if (temporaryVolume is { } temp)
            {
                PageFiles.RemoveAll(p => p.Volume == temp);
            }

            if (automatic)
            {
                Automatic = true;
            }
            else if (removed is not null)
            {
                PageFiles.Add(removed);
            }

            return Task.CompletedTask;
        }

        public int CountShadowCopies(char volume) => Shadows;

        public Task<int> DeleteShadowCopiesAsync(char volume, CancellationToken cancellationToken = default)
        {
            if (FailShadowDeletion)
            {
                throw new InvalidOperationException("VSS is busy");
            }

            var n = Shadows;
            Shadows = 0;
            return Task.FromResult(n);
        }

        public UnblockState? LoadState() => Saved;

        public void SaveState(UnblockState? state) => Saved = state;
    }
}
