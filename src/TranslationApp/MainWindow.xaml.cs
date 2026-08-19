using System.IO;
using System.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TranslationApp;

public partial class MainWindow : Window
{
    private WasapiRecorder? _recorder;
    private WaveFileWriter? _writer;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var outputDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "TranslationApp", "recordings");
        Directory.CreateDirectory(outputDir);
        var filePath = Path.Combine(outputDir, $"loopback_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            _recorder = new WasapiRecorderBuilder()
                .WithDevice(renderDevice)
                .WithLoopbackCapture()
                .Build();
            _writer = new WaveFileWriter(filePath, _recorder.WaveFormat);

            _recorder.DataAvailable += Recorder_DataAvailable;
            _recorder.RecordingStopped += Recorder_RecordingStopped;
            _recorder.StartRecording();

            FilePathText.Text = filePath;
            StatusText.Text = "録音中...";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
            _writer?.Dispose();
            _writer = null;
            _recorder?.Dispose();
            _recorder = null;
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _recorder?.StopRecording();
        StopButton.IsEnabled = false;
    }

    private void Recorder_DataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        _writer?.Write(buffer);
    }

    private void Recorder_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        _writer?.Dispose();
        _writer = null;
        _recorder?.Dispose();
        _recorder = null;

        Dispatcher.Invoke(() =>
        {
            StatusText.Text = e.Exception is null
                ? "停止しました"
                : $"エラーで停止: {e.Exception.Message}";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        });
    }
}
