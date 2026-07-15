using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Recorder.Core;

namespace Recorda;

public partial class MainWindow : Window
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "Recorda");
    private readonly WasapiDualRecorder _recorder = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly ObservableCollection<RecordingItem> _recordings = new();
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly Stopwatch _recordingClock = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private RecordingState _state = RecordingState.Idle;
    private Task? _finalizeTask;
    private CancellationTokenSource? _finalizeCancellation;
    private string? _sessionDirectory;
    private string? _currentMicFile;
    private string? _currentSystemFile;
    private string? _currentOutputFolder;
    private bool _isMicrophoneMuted;
    private bool _initializing = true;
    private bool _allowClose;
    private bool _closeInProgress;

    public MainWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _log;
        RecordingsList.ItemsSource = _recordings;

        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        string defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Recorda");
        OutputFolderBox.Text = string.IsNullOrWhiteSpace(_settings.OutputFolder)
            ? defaultFolder
            : _settings.OutputFolder;
        MicGainSlider.Value = Math.Clamp(_settings.MicrophoneGain, 0, 1);
        SystemGainSlider.Value = Math.Clamp(_settings.SystemGain, 0, 1);

        EnsureDirectory(OutputFolderBox.Text);
        CleanAbandonedSessions();
        _uiTimer.Tick += UiTimer_Tick;
        Loaded += async (_, _) =>
        {
            await LoadDevicesAsync();
            _initializing = false;
            await ReloadRecordingsAsync();
            Log("Ready");
        };
        SetState(RecordingState.Starting, "Loading audio devices…");
    }

    private async Task LoadDevicesAsync()
    {
        string? previousMic = (MicCombo.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.MicrophoneDeviceId;
        string? previousOutput = (OutputCombo.SelectedItem as AudioDeviceInfo)?.Id ?? _settings.OutputDeviceId;

        try
        {
            var devices = await Task.Run(() => (
                Microphones: WasapiDualRecorder.GetMicDevices(),
                Outputs: WasapiDualRecorder.GetOutputDevices()));
            var microphones = devices.Microphones;
            var outputs = devices.Outputs;
            MicCombo.ItemsSource = microphones;
            OutputCombo.ItemsSource = outputs;
            MicCombo.SelectedItem = microphones.FirstOrDefault(device => device.Id == previousMic) ?? microphones.FirstOrDefault();
            OutputCombo.SelectedItem = outputs.FirstOrDefault(device => device.Id == previousOutput) ?? outputs.FirstOrDefault();

            if (microphones.Count == 0 || outputs.Count == 0)
                SetState(RecordingState.Error, "Connect both a microphone and playback device, then refresh.");
            else
                SetState(RecordingState.Idle, "Choose Record when you’re ready.");
        }
        catch (Exception ex)
        {
            SetState(RecordingState.Error, "Audio devices could not be loaded. Try Refresh.");
            Log($"Device refresh failed: {ex.Message}");
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        SetState(RecordingState.Starting, "Refreshing audio devices…");
        await LoadDevicesAsync();
    }

    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing)
            SaveSettings();
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initializing)
            SaveSettings();
    }

    private async void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select recording folder",
            InitialDirectory = OutputFolderBox.Text
        };

        if (dialog.ShowDialog(this) != true)
            return;

        OutputFolderBox.Text = dialog.FolderName;
        SaveSettings();
        Log($"Recording folder changed to {dialog.FolderName}");
        await ReloadRecordingsAsync();
    }

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        EnsureDirectory(OutputFolderBox.Text);
        OpenWithShell(OutputFolderBox.Text);
    }

    private async void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_state == RecordingState.Recording)
            await StopRecordingAsync();
        else if (_state is RecordingState.Idle or RecordingState.Error)
            StartRecording();
    }

    private void StartRecording()
    {
        if (MicCombo.SelectedItem is not AudioDeviceInfo microphone ||
            OutputCombo.SelectedItem is not AudioDeviceInfo output)
        {
            SetState(RecordingState.Error, "Select a microphone and system-audio device first.");
            return;
        }

        string folder = OutputFolderBox.Text;
        try
        {
            EnsureDirectory(folder);
            EnsureDiskSpace(folder);
            Directory.CreateDirectory(TempRoot);
            EnsureDiskSpace(TempRoot);
            _sessionDirectory = Path.Combine(TempRoot, $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sessionDirectory);
            _currentMicFile = Path.Combine(_sessionDirectory, "microphone.wav");
            _currentSystemFile = Path.Combine(_sessionDirectory, "system.wav");
            _currentOutputFolder = folder;

            SetState(RecordingState.Starting, "Opening audio devices…");
            _recorder.Start(_currentMicFile, _currentSystemFile, microphone.Id, output.Id);
            _isMicrophoneMuted = false;
            _recordingClock.Restart();
            _uiTimer.Start();
            SetState(RecordingState.Recording, "Recording microphone and system audio.");
            Log($"Recording started → {folder}");
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(_sessionDirectory);
            ClearSessionPaths();
            SetState(RecordingState.Error, $"Couldn’t start: {ex.Message}");
            Log($"Start failed: {ex.Message}");
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_finalizeTask is not null)
        {
            await _finalizeTask;
            return;
        }

        _recordingClock.Stop();
        _uiTimer.Stop();
        _recorder.SetMicrophoneMuted(false);
        _isMicrophoneMuted = false;
        UpdateMeters(reset: true);
        SetState(RecordingState.Finalizing, "Stopping audio devices…");
        Log("Recording stopped — preparing the final mix");

        string? micFile = _currentMicFile;
        string? systemFile = _currentSystemFile;
        string? outputFolder = _currentOutputFolder;
        string? sessionDirectory = _sessionDirectory;
        TimeSpan duration = _recordingClock.Elapsed;
        double micGain = MicGainSlider.Value;
        double systemGain = SystemGainSlider.Value;
        ClearSessionPaths();

        _finalizeCancellation = new CancellationTokenSource();
        _finalizeTask = FinishRecordingAsync(micFile, systemFile, outputFolder, sessionDirectory,
            duration, (float)micGain, (float)systemGain, _finalizeCancellation.Token);

        try
        {
            await _finalizeTask;
        }
        finally
        {
            _finalizeTask = null;
            _finalizeCancellation?.Dispose();
            _finalizeCancellation = null;
        }
    }

    private async Task FinishRecordingAsync(
        string? micFile,
        string? systemFile,
        string? outputFolder,
        string? sessionDirectory,
        TimeSpan duration,
        float micGain,
        float systemGain,
        CancellationToken cancellationToken)
    {
        string? partialFile = null;
        try
        {
            await _recorder.StopAsync(cancellationToken: cancellationToken);
            if (micFile is null || systemFile is null || outputFolder is null)
                throw new InvalidOperationException("The recording session did not contain valid output paths.");

            string finalFile = CreateUniqueOutputPath(outputFolder, DateTime.Now);
            partialFile = finalFile + ".partial";
            SetState(RecordingState.Finalizing, "Encoding the final MP3…");
            FinalizingText.Text = "ENCODING  0%";

            var progress = new Progress<double>(value =>
            {
                FinalizingProgress.Value = value;
                FinalizingText.Text = $"ENCODING  {value:P0}";
            });

            TimeSpan micPad = _recorder.MicSilencePad;
            TimeSpan systemPad = _recorder.SystemSilencePad;
            await Task.Run(() => WavMerger.Merge(
                micFile,
                systemFile,
                partialFile,
                micPad,
                systemPad,
                micGain,
                systemGain,
                progress,
                cancellationToken), cancellationToken);

            File.Move(partialFile, finalFile);
            partialFile = null;
            TryDeleteDirectory(sessionDirectory);

            _recordings.Insert(0, RecordingItem.FromFile(finalFile, duration));
            TrimRecentRecordings();
            UpdateEmptyRecordingsState();

            long micPadMs = (long)micPad.TotalMilliseconds;
            long systemPadMs = (long)systemPad.TotalMilliseconds;
            string offset = micPadMs > 0 ? $"mic +{micPadMs} ms" : systemPadMs > 0 ? $"system +{systemPadMs} ms" : "aligned";
            TimerText.Text = "00:00:00";
            SetState(RecordingState.Idle, $"Saved {Path.GetFileName(finalFile)}");
            Log($"Saved: {finalFile} ({offset})");
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(partialFile);
            SetState(RecordingState.Error, "Export was cancelled. The source audio is preserved in diagnostics.");
            Log($"Export cancelled; source audio remains in {sessionDirectory}");
        }
        catch (Exception ex)
        {
            TryDeleteFile(partialFile);
            SetState(RecordingState.Error, "Export failed. The source audio has been preserved.");
            Log($"Export failed: {ex.Message}. Source: {sessionDirectory}");
        }
    }

    private void MuteMicBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_state != RecordingState.Recording)
            return;

        _isMicrophoneMuted = !_isMicrophoneMuted;
        _recorder.SetMicrophoneMuted(_isMicrophoneMuted);
        MuteMicBtn.Content = _isMicrophoneMuted ? "UNMUTE" : "MUTE MIC";
        MuteMicBtn.Background = _isMicrophoneMuted ? FindBrush("SignalDarkBrush") : FindBrush("RaisedBrush");
        StatusText.Text = _isMicrophoneMuted
            ? "Recording system audio. Microphone is muted."
            : "Recording microphone and system audio.";
        Log(_isMicrophoneMuted ? "Microphone muted" : "Microphone unmuted");
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        TimerText.Text = _recordingClock.Elapsed.ToString(@"hh\:mm\:ss");
        UpdateMeters(reset: false);
    }

    private void UpdateMeters(bool reset)
    {
        double mic = reset ? 0 : Math.Sqrt(_recorder.MicrophonePeak);
        double system = reset ? 0 : Math.Sqrt(_recorder.SystemPeak);
        MicLevelBar.Value = reset ? 0 : Math.Max(mic, MicLevelBar.Value * 0.82);
        SystemLevelBar.Value = reset ? 0 : Math.Max(system, SystemLevelBar.Value * 0.82);
        MicLevelText.Text = reset ? "—" : FormatDecibels(_recorder.MicrophonePeak);
        SystemLevelText.Text = reset ? "—" : FormatDecibels(_recorder.SystemPeak);
        MicLevelBar.Foreground = MicLevelBar.Value > 0.94 ? FindBrush("AmberBrush") : FindBrush("MeterBrush");
        SystemLevelBar.Foreground = SystemLevelBar.Value > 0.94 ? FindBrush("AmberBrush") : FindBrush("MeterBrush");
    }

    private void SetState(RecordingState state, string status)
    {
        _state = state;
        StatusText.Text = status;
        bool idle = state is RecordingState.Idle or RecordingState.Error;
        bool recording = state == RecordingState.Recording;
        bool finalizing = state == RecordingState.Finalizing;

        RecordBtn.IsEnabled = idle || recording;
        MuteMicBtn.IsEnabled = recording;
        SetupSection.IsEnabled = idle;
        FinalizingPanel.Visibility = finalizing ? Visibility.Visible : Visibility.Collapsed;
        if (!finalizing)
        {
            FinalizingProgress.Value = 0;
            FinalizingText.Text = "PREPARING AUDIO…";
        }

        string label;
        Brush stateBrush;
        Brush pillBrush;
        switch (state)
        {
            case RecordingState.Starting:
                label = "STARTING";
                stateBrush = FindBrush("AmberBrush");
                pillBrush = new SolidColorBrush(Color.FromRgb(48, 40, 24));
                break;
            case RecordingState.Recording:
                label = "RECORDING";
                stateBrush = FindBrush("SignalBrush");
                pillBrush = new SolidColorBrush(Color.FromRgb(54, 27, 26));
                break;
            case RecordingState.Finalizing:
                label = "FINALIZING";
                stateBrush = FindBrush("AmberBrush");
                pillBrush = new SolidColorBrush(Color.FromRgb(48, 40, 24));
                break;
            case RecordingState.Error:
                label = "ATTENTION";
                stateBrush = FindBrush("SignalBrush");
                pillBrush = new SolidColorBrush(Color.FromRgb(54, 27, 26));
                break;
            default:
                label = "READY";
                stateBrush = FindBrush("MeterBrush");
                pillBrush = new SolidColorBrush(Color.FromRgb(24, 48, 41));
                break;
        }

        StateText.Text = label;
        StateText.Foreground = stateBrush;
        StateDot.Fill = stateBrush;
        StatePill.Background = pillBrush;

        if (recording)
        {
            RecordBtnText.Text = "STOP";
            RecordGlyph.RadiusX = RecordGlyph.RadiusY = 3;
            RecordGlyph.Width = RecordGlyph.Height = 22;
            RecordBtn.Background = FindBrush("SignalDarkBrush");
            AutomationProperties.SetName(RecordBtn, "Stop recording");
            StateDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.28, 1, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
        }
        else
        {
            RecordBtnText.Text = "RECORD";
            RecordGlyph.RadiusX = RecordGlyph.RadiusY = 12;
            RecordGlyph.Width = RecordGlyph.Height = 24;
            RecordBtn.Background = FindBrush("SignalBrush");
            AutomationProperties.SetName(RecordBtn, "Start recording");
            StateDot.BeginAnimation(OpacityProperty, null);
            StateDot.Opacity = 1;
        }

        if (!recording)
        {
            MuteMicBtn.Content = "MUTE MIC";
            MuteMicBtn.Background = FindBrush("RaisedBrush");
        }
    }

    private async void ReloadRecordings_Click(object sender, RoutedEventArgs e) => await ReloadRecordingsAsync();

    private async Task ReloadRecordingsAsync()
    {
        string folder = OutputFolderBox.Text;
        var items = await Task.Run(() =>
        {
            if (!Directory.Exists(folder))
                return new List<RecordingItem>();

            try
            {
                return Directory.EnumerateFiles(folder, "*.mp3")
                    .Select(path => RecordingItem.FromFile(path))
                    .OrderByDescending(item => item.RecordedAt)
                    .Take(20)
                    .ToList();
            }
            catch
            {
                return new List<RecordingItem>();
            }
        });

        _recordings.Clear();
        foreach (RecordingItem item in items)
            _recordings.Add(item);
        UpdateEmptyRecordingsState();
    }

    private void PlayRecording_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is RecordingItem item && File.Exists(item.FilePath))
            OpenWithShell(item.FilePath);
    }

    private void RevealRecording_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordingItem item)
            return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
    }

    private void CopyRecordingPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordingItem item)
            return;

        try
        {
            Clipboard.SetText(item.FilePath);
            StatusText.Text = "Recording path copied to the clipboard.";
        }
        catch (Exception ex)
        {
            Log($"Clipboard failed: {ex.Message}");
        }
    }

    private void DeleteRecording_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecordingItem item)
            return;

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Permanently delete {Path.GetFileName(item.FilePath)}?",
            "Delete recording",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            File.Delete(item.FilePath);
            _recordings.Remove(item);
            UpdateEmptyRecordingsState();
            StatusText.Text = "Recording deleted.";
            Log($"Deleted: {item.FilePath}");
        }
        catch (Exception ex)
        {
            SetState(RecordingState.Error, $"Couldn’t delete the recording: {ex.Message}");
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        if (e.Key == Key.R)
        {
            e.Handled = true;
            if (_state == RecordingState.Recording) await StopRecordingAsync();
            else if (_state is RecordingState.Idle or RecordingState.Error) StartRecording();
        }
        else if (e.Key == Key.M && _state == RecordingState.Recording)
        {
            e.Handled = true;
            MuteMicBtn_Click(MuteMicBtn, new RoutedEventArgs());
        }
        else if (e.Key == Key.OemComma)
        {
            e.Handled = true;
            FocusSetup();
        }
    }

    private void FocusSetup()
    {
        MicCombo.Focus();
        MicCombo.BringIntoView();
    }

    private void Log(string message)
    {
        _log.Insert(0, $"[{DateTime.Now:HH:mm:ss}]  {message}");
        while (_log.Count > 100)
            _log.RemoveAt(_log.Count - 1);
    }

    private void SaveSettings()
    {
        if (_initializing)
            return;

        _settings.OutputFolder = OutputFolderBox.Text;
        _settings.MicrophoneDeviceId = (MicCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.OutputDeviceId = (OutputCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _settings.MicrophoneGain = MicGainSlider.Value;
        _settings.SystemGain = SystemGainSlider.Value;
        _settings.WindowWidth = RestoreBounds.Width;
        _settings.WindowHeight = RestoreBounds.Height;
        try { _settings.Save(); }
        catch (Exception ex) { Log($"Settings could not be saved: {ex.Message}"); }
    }

    private static void EnsureDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("Choose a recording folder first.");
        Directory.CreateDirectory(path);
    }

    private static void EnsureDiskSpace(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null)
                return;
            var drive = new DriveInfo(root);
            const long minimumFreeBytes = 250L * 1024 * 1024;
            if (drive.IsReady && drive.AvailableFreeSpace < minimumFreeBytes)
                throw new IOException("Less than 250 MB of free disk space remains.");
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
    }

    private static string CreateUniqueOutputPath(string folder, DateTime timestamp)
    {
        string baseName = timestamp.ToString("yyyy-MM-dd_HHmmss") + "_recording";
        string candidate = Path.Combine(folder, baseName + ".mp3");
        int suffix = 2;
        while (File.Exists(candidate) || File.Exists(candidate + ".partial"))
            candidate = Path.Combine(folder, $"{baseName}-{suffix++}.mp3");
        return candidate;
    }

    private static string FormatDecibels(float peak)
    {
        if (peak <= 0.0001f)
            return "−∞";
        return $"{20 * Math.Log10(peak):0}";
    }

    private void UpdateEmptyRecordingsState()
    {
        EmptyRecordingsText.Visibility = _recordings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordingsList.Visibility = _recordings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TrimRecentRecordings()
    {
        while (_recordings.Count > 20)
            _recordings.RemoveAt(_recordings.Count - 1);
    }

    private static void CleanAbandonedSessions()
    {
        try
        {
            if (!Directory.Exists(TempRoot))
                return;
            foreach (string directory in Directory.EnumerateDirectories(TempRoot, "session-*"))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    TryDeleteDirectory(directory);
            }
        }
        catch { }
    }

    private void ClearSessionPaths()
    {
        _sessionDirectory = null;
        _currentMicFile = null;
        _currentSystemFile = null;
        _currentOutputFolder = null;
    }

    private Brush FindBrush(string key) => (Brush)FindResource(key);

    private static void OpenWithShell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void TryDeleteFile(string? path)
    {
        try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is null || !Directory.Exists(path))
                return;
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(TempRoot) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(fullPath, true);
        }
        catch { }
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose)
            return;

        SaveSettings();
        if (_state is not (RecordingState.Recording or RecordingState.Finalizing))
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
        if (_closeInProgress)
            return;

        _closeInProgress = true;
        StatusText.Text = "Finishing the recording before closing…";
        try
        {
            if (_state == RecordingState.Recording)
                await StopRecordingAsync();
            else if (_finalizeTask is not null)
                await _finalizeTask;
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _uiTimer.Stop();
        _recorder.Dispose();
        _finalizeCancellation?.Dispose();
        base.OnClosed(e);
    }
}
