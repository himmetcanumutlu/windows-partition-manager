using WindowsPartitionManager.Core.Analysis;
using WindowsPartitionManager.Core.Formatting;

namespace WindowsPartitionManager.Core.Operations;

public enum UnblockAction
{
    /// <summary>powercfg /h off: deletes hiberfil.sys immediately; also turns Fast Startup off.</summary>
    DisableHibernation,

    /// <summary>Remove the page file from this volume; takes effect after a reboot.</summary>
    DisablePageFile,

    /// <summary>Delete System Restore points / shadow copies on this volume; immediate and not reversible.</summary>
    DeleteShadowCopies,
}

/// <summary>One thing the user can do to let Windows shrink further, with its consequences spelled out.</summary>
public sealed record UnblockStep(
    UnblockAction Action,
    string Title,
    string Description,
    bool RequiresReboot,
    bool Reversible,
    ulong BytesPastTarget,
    IReadOnlyList<string> Files);

/// <summary>Turns the blockers of a <see cref="ShrinkReport"/> into concrete, explained steps.</summary>
public static class UnblockPlanner
{
    public static IReadOnlyList<UnblockStep> Plan(ShrinkReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var steps = new List<UnblockStep>();
        var letter = report.DriveLetter;

        AddIfAny(BlockerKind.HibernationFile, UnblockAction.DisableHibernation,
            "Turn hibernation off",
            "Runs 'powercfg /h off', which deletes hiberfil.sys right away. Fast Startup is disabled with it, so the next boot is a normal cold boot. Turn it back on afterwards with 'powercfg /h on' (Windows Partition Manager can do this for you).",
            requiresReboot: false, reversible: true);

        AddIfAny(BlockerKind.PageFile, UnblockAction.DisablePageFile,
            $"Move the page file off {letter}:",
            $"Removes the page file from {letter}: (takes effect after a reboot). If another internal NTFS drive has at least 8 GB free, a system-managed page file is set up there in the meantime; otherwise the computer runs without a page file until you restore, and very heavy memory use could make programs close. Restart, run the shrink, then restore the previous setting; Windows Partition Manager remembers it.",
            requiresReboot: true, reversible: true);

        AddIfAny(BlockerKind.ShadowCopyStorage, UnblockAction.DeleteShadowCopies,
            $"Delete restore points on {letter}:",
            $"Deletes the shadow copies that System Restore and 'Previous Versions' keep on {letter}:. Takes effect immediately. Existing restore points are lost; Windows creates new ones over time.",
            requiresReboot: false, reversible: false);

        return steps;

        void AddIfAny(BlockerKind kind, UnblockAction action, string title, string description, bool requiresReboot, bool reversible)
        {
            var blockers = report.BlockersPastTarget.Where(b => b.Kind == kind).ToList();
            if (blockers.Count == 0)
            {
                return;
            }

            var bytes = blockers.Aggregate(0UL, (sum, b) => sum + report.Geometry.ClustersToBytes(b.ClustersBeyondTarget));
            steps.Add(new UnblockStep(action, title, description, requiresReboot, reversible, bytes, blockers.Select(b => b.Path).ToList()));
        }
    }

    public static string Summarize(IReadOnlyList<UnblockStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (steps.Count == 0)
        {
            return "Nothing removable stands in the way.";
        }

        var reboot = steps.Any(s => s.RequiresReboot) ? " One step needs a reboot before the shrink can use the space." : string.Empty;
        return $"{steps.Count} step(s) would free {ByteSize.Format(steps.Aggregate(0UL, (s, x) => s + x.BytesPastTarget))} of unmovable data past the target.{reboot}";
    }
}
