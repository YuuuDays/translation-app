using System.IO;

namespace TranslationApp.Stt;

/// <summary>
/// リポジトリルート(TranslationApp.slnxのある場所)を起点に、tools/配下の補助スクリプトを探す。
/// 開発中はdotnet runの実行場所(binフォルダ配下)から見た相対位置がまちまちになるため、
/// ソリューションファイルを目印に親ディレクトリを遡って探索する。
/// </summary>
internal static class ScriptLocator
{
    public static string FindWhisperServerScript()
    {
        var repoRoot = FindRepoRoot();
        var scriptPath = Path.Combine(repoRoot, "tools", "whisper-server", "server.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"faster-whisperサーバーのスクリプトが見つかりません: {scriptPath}");
        }

        return scriptPath;
    }

    public static string FindWhisperServerPython()
    {
        var repoRoot = FindRepoRoot();
        var venvPython = Path.Combine(repoRoot, "tools", "whisper-server", ".venv", "Scripts", "python.exe");
        return File.Exists(venvPython) ? venvPython : "python";
    }

    /// <summary>
    /// GPU推論(device: "cuda")に必要なcuBLAS/cuDNNのDLLは、pip(nvidia-cublas-cu12/nvidia-cudnn-cu12)で
    /// venv内にインストールされるだけでシステムのPATHには通っていないため、そのbinフォルダを探して返す。
    /// 見つからない場合(未インストール/CPU運用)は空を返す。
    /// </summary>
    public static IEnumerable<string> FindCudaLibraryDirs()
    {
        var repoRoot = FindRepoRoot();
        var sitePackages = Path.Combine(repoRoot, "tools", "whisper-server", ".venv", "Lib", "site-packages", "nvidia");

        foreach (var subDir in new[] { "cublas", "cudnn" })
        {
            var binDir = Path.Combine(sitePackages, subDir, "bin");
            if (Directory.Exists(binDir))
            {
                yield return binDir;
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TranslationApp.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new DirectoryNotFoundException("リポジトリルート(TranslationApp.slnx)が見つかりませんでした。");
        }

        return dir.FullName;
    }
}
