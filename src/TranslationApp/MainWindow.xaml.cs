using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TranslationApp;

public partial class MainWindow : Window
{
    // 無音とみなす音量のしきい値(RMS、0.0〜1.0)。値を下げるほど小さい音も「発話中」として拾う。
    private const float SilenceRmsThreshold = 0.02f;

    // この長さだけ無音が連続したら、そこまでを1セグメントとして区切る。
    private const int SilenceCutMs = 600;

    // 無音が来なくても、1セグメントがこの長さを超えたら強制的に区切る。
    // これが無いと、しゃべりっぱなしで無音が来ない限りバッファが際限なく伸びてしまう。
    private const int MaxSegmentMs = 15000;

    private WasapiRecorder? _recorder;

    // 現在の発話セグメントの音声データをためておくバッファ。
    // 区切りが来るたびに書き出して空にするので、ためておく量は最大でも
    // 「MaxSegmentMs分の音声」で頭打ちになり、常時起動でも無限には増えない。
    private readonly MemoryStream _segmentBuffer = new();
    private bool _hasSpeechInSegment;
    private double _silenceDurationMs;
    private double _segmentDurationMs;
    private int _segmentCount;

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
        ResetSegmentState();
        _segmentCount = 0;

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

            // StartRecording()後、音声デバイスのバッファが一定量たまるたびにOS側から
            // DataAvailableイベントが発火し、そのつどPCM音声データの断片(buffer)が渡ってくる。
            // ここでは全部を1ファイルに書き続けるのではなく、Recorder_DataAvailable内のVAD
            // (無音検出)で発話単位に区切り、区切りごとに別々のセグメントとして書き出す。
            _recorder.DataAvailable += Recorder_DataAvailable;
            _recorder.RecordingStopped += Recorder_RecordingStopped;
            _recorder.StartRecording();

            StatusText.Text = "録音中... (セグメント 0 件検出)";
            FilePathText.Text = "-";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
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
        var chunkDurationMs = buffer.Length / (double)_recorder!.WaveFormat.AverageBytesPerSecond * 1000.0;
        var isSilent = ComputeRms(buffer, _recorder.WaveFormat) < SilenceRmsThreshold;

        if (!isSilent)
        {
            _hasSpeechInSegment = true;
            _silenceDurationMs = 0;
        }
        else if (_hasSpeechInSegment)
        {
            _silenceDurationMs += chunkDurationMs;
        }

        // 発話が始まる前の無音はバッファに入れず捨てる(区切り待ちの間にメモリを使わないため)。
        if (!_hasSpeechInSegment)
        {
            return;
        }

        _segmentBuffer.Write(buffer);
        _segmentDurationMs += chunkDurationMs;

        if (_silenceDurationMs >= SilenceCutMs || _segmentDurationMs >= MaxSegmentMs)
        {
            FlushSegment();
        }
    }

    private void Recorder_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        // 停止した瞬間に発話の途中だった場合、まだ書き出していない分も最後のセグメントとして残す。
        if (_hasSpeechInSegment && _segmentBuffer.Length > 0)
        {
            FlushSegment();
        }

        _recorder?.Dispose();
        _recorder = null;

        var segmentCount = _segmentCount;
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = e.Exception is null
                ? $"停止しました(セグメント {segmentCount} 件検出)"
                : $"エラーで停止: {e.Exception.Message}";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        });
    }

    // 現時点のセグメントを1つのWAVファイルとして書き出す。
    // 将来STTをつなぐ段階では、ここでファイルに書く代わりに音声データをそのままSTTへ渡し、
    // 渡し終わったらバッファを捨てる形に置き換える想定(ディスクにもメモリにも溜め続けない)。
    private void FlushSegment()
    {
        _segmentCount++;

        var segmentDir = Path.Combine(_outputDir, "segments");
        Directory.CreateDirectory(segmentDir);
        var segmentPath = Path.Combine(segmentDir, $"segment_{_segmentCount:0000}_{DateTime.Now:HHmmss}.wav");

        using (var segmentWriter = new WaveFileWriter(segmentPath, _recorder!.WaveFormat))
        {
            segmentWriter.Write(_segmentBuffer.GetBuffer().AsSpan(0, (int)_segmentBuffer.Length));
        }

        var count = _segmentCount;
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"録音中... (セグメント {count} 件検出)";
            FilePathText.Text = segmentPath;
        });

        ResetSegmentState();
    }

    private void ResetSegmentState()
    {
        _segmentBuffer.SetLength(0);
        _hasSpeechInSegment = false;
        _silenceDurationMs = 0;
        _segmentDurationMs = 0;
    }

    // WASAPIの共有モードは通常32bit float(IEEE Float)、まれに16bit PCMで音声データが渡ってくるため、
    // どちらの形式でも音量(RMS: 二乗平均平方根)を0.0〜1.0の範囲で計算できるようにしている。
    private static float ComputeRms(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (buffer.Length == 0)
        {
            return 0f;
        }

        double sumOfSquares = 0;
        int sampleCount;

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            foreach (var sample in samples)
            {
                var normalized = sample / 32768.0;
                sumOfSquares += normalized * normalized;
            }
            sampleCount = samples.Length;
        }
        else
        {
            // IEEE Float(32bit)を既定として扱う。
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            foreach (var sample in samples)
            {
                sumOfSquares += (double)sample * sample;
            }
            sampleCount = samples.Length;
        }

        return sampleCount == 0 ? 0f : (float)Math.Sqrt(sumOfSquares / sampleCount);
    }
}
