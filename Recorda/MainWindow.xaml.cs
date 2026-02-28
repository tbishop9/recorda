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

    private void StartRecording()
    {
        string folder = OutputFolderBox.Text;
        if (!Directory.Exists(folder))
        {
            StatusText.Text = "Output folder not found.";
            return;
        }

        string ts = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        _currentMicFile    = Path.Combine(folder, $"{ts}_mic.wav");
        _currentSystemFile = Path.Combine(folder, $"{ts}_system.wav");

        string? micId    = (MicCombo.SelectedItem    as AudioDeviceInfo)?.Id;
        string? outputId = (OutputCombo.SelectedItem as AudioDeviceInfo)?.Id;

        try
        {
            _recorder.Start(_currentMicFile, _currentSystemFile, micId, outputId);
            _recordingStart = DateTime.Now;
            _timer.Start();
            SetUiState(recording: true);
            Log($"Recording started  →  {folder}");
        }
        catch (Exception ex)
        {
            _currentMicFile    = null;
            _currentSystemFile = null;
            StatusText.Text    = $"Error: {ex.Message}";
            Log($"Error starting recording: {ex.Message}");
        }
    }

    private void StopRecording()
    {
        _timer.Stop();
        SetUiState(recording: false);
        RecordBtn.IsEnabled = false;
        StatusText.Text     = "Stopping…";
        Log("Recording stopped — merging audio…");

        string? micFile = _currentMicFile;
        string? sysFile = _currentSystemFile;
        _currentMicFile    = null;
        _currentSystemFile = null;

        Task.Run(() => FinishRecording(micFile, sysFile));
    }

    private void FinishRecording(string? micFile, string? sysFile)
    {
        _recorder.Stop();

        if (micFile is null || sysFile is null)
        {
            Dispatcher.Invoke(() => { RecordBtn.IsEnabled = true; StatusText.Text = "Ready"; });
            return;
        }

        string mergedFile = micFile.Replace("_mic.wav", "_recording.mp3");

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
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                Log($"Merge failed: {ex.Message}");
                StatusText.Text     = $"Merge failed: {ex.Message}";
                RecordBtn.IsEnabled = true;
            });
        }
    }

    // ── UI state helpers ─────────────────────────────────────────────────

    private void SetUiState(bool recording)
    {
        RecordBtn.Background  = (Brush)FindResource(recording ? "AccentRed" : "AccentBlue");
        RecordBtnText.Text    = recording ? "STOP" : "REC";
        StatusText.Text       = recording ? "Recording…" : "Ready";
        MicCombo.IsEnabled    = !recording;
        OutputCombo.IsEnabled = !recording;
        RefreshBtn.IsEnabled  = !recording;
        BrowseBtn.IsEnabled   = !recording;
        OpenFolderBtn.IsEnabled = !recording;
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
