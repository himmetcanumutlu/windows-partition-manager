// Disk operations on several virtual disks at once interfere with each other (volume arrival,
// VSS, drive letters, Windows' own post-format work), so the integration tests run one at a time,
// the way a user would run them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
