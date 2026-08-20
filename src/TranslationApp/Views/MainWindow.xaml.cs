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
    private bool _isPaused;

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
        _isPaused = false;
        PauseButton.Content = "処理を一時停止";

        try
        {
            _recorder.Start();

            StatusText.Text = "録音中... (セグメント 0 件検出)";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            PauseButton.IsEnabled = true;
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
        PauseButton.IsEnabled = false;
    }

    // 自分の声と相手の声がDiscord等の出力側で既に混ざっている場合、録音した音声だけでは
    // どちらの声か区別できない。そのため自動判定ではなく、自分が話す間だけ手動でこのボタンを
    // 押して文字起こし・翻訳の対象から外せるようにしている。
    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _isPaused = !_isPaused;
        PauseButton.Content = _isPaused ? "処理を再開" : "処理を一時停止";
        StatusText.Text = _isPaused
            ? "録音中... (一時停止中: 文字起こし・翻訳をスキップ)"
            : "録音中...";
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
    // 文字起こし後はファイルを削除するので、保存先フォルダにはエラー時のセグメントだけが残る。
    // 将来的には(検証目的の)ファイル書き出し自体をやめ、segment.Dataを直接渡す形に置き換える想定。
    private async void Segmenter_SegmentReady(AudioSegment segment)
    {
        if (_isPaused)
        {
            // 一時停止中はこの区間を文字起こし・翻訳の対象から完全に除外する。
            return;
        }

        var segmentDir = Path.Combine(_outputDir, "segments");
        Directory.CreateDirectory(segmentDir);
        var segmentPath = Path.Combine(segmentDir, $"segment_{segment.Number:0000}_{DateTime.Now:HHmmss}.wav");

        using (var writer = new WaveFileWriter(segmentPath, segment.Format))
        {
            writer.Write(segment.Data);
        }

        Dispatcher.Invoke(() => StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出、文字起こし中...)");

        // 言語を決め打ちせず、faster-whisperの自動検出結果(sourceText/sourceLanguage)を使う。
        // 英語決め打ちだと、韓国語など英語以外の音声も無理やり英語として認識され精度が落ちるため。
        string sourceText;
        string sourceLanguage = "unknown";
        var transcribed = false;
        try
        {
            if (_whisper is null)
            {
                sourceText = "(faster-whisperが利用できません)";
            }
            else
            {
                var result = await _whisper.TranscribeAsync(segmentPath);
                sourceText = result.Text;
                sourceLanguage = result.Language;
                transcribed = true;
            }
        }
        catch (Exception ex)
        {
            sourceText = $"(文字起こし失敗: {ex.Message})";
        }

        // 文字起こしが終わればWAVファイルの役目は終わりなので削除する(失敗時は原因調査用に残す)。
        // そうしないとセグメントを重ねるたびにファイルが際限なく溜まってしまう。
        if (transcribed)
        {
            try
            {
                File.Delete(segmentPath);
            }
            catch (IOException)
            {
                // 削除できなくても致命的ではないので無視する。
            }
        }

        Dispatcher.Invoke(() => StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出、翻訳中...)");

        string japaneseText;
        try
        {
            japaneseText = await _translator.TranslateToJapaneseAsync(sourceText, sourceLanguage);
        }
        catch (Exception ex)
        {
            japaneseText = $"(翻訳失敗: {ex.Message})";
        }

        Dispatcher.Invoke(() =>
        {
            StatusText.Text = $"録音中... (セグメント {segment.Number} 件検出)";

            var index = TranscriptList.Items.Add($"[{segment.Number:0000}] ({sourceLanguage})\nSRC: {sourceText}\nJP: {japaneseText}");
            TranscriptList.ScrollIntoView(TranscriptList.Items[index]);
        });
    }
}
