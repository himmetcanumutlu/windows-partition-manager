using WindowsPartitionManager.Core.Model;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>Maps GPT type GUIDs and MBR type bytes to a <see cref="PartitionKind"/> and a label.</summary>
public static class PartitionTypes
{
    private static readonly Dictionary<Guid, (PartitionKind Kind, string Name)> GptTypes = new()
    {
        [new Guid("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7")] = (PartitionKind.Basic, "Basic data"),
        [new Guid("C12A7328-F81F-11D2-BA4B-00A0C93EC93B")] = (PartitionKind.EfiSystem, "EFI System"),
        [new Guid("E3C9E316-0B5C-4DB8-817D-F92DF00215AE")] = (PartitionKind.MicrosoftReserved, "Microsoft Reserved"),
        [new Guid("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC")] = (PartitionKind.Recovery, "Recovery"),
        [new Guid("AF9B60A0-1431-4F62-BC68-3311714A69AD")] = (PartitionKind.Ldm, "LDM data"),
        [new Guid("E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D")] = (PartitionKind.StorageSpaces, "Storage Spaces"),
        [new Guid("0FC63DAF-8483-4772-8E79-3D69D8477DE4")] = (PartitionKind.Linux, "Linux filesystem"),
        [new Guid("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F")] = (PartitionKind.LinuxSwap, "Linux swap"),
    };

    public static (PartitionKind Kind, string Name) FromGpt(Guid type)
        => GptTypes.TryGetValue(type, out var known) ? known : (PartitionKind.Other, "GPT " + type.ToString("D").ToUpperInvariant());

    public static (PartitionKind Kind, string Name) FromMbr(int type) => type switch
    {
        0x01 or 0x04 or 0x06 or 0x0E => (PartitionKind.Basic, "FAT"),
        0x07 => (PartitionKind.Basic, "Basic data"),
        0x0B or 0x0C => (PartitionKind.Basic, "FAT32"),
        0x05 or 0x0F => (PartitionKind.Extended, "Extended"),
        0x27 => (PartitionKind.Recovery, "Recovery"),
        0x42 => (PartitionKind.Ldm, "Dynamic (LDM)"),
        0x82 => (PartitionKind.LinuxSwap, "Linux swap"),
        0x83 => (PartitionKind.Linux, "Linux"),
        0xEE => (PartitionKind.Other, "GPT protective"),
        0xEF => (PartitionKind.EfiSystem, "EFI System"),
        0 => (PartitionKind.Unknown, "Unknown"),
        _ => (PartitionKind.Other, $"MBR type 0x{type:X2}"),
    };

    /// <summary>Names for the MSFT_Disk.BusType enumeration of the Storage Management API.</summary>
    public static string BusTypeName(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown",
    };
}
