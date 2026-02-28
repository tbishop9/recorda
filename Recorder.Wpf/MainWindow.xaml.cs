using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Recorder.Core;

namespace Recorder.Wpf;

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
        OutputFolderBox.Text = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        _timer.Tick += (_, _) =>
            TimerText.Text = (DateTime.Now - _recordingStart).ToString(@"h\:mm\:ss");

        LoadDevices();
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
            OutputFolderBox.Text = dialog.FolderName;
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
        }
        catch (Exception ex)
        {
            _currentMicFile    = null;
            _currentSystemFile = null;
            StatusText.Text    = $"Error: {ex.Message}";
        }
    }

    private void StopRecording()
    {
        _timer.Stop();
        SetUiState(recording: false);
        RecordBtn.IsEnabled = false;
        StatusText.Text     = "Stopping…";

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

        string mergedFile = micFile.Replace("_mic.wav", "_merged.wav");

        try
        {
            WavMerger.Merge(micFile, sysFile, mergedFile,
                _recorder.MicSilencePad, _recorder.SystemSilencePad);

            try { File.Delete(micFile); } catch { }
            try { File.Delete(sysFile); } catch { }

            Dispatcher.Invoke(() =>
            {
                _log.Insert(0, Path.GetFileName(mergedFile));
                TimerText.Text      = "0:00:00";
                StatusText.Text     = "Ready";
                RecordBtn.IsEnabled = true;
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
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
    }

    // ── Log interaction ──────────────────────────────────────────────────

    private void LogList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is not string filename) return;

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
