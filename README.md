# translation-app

> 🤖 本プロジェクトはバイブコーディング(AIとの対話による開発)で作成しています。

Discordなどの通話でPCのスピーカー/ヘッドホンから流れる音声を、リアルタイムで文字起こし＋日本語に翻訳するWindowsデスクトップアプリ。マイクではなく**PCの出力音声**(WASAPIループバック)を対象にする。

- 音声認識(STT): [faster-whisper](https://github.com/SYSTRAN/faster-whisper)(ローカル・無料、言語自動検出)
- 翻訳: [Ollama](https://ollama.com/)によるローカルLLM(既定 `gemma2:9b`、無料・オフライン)
- クラウドAPIを使わないため、課金なし・ネット接続不要(初回のモデルダウンロードを除く)で動く

## 必要なもの

- Windows + [.NET SDK](https://dotnet.microsoft.com/) 10系
- [Python](https://www.python.org/) 3.11以降
- [Ollama](https://ollama.com/download) — 翻訳(gemma2:9b)はGPU前提。VRAM 8GB程度のNVIDIA GPUを推奨
- 文字起こし(faster-whisper)は既定でCPU実行。VRAMに十分余裕がある環境なら`FasterWhisperClient.StartAsync`の呼び出しを`device: "cuda"`・`compute_type: "float16"`に変更するとCPU比10倍以上速くなるが、VRAM 8GB程度だと翻訳用モデルと共存できず不安定になることを確認済み(詳細は[Docs/MEMO.md](Docs/MEMO.md))

## セットアップ(初回のみ)

```powershell
# faster-whisper用のPython仮想環境
python -m venv tools/whisper-server/.venv
tools/whisper-server/.venv/Scripts/pip install -r tools/whisper-server/requirements.txt

# 翻訳用モデルをOllamaで取得
ollama pull gemma2:9b
```

Ollamaはインストールすると常駐サービスとして自動起動し、`http://localhost:11434` で待ち受ける。faster-whisperのモデルは初回の文字起こし実行時にHugging Faceから自動ダウンロードされる(以降はローカルキャッシュを使うのでオフラインで動く)。

## 使い方

```powershell
dotnet run --project src/TranslationApp/TranslationApp.csproj
```

またはVisual Studioで `TranslationApp.slnx` を開いてF5。

1. 起動するとfaster-whisperサーバーが裏で立ち上がる(モデル読み込み完了まで「録音開始」は無効)
2. 「保存先フォルダ」はエラー時のログ用途で任意に変更可能
3. 「音声の言語」は既定で自動検出。話者の言語が分かっている場合(例: 韓国語話者のみ)は決め打ちすると認識精度が上がることがある
4. 「録音開始」でPCの出力音声を拾い始め、無音区間ごとに自動で発話単位に区切って文字起こし・翻訳し、画面に一覧表示する
5. 自分の声が混ざる場合は「処理を一時停止」で自分の発話中だけ処理をスキップできる(手動トグル)
6. 「録音停止」で終了

## 今できること / できないこと

- ✅ PC出力音声のキャプチャ、無音検出による発話区切り、多言語自動検出付きSTT、日本語への翻訳
- ✅ 常時起動してもメモリ・ディスクが増え続けない設計(処理済みセグメントは即破棄)
- ❌ 字幕のような常時最前面オーバーレイ表示(現状はウィンドウ内リスト表示のみ)
- ❌ クラウドAPI(Azure Speech / DeepL)との切り替え(現状はローカル決め打ち)
- ❌ 自分の声の自動除外(話者識別) — 手動トグルのみ

詳しい設計方針・進め方・実装の経緯は [Docs/MEMO.md](Docs/MEMO.md) を参照。
