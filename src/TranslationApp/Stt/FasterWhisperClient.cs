using System.Diagnostics;
using System.Text.Json;

namespace TranslationApp.Stt;

/// <summary>
/// faster-whisperをPythonの常駐プロセスとして起動し、WAVファイルパスを渡して文字起こし結果を受け取る。
/// モデルの読み込みは起動時の1回だけで、以降はプロセスを常駐させたまま使い回す
/// (セグメントのたびにプロセスを立ち上げ直すとモデル読み込みで毎回数秒〜数十秒かかるため)。
/// </summary>
public sealed class FasterWhisperClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private FasterWhisperClient(Process process)
    {
        _process = process;
    }

    public static async Task<FasterWhisperClient> StartAsync(
        string modelSize = "small",
        string device = "cpu",
        string computeType = "int8")
    {
        var pythonPath = ScriptLocator.FindWhisperServerPython();
        var scriptPath = ScriptLocator.FindWhisperServerScript();

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(modelSize);
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add(computeType);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("faster-whisperサーバープロセスを起動できませんでした。");

        // モデルの読み込みが終わるとサーバー側が"READY"を1行出力する。それまでは待つ。
        var readyLine = await process.StandardOutput.ReadLineAsync();
        if (readyLine != "READY")
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"faster-whisperサーバーの起動に失敗しました。{stderr}");
        }

        return new FasterWhisperClient(process);
    }

    public async Task<string> TranscribeAsync(string wavFilePath)
    {
        // Pythonプロセスは1リクエストずつ順番に処理する常駐サーバーなので、
        // 複数セグメントが同時に来ても呼び出しをここで直列化する。
        await _requestLock.WaitAsync();
        try
        {
            await _process.StandardInput.WriteLineAsync(wavFilePath);
            await _process.StandardInput.FlushAsync();

            var resultLine = await _process.StandardOutput.ReadLineAsync()
                ?? throw new InvalidOperationException("faster-whisperサーバーからの応答がありませんでした(プロセスが終了した可能性があります)。");

            using var document = JsonDocument.Parse(resultLine);
            if (document.RootElement.TryGetProperty("error", out var errorProperty))
            {
                throw new InvalidOperationException($"文字起こしに失敗しました: {errorProperty.GetString()}");
            }

            return document.RootElement.GetProperty("text").GetString() ?? string.Empty;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            await _process.WaitForExitAsync();
        }
        catch
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }
}
