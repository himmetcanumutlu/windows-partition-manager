using System.Security.Cryptography;

namespace WindowsPartitionManager.Integration.Tests.Support;

/// <summary>Writes realistic data to a volume and proves later that every byte is still there.</summary>
internal static class TestData
{
    private const long MiB = 1024 * 1024;

    /// <summary>
    /// Fills most of the volume with filler, writes <paramref name="totalBytes"/> of random files
    /// (from a few KB to several MB, in nested folders), then deletes the filler. The real files
    /// end up near the END of the volume, so a shrink must physically move them.
    /// </summary>
    public static Dictionary<string, string> WriteFilesAtEndOfVolume(char letter, long totalBytes, int seed)
    {
        var root = $@"{letter}:\";
        var drive = new DriveInfo(root);
        var fillerDir = Directory.CreateDirectory(Path.Combine(root, "filler"));
        var reserve = totalBytes + (192 * MiB);

        var zeros = new byte[8 * MiB];
        var fillerIndex = 0;
        while (drive.AvailableFreeSpace > reserve + (16 * MiB))
        {
            var size = Math.Min(512 * MiB, drive.AvailableFreeSpace - reserve);
            using var stream = new FileStream(Path.Combine(fillerDir.FullName, $"filler-{fillerIndex++}.bin"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            for (long written = 0; written < size; written += zeros.Length)
            {
                stream.Write(zeros, 0, (int)Math.Min(zeros.Length, size - written));
            }
        }

        var files = WriteRandomFiles(root, "data", totalBytes, seed);
        Directory.Delete(fillerDir.FullName, recursive: true);
        return files;
    }

    /// <summary>Random files of mixed sizes in nested folders; returns relative path -> SHA-256.</summary>
    public static Dictionary<string, string> WriteRandomFiles(string root, string folder, long totalBytes, int seed)
    {
        var random = new Random(seed);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long written = 0;
        var index = 0;

        while (written < totalBytes)
        {
            // Mostly small files, some medium, a few large: like a real user profile.
            var roll = random.Next(100);
            long size = roll < 70 ? random.Next(1, 64) * 1024
                      : roll < 95 ? random.Next(64, 4096) * 1024L
                      : random.Next(4, 24) * MiB;
            size = Math.Min(size, totalBytes - written);
            if (size <= 0)
            {
                break;
            }

            var relative = Path.Combine(folder, $"d{index % 17:00}", $"s{index % 5}", $"file-{index:00000}.bin");
            var full = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            var data = new byte[size];
            random.NextBytes(data);
            File.WriteAllBytes(full, data);
            hashes[relative] = Convert.ToHexString(SHA256.HashData(data));

            written += size;
            index++;
        }

        return hashes;
    }

    /// <summary>Re-reads every file; returns the paths that are missing or whose content changed.</summary>
    public static List<string> Verify(char letter, IReadOnlyDictionary<string, string> expected)
    {
        var root = $@"{letter}:\";
        var bad = new List<string>();
        foreach (var (relative, hash) in expected)
        {
            var full = Path.Combine(root, relative);
            try
            {
                using var stream = File.OpenRead(full);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.Ordinal))
                {
                    bad.Add(relative + " (content differs)");
                }
            }
            catch (IOException ex)
            {
                bad.Add($"{relative} ({ex.GetType().Name}: {ex.Message})");
            }
            catch (UnauthorizedAccessException ex)
            {
                bad.Add($"{relative} ({ex.Message})");
            }
        }

        return bad;
    }

    public static byte[] Marker(int seed, int length = 1024 * 1024)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }
}
