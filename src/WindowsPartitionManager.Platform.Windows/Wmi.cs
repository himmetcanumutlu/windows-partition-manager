using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using WindowsPartitionManager.Core.Abstractions;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>Small helpers over System.Management for the Storage Management namespace.</summary>
[SupportedOSPlatform("windows")]
internal static class Wmi
{
    public const string StorageNamespace = @"\\.\root\Microsoft\Windows\Storage";

    public static ManagementScope Connect()
    {
        var scope = new ManagementScope(StorageNamespace);
        scope.Connect();
        return scope;
    }

    public static IEnumerable<ManagementObject> Query(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using var mo = (ManagementObject)item;
            yield return mo;
        }
    }

    /// <summary>First match or null; the caller owns the returned object.</summary>
    public static ManagementObject? QuerySingle(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            return (ManagementObject)item;
        }

        return null;
    }

    /// <summary>
    /// Invokes a Storage API method and throws a <see cref="StorageOperationException"/> with the
    /// ExtendedStatus text when ReturnValue is non-zero.
    /// </summary>
    public static ManagementBaseObject Invoke(ManagementObject target, string method, Action<ManagementBaseObject>? setParameters = null)
    {
        ManagementBaseObject? inParams = null;
        try
        {
            if (setParameters is not null)
            {
                inParams = target.GetMethodParameters(method);
                setParameters(inParams);
            }

            var outParams = target.InvokeMethod(method, inParams, null)
                ?? throw new StorageOperationException(method, uint.MaxValue, "no result returned");

            var returnValue = Get<uint>(outParams, "ReturnValue");
            if (returnValue != 0)
            {
                var message = DescribeExtendedStatus(outParams) ?? DescribeReturnCode(returnValue);
                outParams.Dispose();
                throw new StorageOperationException(method, returnValue, message);
            }

            return outParams;
        }
        finally
        {
            inParams?.Dispose();
        }
    }

    /// <summary>Storage Management API return codes (learn.microsoft.com, "Storage Management API Common Return Codes").</summary>
    public static string DescribeReturnCode(uint code) => code switch
    {
        1 => "Not supported.",
        2 => "Unspecified error.",
        3 => "Timeout.",
        4 => "Failed (Windows gave no detail). The volume may still be busy, e.g. right after a format; wait a few seconds and try again. If it keeps failing, run chkdsk on it.",
        5 => "Invalid parameter.",
        6 => "The disk is in use.",
        8 => "Object not found.",
        4097 => "Size not supported.",
        40000 => "Not enough free space (the size exceeds the largest free extent Windows sees).",
        40001 => "Access denied (administrator rights are required).",
        40002 => "Not enough resources to complete the operation.",
        40003 => "Cache out of date; refresh and try again.",
        40004 => "An unexpected I/O error occurred.",
        40005 => "Specify either Size or UseMaximumSize, not both.",
        41000 => "The disk has not been initialized.",
        41001 => "The disk is already initialized.",
        41002 => "The disk is read-only.",
        41003 => "The disk is offline.",
        41004 => "The disk's partition limit has been reached.",
        41005 => "The partition alignment is not valid.",
        41006 => "A parameter is not valid for this type of partition.",
        41010 => "The partition type is not valid.",
        41011 => "Only the first 2 TB are usable on MBR disks.",
        41012 or 41016 => "The offset is not valid.",
        41014 => "The disk is too small for GPT.",
        41017 => "The partition layout is invalid.",
        42000 => "The partition was deleted but its access paths were not.",
        42002 => "The requested drive letter or path is already in use.",
        42004 => "Hidden partitions cannot receive access paths.",
        42007 => "The access path is not valid.",
        42008 => "Cannot shrink a partition whose volume has errors.",
        42009 => "Cannot resize a partition with an unknown file system.",
        42010 => "Not allowed on a system or critical partition.",
        42011 => "Only supported on data partitions.",
        42013 => "This type of partition cannot have a drive letter.",
        43000 => "The cluster size is invalid.",
        43001 => "The file system is not supported.",
        43002 => "The volume cannot be quick-formatted.",
        43003 => "The number of clusters exceeds 32 bits (volume too large for this file system).",
        43006 => "The drive is read-only.",
        _ => "see Storage Management API return codes",
    };

    private static readonly string[] StatusTextProperties = ["Message", "MessageDescription", "CIMStatusCodeDescription"];

    private static string? DescribeExtendedStatus(ManagementBaseObject outParams)
    {
        if (GetRaw(outParams, "ExtendedStatus") is not ManagementBaseObject status)
        {
            return null;
        }

        using (status)
        {
            var parts = StatusTextProperties
                .Select(p => Get<string>(status, p))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    /// <summary>MSFT_* classes expose DriveLetter as a CIM char16; it is '\0' when no letter is assigned.</summary>
    public static char? ReadDriveLetter(ManagementBaseObject mo) => GetRaw(mo, "DriveLetter") switch
    {
        char c when c != '\0' && char.IsLetter(c) => char.ToUpperInvariant(c),
        string { Length: > 0 } s when char.IsLetter(s[0]) => char.ToUpperInvariant(s[0]),
        _ => null,
    };

    public static object? GetRaw(ManagementBaseObject mo, string property)
    {
        try
        {
            var value = mo[property];
            return value is DBNull ? null : value;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    public static T? Get<T>(ManagementBaseObject mo, string property)
    {
        var value = GetRaw(mo, property);
        if (value is null)
        {
            return default;
        }

        if (value is T typed)
        {
            return typed;
        }

        try
        {
            return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
        }
        catch (InvalidCastException)
        {
            return default;
        }
        catch (FormatException)
        {
            return default;
        }
        catch (OverflowException)
        {
            return default;
        }
    }
}
