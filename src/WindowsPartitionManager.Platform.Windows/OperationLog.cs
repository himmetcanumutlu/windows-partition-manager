using System.Globalization;

namespace WindowsPartitionManager.Platform.Windows;

/// <summary>
/// Append-only record of every write operation Windows Partition Manager asks Windows to perform: what, when, and
/// how it ended. Lives in %LOCALAPPDATA%\WindowsPartitionManager\operations.log so there is always a trail.
/// </summary>
public static class OperationLog
{
    private static readonly Lock Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsPartitionManager",
        "operations.log");

    public static void Append(string operation, string details, string outcome)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {operation,-16} {details}  -> {outcome}{Environment.NewLine}");

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                // Write-through and flushed: the record must survive a crash or power loss that
                // happens right after the operation, which is exactly when it is needed.
                using var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            catch (IOException)
            {
                // Logging must never break the operation itself.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
