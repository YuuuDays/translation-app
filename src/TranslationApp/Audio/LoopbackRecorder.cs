using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace TranslationApp.Audio;

/// <summary>
/// WASAPIループバックでPCの既定の再生デバイス(スピーカー/ヘッドホン出力)の音声をキャプチャする。
/// マイク入力ではなく、PCから出ている音(Discord等の相手の声を含む)を横取りする。
/// </summary>
public sealed class LoopbackRecorder : IDisposable
{
    private WasapiRecorder? _recorder;
    private WaveFormat? _waveFormat;

    /// <summary>録音中の音声フォーマット。停止後もStart()時点の値を保持し続ける。</summary>
    public WaveFormat WaveFormat => _waveFormat ?? throw new InvalidOperationException("録音が開始されていません。");

    public event CaptureDataAvailableHandler? DataAvailable;
    public event EventHandler<StoppedEventArgs>? RecordingStopped;

    public void Start()
    {
        using var enumerator = new MMDeviceEnumerator();
        var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        // WithDevice(renderDevice) : 既定の再生デバイスを対象に指定
        // WithLoopbackCapture()    : 通常の録音ではなく「再生中の音声を横取りする」ループバックモードを有効化
        // Build()                  : 設定を確定し、実際にキャプチャを行うWasapiRecorderを生成
        _recorder = new WasapiRecorderBuilder()
            .WithDevice(renderDevice)
            .WithLoopbackCapture()
            .Build();
        _waveFormat = _recorder.WaveFormat;

        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
        _recorder.StartRecording();
    }

    public void Stop() => _recorder?.StopRecording();

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        => DataAvailable?.Invoke(buffer, flags, devicePosition, qpcPosition);

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        _recorder?.Dispose();
        _recorder = null;
        RecordingStopped?.Invoke(this, e);
    }

    public void Dispose()
    {
        _recorder?.Dispose();
        _recorder = null;
    }
}
