using System.IO;
using System.Windows;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using TranslationApp.Audio;
using TranslationApp.Models;
using TranslationApp.Stt;
using TranslationApp.Translation;

namespace TranslationApp.Views;

public partial class MainWindow : Window
{
    private readonly LoopbackRecorder _recorder = new();
    private readonly SilenceSegmenter _segmenter = new();
    private readonly OllamaTranslator _translator = new();
    private FasterWhisperClient? _whisper;

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
        Closed += async (_, _) =>
        {
            _recorder.Dispose();
            _translator.Dispose();
            if (_whisper is not null)
            {
                await _whisper.DisposeAsync();
            }
        };

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // faster-whisperはモデル読み込みに数秒〜十数秒かかるため、常駐プロセスとして
            // 起動しておき、録音セグメントが来るたびに使い回す(セグメントごとに立ち上げ直さない)。
            _whisper = await FasterWhisperClient.StartAsync(modelSize: "small", device: "cpu", computeType: "int8");
            StatusText.Text = "待機中";
            StartButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"faster-whisperの起動に失敗しました: {ex.Message}";
        }
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
        TranscriptList.Items.Clear();

        try
        {
            _recorder.Start();

            StatusText.Text = "録音中... (セグメント 0 件検出)";
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

    // セグメントが確定するたびに、WAVファイルとして書き出してからfaster-whisperに渡して文字起こしする。
    // 将来的には(検証目的の)ファイル書き出しをやめ、segment.Dataを直接渡す形に置き換える想定。
    private async void Segmenter_SegmentReady(AudioSegment segment)
    {
        var segmentDir = Path.Combine(_outputDir, "segments");
        Directory.CreateDirectory(segmentDir);
        var segmentPath = Path.Combine(segmentDir, $"segment_{segment.Number:0000}_{DateTime.Now:HHmmss}.wav");

        using (var writer = new WaveFileWriter(segmentPath, segment.Format))
        {
            writer.Write(segment.Data);
        }

        Dispatcher.Invoke(() => StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出、文字起こし中...)");

        string englishText;
        try
        {
            englishText = _whisper is null
                ? "(faster-whisperが利用できません)"
                : await _whisper.TranscribeAsync(segmentPath);
        }
        catch (Exception ex)
        {
            englishText = $"(文字起こし失敗: {ex.Message})";
        }

        Dispatcher.Invoke(() => StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出、翻訳中...)");

        string japaneseText;
        try
        {
            japaneseText = await _translator.TranslateToJapaneseAsync(englishText);
        }
        catch (Exception ex)
        {
            japaneseText = $"(翻訳失敗: {ex.Message})";
        }

        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出)";
            TranscriptList.Items.Add($"[{segment.Number:0000}] EN: {englishText}");
            TranscriptList.Items.Add($"       JA: {japaneseText}");
        });
    }
}
