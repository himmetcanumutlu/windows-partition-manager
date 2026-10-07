using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WindowsPartitionManager.Core.Abstractions;
using WindowsPartitionManager.Core.Formatting;
using WindowsPartitionManager.Core.Model;
using WindowsPartitionManager.Core.Operations;

namespace WindowsPartitionManager.App.ViewModels;

/// <summary>Form for shrinking or extending one partition.</summary>
public sealed class ResizePartitionViewModel : INotifyPropertyChanged
{
    private const ulong MiB = 1024 * 1024;

    private readonly IStorageOperations _operations;
    private readonly ISupportedSizeProvider _limitsProvider;
    private readonly IPowerStatusProvider _power;
    private readonly Disk _disk;
    private readonly Partition _partition;
    private readonly Func<string, bool> _confirm;

    private SupportedSize? _limits;
    private string _newSizeText;
    private bool _isBusy;
    private bool _isDone;
    private bool _limitsLoading = true;
    private string _status = string.Empty;
    private string? _error;

    public ResizePartitionViewModel(IStorageOperations operations, ISupportedSizeProvider limitsProvider, IPowerStatusProvider power, Disk disk, Partition partition, Func<string, bool> confirm)
    {
        _operations = operations;
        _limitsProvider = limitsProvider;
        _power = power;
        _disk = disk;
        _partition = partition;
        _confirm = confirm;

        _newSizeText = ToMegabytes(partition.SizeBytes);

        ResizeCommand = new RelayCommand(() => _ = ResizeAsync(), () => CanResize);
        ShrinkToMinimumCommand = new RelayCommand(() => NewSizeText = ToMegabytesRoundedUp(LowerBound));
        ExtendToMaximumCommand = new RelayCommand(() => NewSizeText = ToMegabytes(UpperBound), () => UpperBound > partition.SizeBytes);
        Revalidate();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler<Partition>? Completed;

    /// <summary>Raised after every attempt, successful or not, so the disk list can be refreshed.</summary>
    public event EventHandler? AttemptFinished;

    public string Title => $"Resize · {(_partition.DriveLetter is { } l ? $"{l}:" : $"partition {_partition.Number}")} on disk {_disk.Number}";

    public string InfoText
    {
        get
        {
            var volume = _partition.Volume;
            var used = volume is null ? "unknown" : ByteSize.Format(volume.UsedBytes);
            var label = volume?.Label is { Length: > 0 } s ? $" \"{s}\"" : string.Empty;
            return $"{_disk.FriendlyName} · {volume?.FileSystem}{label} · current size {ByteSize.Format(_partition.SizeBytes)}, {used} in use";
        }
    }

    public string LimitsText
    {
        get
        {
            if (_limitsLoading)
            {
                return "Asking Windows how far this volume can shrink or grow…";
            }

            var lower = ByteSize.Format(LowerBound);
            var upper = ByteSize.Format(UpperBound);
            var source = _limits is null ? " (Windows did not report its own limits; using data size and free space only)" : string.Empty;
            return UpperBound > _partition.SizeBytes
                ? $"Possible range: {lower} to {upper}{source}"
                : $"Can shrink to {lower}; cannot extend (no free space right after the partition){source}";
        }
    }

    /// <summary>Smallest size we will offer: the data must fit and Windows must agree.</summary>
    public ulong LowerBound => Math.Max(ResizePlanner.MinimumSize(_partition), _limits?.MinimumBytes ?? 0);

    /// <summary>Largest size we will offer: contiguous free space after the partition, capped by Windows.</summary>
    public ulong UpperBound
    {
        get
        {
            var max = ResizePlanner.MaximumSize(_disk, _partition);
            return _limits is { } l ? Math.Min(max, Math.Max(l.MaximumBytes, _partition.SizeBytes)) : max;
        }
    }

    public ObservableCollection<ValidationIssue> Issues { get; } = [];

    public RelayCommand ResizeCommand { get; }

    public RelayCommand ShrinkToMinimumCommand { get; }

    public RelayCommand ExtendToMaximumCommand { get; }

    public string NewSizeText
    {
        get => _newSizeText;
        set
        {
            if (SetField(ref _newSizeText, value))
            {
                Revalidate();
            }
        }
    }

    public string ChangeText { get; private set; } = string.Empty;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanResize));
                OnPropertyChanged(nameof(IsFormEnabled));
                ResizeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDone
    {
        get => _isDone;
        private set
        {
            if (SetField(ref _isDone, value))
            {
                OnPropertyChanged(nameof(CanResize));
                OnPropertyChanged(nameof(IsFormEnabled));
                ResizeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsFormEnabled => !IsBusy && !IsDone;

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error is not null;

    public bool HasBlockingIssue => Issues.Any(i => i.IsError);

    public bool CanResize => !IsBusy && !IsDone && !_limitsLoading && !HasBlockingIssue;

    /// <summary>Fetches Windows' own min/max; slow (seconds), so the form opens first and fills in later.</summary>
    public async Task LoadLimitsAsync()
    {
        try
        {
            _limits = await _limitsProvider.GetSupportedSizeAsync(_disk.Number, _partition.Number);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _limits = null;
        }
        finally
        {
            _limitsLoading = false;
            OnPropertyChanged(nameof(LimitsText));
            OnPropertyChanged(nameof(LowerBound));
            OnPropertyChanged(nameof(UpperBound));
            ExtendToMaximumCommand.RaiseCanExecuteChanged();
            Revalidate();
        }
    }

    private static string ToMegabytes(ulong bytes) => (bytes / MiB).ToString(CultureInfo.InvariantCulture) + " MB";

    /// <summary>For lower bounds: rounding down would land just below the limit and be refused.</summary>
    private static string ToMegabytesRoundedUp(ulong bytes) => ((bytes + MiB - 1) / MiB).ToString(CultureInfo.InvariantCulture) + " MB";

    private ResizePartitionRequest? BuildRequest(out string? parseError)
    {
        parseError = null;
        if (!ByteSize.TryParse(NewSizeText, out var size) || size == 0)
        {
            parseError = $"Could not understand the size '{NewSizeText}'. Try 300GB, 1.5TB or 20480MB.";
            return null;
        }

        return ResizePlanner.Normalize(new ResizePartitionRequest
        {
            DiskNumber = _disk.Number,
            PartitionNumber = _partition.Number,
            NewSizeBytes = size,
            ExpectedDisk = DiskFingerprint.Of(_disk),
            ExpectedPartition = PartitionFingerprint.Of(_partition),
        });
    }

    private void Revalidate()
    {
        Issues.Clear();
        var request = BuildRequest(out var parseError);
        if (request is null)
        {
            Issues.Add(new ValidationIssue(IssueSeverity.Error, parseError!));
            ChangeText = string.Empty;
        }
        else
        {
            foreach (var issue in ResizePlanner.Validate(_disk, _partition, request, _limits))
            {
                Issues.Add(issue);
            }

            foreach (var issue in SafetyChecks.Power(_power.GetPowerStatus(), touchesSystemVolume: _partition.IsBoot || _partition.IsSystem))
            {
                Issues.Add(issue);
            }

            ChangeText = request.NewSizeBytes < _partition.SizeBytes
                ? $"Shrink from {ByteSize.Format(_partition.SizeBytes)} to {ByteSize.Format(request.NewSizeBytes)}; frees {ByteSize.Format(_partition.SizeBytes - request.NewSizeBytes)} right after the partition for a new drive."
                : request.NewSizeBytes > _partition.SizeBytes
                    ? $"Extend from {ByteSize.Format(_partition.SizeBytes)} to {ByteSize.Format(request.NewSizeBytes)} (+{ByteSize.Format(request.NewSizeBytes - _partition.SizeBytes)})."
                    : "No change.";
        }

        OnPropertyChanged(nameof(ChangeText));
        OnPropertyChanged(nameof(HasBlockingIssue));
        OnPropertyChanged(nameof(CanResize));
        ResizeCommand.RaiseCanExecuteChanged();
    }

    private async Task ResizeAsync()
    {
        var request = BuildRequest(out _);
        if (request is null || HasBlockingIssue || IsBusy)
        {
            return;
        }

        if (!_confirm($"{ChangeText}\n\nWindows moves files as needed and keeps your data, but any resize carries risk: make sure important files are backed up and do not power off during the operation. Continue?"))
        {
            return;
        }

        IsBusy = true;
        Error = null;
        try
        {
            var result = await _operations.ResizePartitionAsync(request, new Progress<string>(s => Status = s));
            Status = $"Done: the partition is now {ByteSize.Format(result.SizeBytes)}." +
                     (request.NewSizeBytes < _partition.SizeBytes ? " Use \"New partition\" on the freed space to create a drive." : string.Empty);
            IsDone = true;
            Completed?.Invoke(this, result);
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = ex.Message;
            Status = "Failed. Nothing is lost: Windows applies a resize only when it can complete it. The disk list has been refreshed.";
            AttemptFinished?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
