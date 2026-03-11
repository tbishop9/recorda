using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Recorder.Core;

namespace Recorda;

public partial class MainWindow : Window
{
    private readonly WasapiDualRecorder _recorder = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _recordingStart;
    private string? _currentMicFile;
    private string? _currentSystemFile;
    private string? _currentOutputFolder;
    private bool _isMicrophoneMuted;

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _log;

        string defaultFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Recorda");
        Directory.CreateDirectory(defaultFolder);
        OutputFolderBox.Text = defaultFolder;

        _timer.Tick += (_, _) =>
            TimerText.Text = (DateTime.Now - _recordingStart).ToString(@"h\:mm\:ss");

        LoadDevices();
        UpdateMuteButton();
        Log("Ready");
    }

    // ── Device loading ───────────────────────────────────────────────────

    private void LoadDevices()
    {
        string? prevMicId    = (MicCombo.SelectedItem    as AudioDeviceInfo)?.Id;
        string? prevOutputId = (OutputCombo.SelectedItem as AudioDeviceInfo)?.Id;

        var mics    = WasapiDualRecorder.GetMicDevices();
        var outputs = WasapiDualRecorder.GetOutputDevices();

        MicCombo.ItemsSource    = mics;
        OutputCombo.ItemsSource = outputs;

        MicCombo.SelectedItem    = mics.FirstOrDefault(d => d.Id == prevMicId)    ?? mics.FirstOrDefault();
        OutputCombo.SelectedItem = outputs.FirstOrDefault(d => d.Id == prevOutputId) ?? outputs.FirstOrDefault();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => LoadDevices();

    // ── Output folder ────────────────────────────────────────────────────

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select output folder",
            InitialDirectory = OutputFolderBox.Text
        };
        if (dialog.ShowDialog(this) == true)
        {
            OutputFolderBox.Text = dialog.FolderName;
            Log($"Output folder changed to: {dialog.FolderName}");
        }
    }

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        string folder = OutputFolderBox.Text;
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", $"\"{folder}\"");
    }

    // ── Recording ────────────────────────────────────────────────────────

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_recorder.IsRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void MuteMicBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_recorder.IsRecording)
            return;

        ApplyMicrophoneMute(!_isMicrophoneMuted);
    }

    private void StartRecording()
    {
        string folder = OutputFolderBox.Text;
        if (!Directory.Exists(folder))
        {
            StatusText.Text = "Output folder not found.";
            return;
        }

        string ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        string tempFolder = Path.GetTempPath();
        _currentMicFile      = Path.Combine(tempFolder, $"{ts}_mic.wav");
        _currentSystemFile   = Path.Combine(tempFolder, $"{ts}_system.wav");
        _currentOutputFolder = folder;

        string? micId    = (MicCombo.SelectedItem    as AudioDeviceInfo)?.Id;
        string? outputId = (OutputCombo.SelectedItem as AudioDeviceInfo)?.Id;

        try
        {
            _recorder.Start(_currentMicFile, _currentSystemFile, micId, outputId);
            _recordingStart = DateTime.Now;
            _timer.Start();
            _isMicrophoneMuted = false;
            SetUiState(recording: true);
            UpdateMuteButton();
            Log($"Recording started  →  {folder}");
        }
        catch (Exception ex)
        {
            _currentMicFile      = null;
            _currentSystemFile   = null;
            _currentOutputFolder = null;
            _isMicrophoneMuted   = false;
            StatusText.Text      = $"Error: {ex.Message}";
            UpdateMuteButton();
            Log($"Error starting recording: {ex.Message}");
        }
    }

    private void StopRecording()
    {
        _timer.Stop();
        _recorder.SetMicrophoneMuted(false);
        _isMicrophoneMuted = false;
        UpdateMuteButton();
        SetUiState(recording: false);
        RecordBtn.IsEnabled = false;
        StatusText.Text     = "Stopping…";
        Log("Recording stopped — merging audio…");

        string? micFile      = _currentMicFile;
        string? sysFile      = _currentSystemFile;
        string? outputFolder = _currentOutputFolder;
        _currentMicFile      = null;
        _currentSystemFile   = null;
        _currentOutputFolder = null;

        Task.Run(() => FinishRecording(micFile, sysFile, outputFolder));
    }

    private void FinishRecording(string? micFile, string? sysFile, string? outputFolder)
    {
        _recorder.Stop();

        if (micFile is null || sysFile is null || outputFolder is null)
        {
            Dispatcher.Invoke(() =>
            {
                RecordBtn.IsEnabled = true;
                StatusText.Text = "Ready";
                UpdateMuteButton();
            });
            return;
        }

        string ts = Path.GetFileName(micFile).Replace("_mic.wav", "");
        string mergedFile = Path.Combine(outputFolder, $"{ts}_recording.mp3");

        try
        {
            WavMerger.Merge(micFile, sysFile, mergedFile,
                _recorder.MicSilencePad, _recorder.SystemSilencePad);

            try { File.Delete(micFile); } catch { }
            try { File.Delete(sysFile); } catch { }

            long micPadMs = (long)(_recorder.MicSilencePad.TotalMilliseconds);
            long sysPadMs = (long)(_recorder.SystemSilencePad.TotalMilliseconds);
            string offsetNote = micPadMs > 0
                ? $"mic offset +{micPadMs} ms"
                : sysPadMs > 0
                    ? $"system offset +{sysPadMs} ms"
                    : "no offset";

            Dispatcher.Invoke(() =>
            {
                Log($"Saved: {Path.GetFileName(mergedFile)}  ({offsetNote})");
                TimerText.Text      = "0:00:00";
                StatusText.Text     = "Ready";
                RecordBtn.IsEnabled = true;
                UpdateMuteButton();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                Log($"Merge failed: {ex.Message}");
                StatusText.Text     = $"Merge failed: {ex.Message}";
                RecordBtn.IsEnabled = true;
                UpdateMuteButton();
            });
        }
    }

    // ── UI state helpers ─────────────────────────────────────────────────

    private void SetUiState(bool recording)
    {
        RecordBtn.Background    = (Brush)FindResource(recording ? "AccentRed" : "AccentBlue");
        RecordBtnText.Text      = recording ? "STOP" : "REC";
        MicCombo.IsEnabled      = !recording;
        OutputCombo.IsEnabled   = !recording;
        RefreshBtn.IsEnabled    = !recording;
        BrowseBtn.IsEnabled     = !recording;
        OpenFolderBtn.IsEnabled = !recording;
        MuteMicBtn.IsEnabled    = recording;
        UpdateStatusText();
        UpdateMuteButton();
    }

    private void ApplyMicrophoneMute(bool muted)
    {
        _recorder.SetMicrophoneMuted(muted);
        _isMicrophoneMuted = muted;
        UpdateStatusText();
        UpdateMuteButton();
        Log(muted ? "Microphone muted." : "Microphone unmuted.");
    }

    private void UpdateStatusText()
    {
        if (!_recorder.IsRecording)
        {
            StatusText.Text = "Ready";
            return;
        }

        StatusText.Text = _isMicrophoneMuted
            ? "Recording… microphone muted"
            : "Recording…";
    }

    private void UpdateMuteButton()
    {
        MuteMicBtn.Content = _isMicrophoneMuted ? "Unmute Mic" : "Mute Mic";
        MuteMicBtn.Background = _isMicrophoneMuted
            ? (Brush)FindResource("AccentRed")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F9FAFB"));
        MuteMicBtn.BorderBrush = _isMicrophoneMuted
            ? (Brush)FindResource("AccentRed")
            : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D1D5DB"));
        MuteMicBtn.Foreground = _isMicrophoneMuted
            ? Brushes.White
            : (Brush)FindResource("TextPrimary");
    }

    // ── Log helpers ──────────────────────────────────────────────────────

    private void Log(string message)
    {
        string entry = $"[{DateTime.Now:HH:mm:ss}]  {message}";
        if (Dispatcher.CheckAccess())
            _log.Insert(0, entry);
        else
            Dispatcher.Invoke(() => _log.Insert(0, entry));
    }

    // ── Log interaction ──────────────────────────────────────────────────

    private void LogList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is not string entry) return;

        // Entries that reference a saved file look like: "[HH:mm:ss]  Saved: filename.mp3 (…)"
        const string savedPrefix = "Saved: ";
        int idx = entry.IndexOf(savedPrefix, StringComparison.Ordinal);
        if (idx < 0) return;

        string rest     = entry[(idx + savedPrefix.Length)..];
        string filename = rest.Contains("  (") ? rest[..rest.IndexOf("  (")] : rest.Trim();

        string fullPath = Path.Combine(OutputFolderBox.Text, filename);
        string args = File.Exists(fullPath)
            ? $"/select,\"{fullPath}\""
            : $"\"{OutputFolderBox.Text}\"";

        Process.Start("explorer.exe", args);
    }

    // ── Lifecycle ────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _timer.Stop();
        _recorder.Dispose();
    }
}
