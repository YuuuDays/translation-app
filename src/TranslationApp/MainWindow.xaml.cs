using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TranslationApp;

public partial class MainWindow : Window
{
    private WasapiRecorder? _recorder;
    private WaveFileWriter? _writer;
    private string _outputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "TranslationApp", "recordings");

    public MainWindow()
    {
        InitializeComponent();
        OutputDirText.Text = _outputDir;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "録音の保存先フォルダを選択",
            InitialDirectory = Directory.Exists(_outputDir) ? _outputDir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog() == true)
        {
            _outputDir = dialog.FolderName;
            OutputDirText.Text = _outputDir;
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_outputDir);
        var filePath = Path.Combine(_outputDir, $"loopback_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        try
        {
            // WASAPI(Windows Audio Session API)の「ループバック」モードは、本来マイクなどの
            // 入力デバイス用であるWASAPIキャプチャの仕組みを流用し、指定した再生(レンダー)デバイスに
            // 今まさに送られている音声データを、あたかも録音デバイスの入力であるかのように横取りする機能。
            // ここでは「既定の再生デバイス」(スピーカー/ヘッドホン出力)を対象にすることで、
            // マイクではなくPCから出ている音(Discord等の相手の声を含む)を丸ごと拾う。
            using var enumerator = new MMDeviceEnumerator();
            var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            // WithDevice(renderDevice) : 上で取得した再生デバイスを対象に指定
            // WithLoopbackCapture()    : 通常の録音ではなく「再生中の音声を横取りする」ループバックモードを有効化
            // Build()                  : 設定を確定し、実際にキャプチャを行うWasapiRecorderを生成
            _recorder = new WasapiRecorderBuilder()
                .WithDevice(renderDevice)
                .WithLoopbackCapture()
                .Build();
            _writer = new WaveFileWriter(filePath, _recorder.WaveFormat);

            // StartRecording()後、音声デバイスのバッファが一定量たまるたびにOS側から
            // DataAvailableイベントが発火し、そのつどPCM音声データの断片(buffer)が渡ってくる。
            // これをそのままWAVファイルに追記していくことで、途切れなく録音できる。
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
