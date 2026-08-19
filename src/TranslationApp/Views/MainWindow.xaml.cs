using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TranslationApp.Audio;
using TranslationApp.Models;

namespace TranslationApp.Views;

public partial class MainWindow : Window
{
    private readonly LoopbackRecorder _recorder = new();
    private readonly SilenceSegmenter _segmenter = new();

    private string _outputDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "TranslationApp", "recordings");

    public MainWindow()
    {
        InitializeComponent();
        OutputDirText.Text = _outputDir;

        _recorder.DataAvailable += Recorder_DataAvailable;
        _recorder.RecordingStopped += Recorder_RecordingStopped;
        _segmenter.SegmentReady += Segmenter_SegmentReady;
        Closed += (_, _) => _recorder.Dispose();
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
        _segmenter.Reset();

        try
        {
            _recorder.Start();

            StatusText.Text = "録音中... (セグメント 0 件検出)";
            FilePathText.Text = "-";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"エラー: {ex.Message}";
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _recorder.Stop();
        StopButton.IsEnabled = false;
    }

    private void Recorder_DataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
        => _segmenter.AddAudio(buffer, _recorder.WaveFormat);

    private void Recorder_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        // 停止した瞬間に発話の途中だった場合、まだ書き出していない分も最後のセグメントとして残す。
        _segmenter.FlushRemaining(_recorder.WaveFormat);

        Dispatcher.Invoke(() =>
        {
            StatusText.Text = e.Exception is null
                ? "停止しました"
                : $"エラーで停止: {e.Exception.Message}";
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        });
    }

    // 現時点では検証のためセグメントをWAVファイルに書き出しているだけだが、将来STTをつなぐ段階では、
    // ここでファイルに書く代わりにsegment.DataをそのままSTTへ渡す形に置き換える想定
    // (ディスクにもメモリにも溜め続けない)。
    private void Segmenter_SegmentReady(AudioSegment segment)
    {
        var segmentDir = Path.Combine(_outputDir, "segments");
        Directory.CreateDirectory(segmentDir);
        var segmentPath = Path.Combine(segmentDir, $"segment_{segment.Number:0000}_{DateTime.Now:HHmmss}.wav");

        using (var writer = new WaveFileWriter(segmentPath, segment.Format))
        {
            writer.Write(segment.Data);
        }

        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出)";
            FilePathText.Text = segmentPath;
        });
    }
}
