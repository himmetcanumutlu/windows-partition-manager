using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Not much to configure yet: where the logs are, what can be restored, which version this is.</summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private string _restoreStatus = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly IUnblockOperations _unblock;
    private readonly Func<string, bool> _confirm;

    public SettingsViewModel(string logPath, string statePath, IUnblockOperations unblock, Func<string, bool> confirm)
    {
        LogPath = logPath;
        StatePath = statePath;
        _unblock = unblock;
        _confirm = confirm;

        OpenLogCommand = new RelayCommand(() => Open(LogPath), () => File.Exists(LogPath));
        OpenLogFolderCommand = new RelayCommand(() => Open(Path.GetDirectoryName(LogPath)!), () => Directory.Exists(Path.GetDirectoryName(LogPath)));
        RestoreCommand = new RelayCommand(() => _ = RestoreAsync(), () => HasPendingRestore);
    }

    public string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return (informational ?? assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
    }

    public string LogPath { get; }

    public string StatePath { get; }

    public bool HasPendingRestore => _unblock.LoadState() is { HasSomethingToRestore: true };

    public string RestoreText => _unblock.LoadState() is { } s
        ? $"Settings changed on {s.AppliedAt:yyyy-MM-dd HH:mm} for {s.Volume}: can be restored (hibernation and page file)."
        : "No settings are waiting to be restored.";

    public RelayCommand OpenLogCommand { get; }

    public RelayCommand OpenLogFolderCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public string RestoreStatus
    {
        get => _restoreStatus;
        private set
        {
            _restoreStatus = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreStatus)));
        }
    }

    public event EventHandler? Changed;

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Nothing registered to open it; not worth an error dialog.
        }
    }

    private async Task RestoreAsync()
    {
        if (!_confirm("Restore hibernation and page file settings to what they were before Windows Partition Manager changed them?"))
        {
            return;
        }

        try
        {
            await new UnblockRunner(_unblock).RestoreAsync(new Progress<string>(s => RestoreStatus = s));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RestoreStatus = ex.Message;
        }

        RestoreCommand.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RestoreText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPendingRestore)));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
