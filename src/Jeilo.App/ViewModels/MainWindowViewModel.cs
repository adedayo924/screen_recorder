using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jeilo.Core.Audio;
using Jeilo.Core.Capture;
using Jeilo.Core.Encoding;
using Jeilo.Core.Recording;
using Jeilo.App.Services;
using Jeilo.Core.Settings;
using NAudio.CoreAudioApi;

namespace Jeilo.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly RecordingEngine _engine;
    private readonly SemaphoreSlim _metadataGate = new(2, 2);
    private readonly string _thumbnailCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Jeilo Recorder",
        "Thumbnails");
    private CancellationTokenSource? _metadataCancellation;
    private CancellationTokenSource? _countdownCancellation;
    private CancellationTokenSource? _toastCancellation;
    private string? _ffmpegPath;
    private bool _initializing = true;

    public event Action? RequestMinimize;
    public event Action? RequestRestore;
    public event Action<string>? RequestSetClipboardText;
    public event Action<RecordingItem, Action<bool>>? RequestConfirmDelete;

    [ObservableProperty]
    private string _activePage = "record";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _canToggleRecording;

    [ObservableProperty]
    private string _recordButtonText = "Start recording";

    [ObservableProperty]
    private string _elapsedText = "00:00:00";

    [ObservableProperty]
    private string _statusText = "Ready to record";

    [ObservableProperty]
    private string _statusDetail = "Your screen and audio are ready.";

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private double _micLevel;

    [ObservableProperty]
    private double _systemLevel;

    [ObservableProperty]
    private string _micLevelText = "0%";

    [ObservableProperty]
    private string _systemLevelText = "0%";

    [ObservableProperty]
    private bool _isMicWarning;

    [ObservableProperty]
    private bool _isMicClipping;

    [ObservableProperty]
    private bool _isSystemWarning;

    [ObservableProperty]
    private bool _isSystemClipping;

    [ObservableProperty]
    private bool _microphoneEnabled;

    [ObservableProperty]
    private bool _systemAudioEnabled;

    [ObservableProperty]
    private float _microphoneGain = 1.0f;

    [ObservableProperty]
    private float _systemAudioGain = 1.0f;

    [ObservableProperty]
    private AudioDeviceItem? _selectedMicrophone;

    [ObservableProperty]
    private AudioDeviceItem? _selectedSystemAudio;

    [ObservableProperty]
    private MonitorItem? _selectedMonitor;

    [ObservableProperty]
    private CaptureSourceOption? _selectedSource;

    [ObservableProperty]
    private WindowItem? _selectedWindow;

    [ObservableProperty]
    private int _selectedFps = 30;

    [ObservableProperty]
    private QualityOption? _selectedQuality;

    [ObservableProperty]
    private EncoderOption? _selectedEncoder;

    [ObservableProperty]
    private bool _includeCursor = true;

    [ObservableProperty]
    private string _outputDirectory = string.Empty;

    [ObservableProperty]
    private string _saveStatusText = "Settings are saved automatically.";

    [ObservableProperty]
    private string _audioSummary = "Microphone + system audio";

    [ObservableProperty]
    private string _selectedMonitorLabel = "Display 1";

    [ObservableProperty]
    private string _ffmpegStatus = "Checking FFmpeg...";

    [ObservableProperty]
    private string _ffmpegPathDisplay = string.Empty;

    [ObservableProperty]
    private bool _isFfmpegReady;

    [ObservableProperty]
    private bool _isLibraryEmpty = true;

    [ObservableProperty]
    private string _libraryCountText = "0 recordings";

    [ObservableProperty]
    private string _lastSavedFileName = string.Empty;

    [ObservableProperty]
    private bool _hasLastSavedFile;

    [ObservableProperty]
    private string _lastSavedPath = string.Empty;

    [ObservableProperty]
    private string _selectedThemeMode = "Dark";

    [ObservableProperty]
    private int _selectedCountdownSeconds = 3;

    [ObservableProperty]
    private bool _isCountingDown;

    [ObservableProperty]
    private int _countdownNumber = 3;

    [ObservableProperty]
    private bool _minimizeOnRecord;

    [ObservableProperty]
    private string _librarySearchQuery = string.Empty;

    [ObservableProperty]
    private string _librarySortOrder = "Newest first";

    [ObservableProperty]
    private string _toastMessage = string.Empty;

    [ObservableProperty]
    private bool _isToastVisible;

    public MainWindowViewModel()
    {
        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _engine = new RecordingEngine();

        FpsOptions = new[] { 24, 30, 60 };
        ThemeModes = new[] { "Dark", "Light", "System" };
        CountdownOptions = new[] { 0, 3, 5 };
        LibrarySortOptions = new[] { "Newest first", "Oldest first", "Largest size", "Name" };

        Qualities = new ObservableCollection<QualityOption>
        {
            new(VideoQuality.Low, "Low", "Smallest files"),
            new(VideoQuality.Medium, "Medium", "Balanced"),
            new(VideoQuality.High, "High", "Recommended"),
            new(VideoQuality.Ultra, "Ultra", "Sharpest detail")
        };

        LoadFfmpeg();
        LoadMonitors();
        LoadWindows();
        LoadAudioDevices();
        SelectedSource = CaptureSources[0];
        ApplySettings();
        RefreshLibrary();

        _engine.ProgressChanged += progress =>
            Dispatcher.UIThread.Post(() => ApplyProgress(progress));

        _initializing = false;
        UpdateAudioSummary();
        UpdateRecordingAvailability();
        AppVersion = typeof(MainWindowViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    }

    public IReadOnlyList<int> FpsOptions { get; }

    public IReadOnlyList<string> ThemeModes { get; }

    public IReadOnlyList<int> CountdownOptions { get; }

    public IReadOnlyList<string> LibrarySortOptions { get; }

    public ObservableCollection<QualityOption> Qualities { get; }

    public ObservableCollection<EncoderOption> Encoders { get; } = new();

    public ObservableCollection<MonitorItem> Monitors { get; } = new();

    public ObservableCollection<CaptureSourceOption> CaptureSources { get; } = new()
    {
        new(CaptureTargetKind.Monitor, "Screen", "Record an entire display"),
        new(CaptureTargetKind.Window, "Window", "Record a single app window")
    };

    public ObservableCollection<WindowItem> Windows { get; } = new();

    public ObservableCollection<AudioDeviceItem> Microphones { get; } = new();

    public ObservableCollection<AudioDeviceItem> SystemAudioDevices { get; } = new();

    public ObservableCollection<RecordingItem> Recordings { get; } = new();

    public ObservableCollection<RecordingItem> FilteredRecordings { get; } = new();

    public string AppVersion { get; }

    public bool HasRecordings => !IsLibraryEmpty;

    public bool IsRecordPage => ActivePage == "record";

    public bool IsLibraryPage => ActivePage == "library";

    public bool IsSettingsPage => ActivePage == "settings";

    public bool IsMonitorSource => SelectedSource?.Kind != CaptureTargetKind.Window;

    public bool IsWindowSource => SelectedSource?.Kind == CaptureTargetKind.Window;

    public string SourceBadgeText => IsWindowSource ? "WINDOW" : "SCREEN";

    public string TargetLabel => IsWindowSource ? "Window" : "Display";

    public string CapturePreviewTitle => IsWindowSource
        ? SelectedWindow?.Name ?? "Choose a window"
        : SelectedMonitor?.Resolution ?? "No display";

    public string CapturePreviewSubtitle => IsWindowSource
        ? SelectedWindow?.Detail ?? "Pick an app window"
        : SelectedMonitor?.Name ?? "No display detected";

    public bool IsEngineActive => IsRecording || IsBusy || IsCountingDown || _engine.IsRecording;

    public bool IsConfigurationEnabled => !IsEngineActive;

    public bool CanEditMicrophone => IsConfigurationEnabled && MicrophoneEnabled;

    public bool CanEditSystemAudio => IsConfigurationEnabled && SystemAudioEnabled;

    public bool CanNavigate => !IsEngineActive;

    public string RecorderStateLabel => IsRecording
        ? "RECORDING"
        : IsCountingDown
            ? "STARTING"
            : IsBusy
                ? "WORKING"
                : "READY TO RECORD";

    public string PreviewStateText => IsRecording
        ? "Recording in progress"
        : IsCountingDown
            ? $"Starting in {CountdownNumber}s..."
            : IsBusy
                ? "Getting ready"
                : "Ready to capture";

    public string WindowTitle => IsRecording
        ? $"{ElapsedText}  •  Recording  •  Jeilo Recorder"
        : "Jeilo Recorder";

    public bool HasAudioSource => MicrophoneEnabled || SystemAudioEnabled;

    public string PageTitle => ActivePage switch
    {
        "library" => "Library",
        "settings" => "Settings",
        _ => "Record"
    };

    public string PageSubtitle => ActivePage switch
    {
        "library" => "Your recordings, stored privately on this PC.",
        "settings" => "Configure theme, recording presets, audio, and shortcuts.",
        _ => "Capture your screen or window, microphone, and system audio."
    };

    [RelayCommand]
    private void Navigate(string? page)
    {
        if (string.IsNullOrWhiteSpace(page) || IsEngineActive)
        {
            return;
        }

        ActivePage = page;
        if (page == "library")
        {
            RefreshLibrary();
        }
        else
        {
            CancelMetadataLoads();

            if (page == "record" && IsWindowSource)
            {
                LoadWindows();
            }
        }
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        if (IsEngineActive)
        {
            return;
        }

        LoadMonitors();
        LoadWindows();
        LoadAudioDevices();
        UpdateAudioSummary();
        UpdateRecordingAvailability();
        ShowToast("Hardware devices refreshed");
    }

    [RelayCommand]
    private void RefreshLibrary()
    {
        CancelMetadataLoads();
        Recordings.Clear();
        var items = new List<RecordingItem>();

        if (Directory.Exists(OutputDirectory))
        {
            try
            {
                var files = new DirectoryInfo(OutputDirectory)
                    .EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(file => file.LastWriteTime)
                    .Take(120);

                foreach (var file in files)
                {
                    var item = new RecordingItem(file);
                    items.Add(item);
                    Recordings.Add(item);
                }
            }
            catch
            {
                // An unreadable folder should simply show as empty.
            }
        }

        ApplyLibraryFilter();

        if (ActivePage == "library" && _ffmpegPath is not null && items.Count > 0)
        {
            StartMetadataLoads(items);
        }
    }

    public void ApplyLibraryFilter()
    {
        var query = LibrarySearchQuery?.Trim();
        IEnumerable<RecordingItem> items = Recordings;

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(item => item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        items = LibrarySortOrder switch
        {
            "Oldest first" => items.OrderBy(item => new FileInfo(item.Path).LastWriteTime),
            "Largest size" => items.OrderByDescending(item => new FileInfo(item.Path).Length),
            "Name" => items.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            _ => items.OrderByDescending(item => new FileInfo(item.Path).LastWriteTime)
        };

        FilteredRecordings.Clear();
        foreach (var item in items)
        {
            FilteredRecordings.Add(item);
        }

        IsLibraryEmpty = FilteredRecordings.Count == 0;
        LibraryCountText = FilteredRecordings.Count == 1
            ? "1 recording"
            : $"{FilteredRecordings.Count} recordings";
    }

    [RelayCommand]
    private void ClearSearch()
    {
        LibrarySearchQuery = string.Empty;
    }

    private void StartMetadataLoads(IReadOnlyList<RecordingItem> items)
    {
        var cancellation = new CancellationTokenSource();
        _metadataCancellation = cancellation;
        var tasks = items
            .Select(item => LoadLibraryMetadataAsync(item, cancellation.Token))
            .ToArray();

        _ = Task.WhenAll(tasks).ContinueWith(
            _ => cancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task LoadLibraryMetadataAsync(RecordingItem item, CancellationToken cancellationToken)
    {
        try
        {
            await _metadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var metadata = await RecordingMetadataReader
                    .ReadAsync(_ffmpegPath!, item.Path, _thumbnailCacheDirectory, cancellationToken)
                    .ConfigureAwait(false);

                if (metadata is null || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        item.ApplyMetadata(metadata.Duration, metadata.ThumbnailPath);
                    }
                });
            }
            finally
            {
                _metadataGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled.
        }
        catch
        {
            // Missing metadata must never crash the library.
        }
    }

    private void CancelMetadataLoads()
    {
        var cancellation = _metadataCancellation;
        _metadataCancellation = null;

        try
        {
            cancellation?.Cancel();
        }
        catch
        {
            // Best effort.
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(OutputDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = OutputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetError("Could not open the folder", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenRecording(RecordingItem? item)
    {
        if (item is null || !File.Exists(item.Path))
        {
            SetError("Recording not found", item?.Path ?? "The file does not exist.");
            return;
        }

        OpenPath(item.Path);
    }

    [RelayCommand]
    private void ShowInFolder(RecordingItem? item)
    {
        if (item is null)
        {
            return;
        }

        ShowPathInFolder(item.Path);
    }

    [RelayCommand]
    private void CopyRecordingPath(RecordingItem? item)
    {
        if (item is null)
        {
            return;
        }

        RequestSetClipboardText?.Invoke(item.Path);
        ShowToast("File path copied to clipboard");
    }

    [RelayCommand]
    private void DeleteRecording(RecordingItem? item)
    {
        if (item is null)
        {
            return;
        }

        if (RequestConfirmDelete is not null)
        {
            RequestConfirmDelete(item, confirmed =>
            {
                if (confirmed)
                {
                    ExecuteDelete(item);
                }
            });
        }
        else
        {
            ExecuteDelete(item);
        }
    }

    private void ExecuteDelete(RecordingItem item)
    {
        try
        {
            if (File.Exists(item.Path))
            {
                File.Delete(item.Path);
            }

            Recordings.Remove(item);
            FilteredRecordings.Remove(item);

            if (LastSavedPath == item.Path)
            {
                HasLastSavedFile = false;
                LastSavedPath = string.Empty;
                LastSavedFileName = string.Empty;
            }

            ApplyLibraryFilter();
            ShowToast($"Deleted {item.FileName}");
        }
        catch (Exception ex)
        {
            SetError("Could not delete file", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLastSaved()
    {
        if (!HasLastSavedFile)
        {
            return;
        }

        if (!File.Exists(LastSavedPath))
        {
            SetError("That recording is no longer available", LastSavedPath);
            return;
        }

        OpenPath(LastSavedPath);
    }

    [RelayCommand]
    private void ShowLastSavedInFolder()
    {
        if (HasLastSavedFile && File.Exists(LastSavedPath))
        {
            ShowPathInFolder(LastSavedPath);
        }
    }

    private void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetError("Could not open the recording", ex.Message);
        }
    }

    private void ShowPathInFolder(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(path);
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            SetError("Could not show the file", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (IsCountingDown)
        {
            CancelCountdown();
            return;
        }

        if (IsRecording)
        {
            await StopRecordingAsync();
        }
        else
        {
            if (SelectedCountdownSeconds > 0)
            {
                await StartCountdownAndRecordAsync();
            }
            else
            {
                await StartRecordingAsync();
            }
        }
    }

    [RelayCommand]
    private void CancelCountdown()
    {
        _countdownCancellation?.Cancel();
        _countdownCancellation = null;
        IsCountingDown = false;
        IsBusy = false;
        RecordButtonText = "Start recording";
        StatusText = "Ready to record";
        StatusDetail = "Countdown cancelled.";
        UpdateRecordingAvailability();
    }

    private async Task StartCountdownAndRecordAsync()
    {
        if (!IsFfmpegReady || !HasAudioSource)
        {
            // Validate prerequisites before counting down!
            if (!IsFfmpegReady)
            {
                SetError("FFmpeg is missing", "The encoder could not be found. Reinstall or repair Jeilo Recorder.");
                return;
            }

            if (!HasAudioSource)
            {
                SetError("Choose an audio source", "Enable the microphone or system audio before recording.");
                return;
            }
        }

        IsCountingDown = true;
        IsBusy = true;
        RecordButtonText = "Cancel countdown";
        StatusText = "Starting soon...";
        StatusDetail = $"Recording in {SelectedCountdownSeconds} seconds...";
        UpdateRecordingAvailability();

        _countdownCancellation = new CancellationTokenSource();
        var token = _countdownCancellation.Token;

        try
        {
            for (var i = SelectedCountdownSeconds; i > 0; i--)
            {
                CountdownNumber = i;
                await Task.Delay(1000, token);
            }

            IsCountingDown = false;
            await StartRecordingAsync();
        }
        catch (OperationCanceledException)
        {
            IsCountingDown = false;
            IsBusy = false;
            RecordButtonText = "Start recording";
            StatusText = "Ready to record";
            StatusDetail = "Countdown was cancelled.";
            UpdateRecordingAvailability();
        }
    }

    [RelayCommand]
    private async Task ToggleRecordingShortcutAsync()
    {
        if (IsBusy && !IsCountingDown)
        {
            return;
        }

        if (!IsRecording && !IsCountingDown && ActivePage != "record")
        {
            ActivePage = "record";
        }

        await ToggleRecordingAsync();
    }

    public void SetOutputDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        OutputDirectory = directory;
        PersistSettings();
        RefreshLibrary();
        ShowToast("Save directory updated");
    }

    public void ShowToast(string message, int durationMs = 2600)
    {
        ToastMessage = message;
        IsToastVisible = true;

        _toastCancellation?.Cancel();
        _toastCancellation = new CancellationTokenSource();
        var token = _toastCancellation.Token;

        Task.Delay(durationMs, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Dispatcher.UIThread.Post(() => IsToastVisible = false);
            }
        }, TaskScheduler.Default);
    }

    public async Task ShutdownAsync()
    {
        CancelMetadataLoads();
        PersistSettings();

        try
        {
            if (_engine.IsRecording)
            {
                await _engine.StopAsync();
            }
        }
        catch
        {
            // Closing the app must continue even if finalizing fails.
        }

        await _engine.DisposeAsync();
    }

    private async Task StartRecordingAsync()
    {
        if (!IsFfmpegReady)
        {
            SetError("FFmpeg is missing", "The encoder could not be found. Reinstall or repair Jeilo Recorder.");
            return;
        }

        if (!HasAudioSource)
        {
            SetError("Choose an audio source", "Enable the microphone or system audio before recording.");
            return;
        }

        if (MicrophoneEnabled && SelectedMicrophone is null)
        {
            SetError("No microphone found", "Pick a microphone or turn the microphone off.");
            return;
        }

        if (SystemAudioEnabled && SelectedSystemAudio is null)
        {
            SetError("No system audio device found", "Pick a playback device or turn system audio off.");
            return;
        }

        if (IsWindowSource)
        {
            if (SelectedWindow is null)
            {
                SetError("Choose a window", "Open the app you want to record and select its window.");
                return;
            }

            if (!WindowCatalog.IsAvailable(SelectedWindow.Handle))
            {
                LoadWindows();
                SetError("The window is not available", "The selected window was closed or minimized. Choose another one.");
                return;
            }
        }
        else if (SelectedMonitor is null)
        {
            SetError("No display found", "Connect a display and refresh the device list.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            SetError("Choose a save folder", "Select where recordings should be saved.");
            return;
        }

        try
        {
            Directory.CreateDirectory(OutputDirectory);
        }
        catch (Exception ex)
        {
            SetError("The save folder is not available", ex.Message);
            return;
        }

        PersistSettings();
        IsStatusError = false;
        IsBusy = true;
        RecordButtonText = "Getting ready...";
        StatusText = "Getting ready";
        StatusDetail = IsWindowSource
            ? "Starting the window capture and encoder..."
            : "Starting the screen capture and encoder...";
        UpdateRecordingAvailability();

        try
        {
            var options = new RecordingOptions
            {
                OutputDirectory = OutputDirectory,
                TargetKind = SelectedSource?.Kind ?? CaptureTargetKind.Monitor,
                MonitorIndex = SelectedMonitor?.Index ?? 0,
                WindowHandle = IsWindowSource ? SelectedWindow?.Handle ?? IntPtr.Zero : IntPtr.Zero,
                Fps = SelectedFps,
                Quality = SelectedQuality?.Value ?? VideoQuality.High,
                Encoder = SelectedEncoder?.Value ?? "auto",
                MicrophoneEnabled = MicrophoneEnabled,
                MicrophoneId = SelectedMicrophone?.Id,
                MicrophoneGain = MicrophoneGain,
                SystemAudioEnabled = SystemAudioEnabled,
                SystemAudioId = SelectedSystemAudio?.Id,
                SystemAudioGain = SystemAudioGain,
                IncludeCursor = IncludeCursor,
                MaxDuration = TimeSpan.FromHours(4)
            };

            await _engine.StartAsync(options);

            HasLastSavedFile = false;
            LastSavedPath = string.Empty;
            IsRecording = true;
            IsBusy = false;
            RecordButtonText = "Stop recording";
            StatusText = "Recording";
            StatusDetail = IsWindowSource
                ? "Capturing the selected window and audio."
                : "Capturing your screen and audio.";

            if (MinimizeOnRecord)
            {
                RequestMinimize?.Invoke();
            }
        }
        catch (Exception ex)
        {
            IsRecording = false;
            IsBusy = false;
            RecordButtonText = "Start recording";
            SetError("Recording could not start", ex.Message);
        }
        finally
        {
            UpdateRecordingAvailability();
        }
    }

    private async Task StopRecordingAsync()
    {
        if (!IsRecording && !IsBusy)
        {
            return;
        }

        IsBusy = true;
        RecordButtonText = "Saving video...";
        StatusText = "Finishing the video";
        StatusDetail = "Writing the final file. This can take a few seconds.";
        UpdateRecordingAvailability();

        try
        {
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            SetError("Recording failed", ex.Message);
        }
        finally
        {
            IsRecording = false;
            IsBusy = false;
            RecordButtonText = "Start recording";
            UpdateRecordingAvailability();

            if (MinimizeOnRecord)
            {
                RequestRestore?.Invoke();
            }
        }
    }

    private void ApplyProgress(RecordingProgress progress)
    {
        ElapsedText = FormatElapsed(progress.Elapsed);
        UpdateMeters(progress);

        switch (progress.State)
        {
            case RecordingState.Preparing:
                IsRecording = false;
                IsBusy = true;
                RecordButtonText = "Getting ready...";
                StatusText = "Getting ready";
                StatusDetail = progress.Message ?? "Starting the screen capture and encoder...";
                break;

            case RecordingState.Recording:
                IsRecording = true;
                IsBusy = false;
                RecordButtonText = "Stop recording";
                StatusText = "Recording";
                StatusDetail =
                    $"{progress.FramesWritten:N0} frames  •  {progress.CpuPercent:0}% CPU  •  " +
                    $"{progress.FramesDropped} dropped";
                break;

            case RecordingState.Finalizing:
                IsRecording = false;
                IsBusy = true;
                RecordButtonText = "Saving video...";
                StatusText = "Finishing the video";
                StatusDetail = "Writing the final file. This can take a few seconds.";
                break;

            case RecordingState.Completed:
                IsRecording = false;
                IsBusy = false;
                RecordButtonText = "Start recording";
                IsStatusError = false;
                StatusText = "Recording saved";
                StatusDetail = Path.GetFileName(progress.OutputPath);
                LastSavedFileName = Path.GetFileName(progress.OutputPath);
                LastSavedPath = progress.OutputPath;
                HasLastSavedFile = true;
                MicLevel = 0;
                SystemLevel = 0;
                MicLevelText = MicrophoneEnabled ? "0%" : "Off";
                SystemLevelText = SystemAudioEnabled ? "0%" : "Off";
                RefreshLibrary();
                ShowToast("Recording saved successfully!");
                break;

            case RecordingState.Failed:
                IsRecording = false;
                IsBusy = false;
                RecordButtonText = "Start recording";
                SetError("Recording failed", progress.Message ?? "Something went wrong.");
                break;
        }

        UpdateRecordingAvailability();
    }

    private void UpdateMeters(RecordingProgress progress)
    {
        MicLevel = MicrophoneEnabled ? ToMeter(progress.MicrophonePeak) : 0;
        SystemLevel = SystemAudioEnabled ? ToMeter(progress.SystemAudioPeak) : 0;
        MicLevelText = MicrophoneEnabled ? $"{MicLevel:0}%" : "Off";
        SystemLevelText = SystemAudioEnabled ? $"{SystemLevel:0}%" : "Off";

        IsMicWarning = MicLevel >= 75 && MicLevel < 92;
        IsMicClipping = MicLevel >= 92;
        IsSystemWarning = SystemLevel >= 75 && SystemLevel < 92;
        IsSystemClipping = SystemLevel >= 92;
    }

    private void LoadFfmpeg()
    {
        var path = FfmpegLocator.Find();
        if (path is null)
        {
            IsFfmpegReady = false;
            _ffmpegPath = null;
            FfmpegStatus = "FFmpeg not found";
            FfmpegPathDisplay = "The encoder is required to record.";
            Encoders.Clear();
            Encoders.Add(new EncoderOption("auto", "Automatic", false));
            SelectedEncoder = Encoders[0];
            SetError("Encoder missing", "The bundled encoder could not be found. Reinstall or repair Jeilo Recorder.");
            return;
        }

        _ffmpegPath = path;
        IsFfmpegReady = true;
        FfmpegStatus = "FFmpeg ready";
        FfmpegPathDisplay = path;
        Encoders.Clear();
        foreach (var encoder in EncoderCatalog.Discover(path).Where(encoder => encoder.IsAvailable))
        {
            Encoders.Add(new EncoderOption(encoder.Value, encoder.DisplayName, encoder.IsAvailable));
        }

        SelectedEncoder = Encoders.FirstOrDefault(encoder => encoder.Value == _settings.Encoder)
            ?? Encoders.FirstOrDefault(encoder => encoder.Value == "auto")
            ?? Encoders.FirstOrDefault();
    }

    private void LoadMonitors()
    {
        var previousIndex = SelectedMonitor?.Index ?? _settings.MonitorIndex;
        Monitors.Clear();

        try
        {
            var monitors = MonitorCatalog.Enumerate();
            for (var i = 0; i < monitors.Count; i++)
            {
                var monitor = monitors[i];
                var name = monitor.IsPrimary ? $"Display {i + 1} (Primary)" : $"Display {i + 1}";
                Monitors.Add(new MonitorItem(
                    i,
                    name,
                    $"{monitor.Bounds.Width} × {monitor.Bounds.Height}",
                    $"{monitor.Bounds.Width}×{monitor.Bounds.Height} at {monitor.Dpi} DPI",
                    monitor.IsPrimary));
            }
        }
        catch
        {
            // Monitor enumeration can fail if desktop is transitioning.
        }

        SelectedMonitor = Monitors.FirstOrDefault(monitor => monitor.Index == previousIndex)
            ?? Monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? Monitors.FirstOrDefault();

        UpdateMonitorLabel();
        NotifyCaptureSourceChanged();
    }

    private void LoadWindows()
    {
        var previousHandle = SelectedWindow?.Handle ?? IntPtr.Zero;
        Windows.Clear();

        try
        {
            foreach (var window in WindowCatalog.Enumerate())
            {
                Windows.Add(new WindowItem(
                    window.Handle,
                    window.Title,
                    $"{window.ProcessName}  •  {window.Bounds.Width} × {window.Bounds.Height}"));
            }
        }
        catch
        {
            // Windows can close while the list is being built.
        }

        SelectedWindow = Windows.FirstOrDefault(window =>
                             previousHandle != IntPtr.Zero && window.Handle == previousHandle)
                         ?? Windows.FirstOrDefault();

        NotifyCaptureSourceChanged();
    }

    private void NotifyCaptureSourceChanged()
    {
        OnPropertyChanged(nameof(IsMonitorSource));
        OnPropertyChanged(nameof(IsWindowSource));
        OnPropertyChanged(nameof(SourceBadgeText));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(CapturePreviewTitle));
        OnPropertyChanged(nameof(CapturePreviewSubtitle));
    }

    private void LoadAudioDevices()
    {
        var previousMic = SelectedMicrophone?.Id ?? _settings.MicrophoneId;
        var previousSystem = SelectedSystemAudio?.Id ?? _settings.SystemAudioId;

        Microphones.Clear();
        SystemAudioDevices.Clear();

        try
        {
            foreach (var device in AudioDeviceCatalog.Enumerate(DataFlow.Capture)
                         .OrderByDescending(device => device.IsDefaultCommunications))
            {
                var name = device.IsDefaultCommunications
                    ? $"{device.FriendlyName}  (default)"
                    : device.FriendlyName;
                Microphones.Add(new AudioDeviceItem(
                    device.Id,
                    name,
                    $"{device.SampleRate / 1000.0:0.#} kHz  •  {device.MixFormat}",
                    device.IsDefaultCommunications));
            }

            foreach (var device in AudioDeviceCatalog.Enumerate(DataFlow.Render)
                         .OrderByDescending(device => device.IsDefaultConsole))
            {
                var name = device.IsDefaultConsole
                    ? $"{device.FriendlyName}  (default)"
                    : device.FriendlyName;
                SystemAudioDevices.Add(new AudioDeviceItem(
                    device.Id,
                    name,
                    $"{device.SampleRate / 1000.0:0.#} kHz  •  {device.MixFormat}",
                    device.IsDefaultConsole));
            }
        }
        catch
        {
            // Audio devices can disappear while Windows is switching endpoints.
        }

        SelectedMicrophone = Microphones.FirstOrDefault(device => device.Id == previousMic)
            ?? Microphones.FirstOrDefault(device => device.IsDefault)
            ?? Microphones.FirstOrDefault();

        SelectedSystemAudio = SystemAudioDevices.FirstOrDefault(device => device.Id == previousSystem)
            ?? SystemAudioDevices.FirstOrDefault(device => device.IsDefault)
            ?? SystemAudioDevices.FirstOrDefault();
    }

    private void ApplySettings()
    {
        MicrophoneEnabled = _settings.MicrophoneEnabled;
        SystemAudioEnabled = _settings.SystemAudioEnabled;
        OutputDirectory = _settings.OutputDirectory;
        SelectedFps = _settings.Fps;
        SelectedQuality = Qualities.FirstOrDefault(quality => quality.Value == _settings.Quality)
            ?? Qualities.First(quality => quality.Value == VideoQuality.High);
        IncludeCursor = _settings.IncludeCursor;
        SelectedThemeMode = _settings.ThemeMode;
        SelectedCountdownSeconds = _settings.CountdownSeconds;
        MinimizeOnRecord = _settings.MinimizeOnRecord;
        MicrophoneGain = _settings.MicrophoneGain;
        SystemAudioGain = _settings.SystemAudioGain;
        SaveStatusText = "Settings are saved automatically.";

        App.SetTheme(SelectedThemeMode);
    }

    private void PersistSettings()
    {
        if (_initializing)
        {
            return;
        }

        _settings.OutputDirectory = OutputDirectory;
        _settings.MicrophoneEnabled = MicrophoneEnabled;
        _settings.SystemAudioEnabled = SystemAudioEnabled;
        _settings.MicrophoneId = SelectedMicrophone?.Id;
        _settings.SystemAudioId = SelectedSystemAudio?.Id;
        _settings.MonitorIndex = SelectedMonitor?.Index ?? 0;
        _settings.Fps = SelectedFps;
        _settings.Quality = SelectedQuality?.Value ?? VideoQuality.High;
        _settings.Encoder = SelectedEncoder?.Value ?? "auto";
        _settings.IncludeCursor = IncludeCursor;
        _settings.ThemeMode = SelectedThemeMode;
        _settings.CountdownSeconds = SelectedCountdownSeconds;
        _settings.MinimizeOnRecord = MinimizeOnRecord;
        _settings.MicrophoneGain = MicrophoneGain;
        _settings.SystemAudioGain = SystemAudioGain;

        try
        {
            _settingsStore.Save(_settings);
        }
        catch
        {
            SaveStatusText = "Settings could not be saved.";
        }
    }

    private void UpdateRecordingAvailability()
    {
        CanToggleRecording = IsRecording || IsCountingDown || (!IsBusy && IsFfmpegReady);
        OnPropertyChanged(nameof(HasAudioSource));
        OnPropertyChanged(nameof(IsEngineActive));
        OnPropertyChanged(nameof(IsConfigurationEnabled));
        OnPropertyChanged(nameof(CanEditMicrophone));
        OnPropertyChanged(nameof(CanEditSystemAudio));
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(RecorderStateLabel));
        OnPropertyChanged(nameof(PreviewStateText));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void UpdateAudioSummary()
    {
        AudioSummary = (MicrophoneEnabled, SystemAudioEnabled) switch
        {
            (true, true) => "Microphone + system audio",
            (true, false) => "Microphone only",
            (false, true) => "System audio only",
            _ => "No audio source selected"
        };
    }

    private void UpdateMonitorLabel()
    {
        SelectedMonitorLabel = SelectedMonitor is null
            ? "No display detected"
            : $"{SelectedMonitor.Name}  •  {SelectedMonitor.Resolution}";
    }

    private void SetError(string title, string detail)
    {
        IsStatusError = true;
        StatusText = title;
        StatusDetail = detail;
    }

    private static double ToMeter(float peak)
    {
        return Math.Round(Math.Sqrt(Math.Clamp(peak, 0f, 1f)) * 100);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsRecordPage));
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(PageTitle));
        OnPropertyChanged(nameof(PageSubtitle));
    }

    partial void OnIsLibraryEmptyChanged(bool value)
    {
        OnPropertyChanged(nameof(HasRecordings));
    }

    partial void OnIsRecordingChanged(bool value)
    {
        UpdateRecordingAvailability();
    }

    partial void OnElapsedTextChanged(string value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnIsBusyChanged(bool value)
    {
        UpdateRecordingAvailability();
    }

    partial void OnIsCountingDownChanged(bool value)
    {
        UpdateRecordingAvailability();
    }

    partial void OnCountdownNumberChanged(int value)
    {
        OnPropertyChanged(nameof(PreviewStateText));
    }

    partial void OnLibrarySearchQueryChanged(string value)
    {
        ApplyLibraryFilter();
    }

    partial void OnLibrarySortOrderChanged(string value)
    {
        ApplyLibraryFilter();
    }

    partial void OnSelectedThemeModeChanged(string value)
    {
        App.SetTheme(value);
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedCountdownSecondsChanged(int value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnMinimizeOnRecordChanged(bool value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnMicrophoneGainChanged(float value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSystemAudioGainChanged(float value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnMicrophoneEnabledChanged(bool value)
    {
        UpdateAudioSummary();
        UpdateRecordingAvailability();
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSystemAudioEnabledChanged(bool value)
    {
        UpdateAudioSummary();
        UpdateRecordingAvailability();
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedMicrophoneChanged(AudioDeviceItem? value)
    {
        UpdateRecordingAvailability();
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedSystemAudioChanged(AudioDeviceItem? value)
    {
        UpdateRecordingAvailability();
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedMonitorChanged(MonitorItem? value)
    {
        UpdateMonitorLabel();
        NotifyCaptureSourceChanged();
        UpdateRecordingAvailability();
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedSourceChanged(CaptureSourceOption? value)
    {
        if (value?.Kind == CaptureTargetKind.Window)
        {
            LoadWindows();
        }

        NotifyCaptureSourceChanged();
        UpdateRecordingAvailability();
    }

    partial void OnSelectedWindowChanged(WindowItem? value)
    {
        NotifyCaptureSourceChanged();
        UpdateRecordingAvailability();
    }

    partial void OnSelectedFpsChanged(int value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedQualityChanged(QualityOption? value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnSelectedEncoderChanged(EncoderOption? value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnIncludeCursorChanged(bool value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnOutputDirectoryChanged(string value)
    {
        if (!_initializing)
        {
            PersistSettings();
        }
    }

    partial void OnIsFfmpegReadyChanged(bool value)
    {
        UpdateRecordingAvailability();
    }
}
