# リアルタイム音声文字起こし＋翻訳アプリ — プロジェクトメモ

## プロジェクト概要

Windowsデスクトップアプリ。PCのヘッドホン/スピーカー出力音声(Discord等の相手の声)を対象に、リアルタイムで文字起こし＋翻訳を行う。

- 言語: C# + WPF
- 音声キャプチャ: NAudio (WASAPI Loopback)
- STT: Azure Speech SDK(クラウド) / faster-whisper large-v3(ローカル) を切り替え可能に
- 翻訳: DeepL API(クラウド) / ローカルLLM(Ollama等) を切り替え可能に
- 設計方針: `ISttProvider` / `ITranslator` でプロバイダーを抽象化
- 翻訳対応言語: 現在は英語→日本語。将来は韓国語→日本語、日本語→英語にも対応予定
- 優先事項: 精度重視、なるべく無料で運用、クラウド/ローカルを状況に応じて切り替え

---

## このアプリがやっていること(かみ砕いた説明)

同時通訳者が隣にいて、Discordで相手が英語で喋るたびに「今こう言いました」と即座に日本語で耳打ちしてくれる、そのソフト版。これは3つの作業に分解できる。

1. **耳で聞く**(音を拾う)
2. **聞いた言葉を文字に起こす**(何を言ったか理解する)
3. **その文字を日本語に訳して伝える**(翻訳して出力する)

---

## 全体パイプライン(5段階)

```
音声キャプチャ → セグメント化(VAD) → STT(ISttProvider) → 翻訳(ITranslator) → UI表示
```

### 工程1: 音声キャプチャ

PCのスピーカー出力そのものを横取りする必要がある(マイクではない)。
**WASAPI Loopback** = Windowsの音声出力を録音のように取り込む機能。NAudioで実装。

### 工程2: セグメント化(発話区切り)

同時通訳者は一文の区切りがついたところで初めて訳し始める。これをコンピューターにやらせるのが「セグメント化」。
**VAD (Voice Activity Detection、発話区間検出)** = 無音区間(息継ぎ)を検出して「ここで一文終わり」と判断する仕組み。

- 区切りをしっかり待ってから訳す → 精度は高いが表示が遅れる
- 聞こえた分から仮訳を出す → 速いが後で言い直されると訳が変わる
- → **精度とリアルタイム性のトレードオフ**。精度重視の方針なら、区切りを待つ方向に倒すのが妥当。

### 工程3: STT(音声認識)

音声を英語の文字列に変換する。

| 方式 | 特徴 |
|---|---|
| Azure Speech(クラウド) | 精度非常に高い、低遅延。無料枠超えで課金、ネット接続必須 |
| faster-whisper(ローカル) | 無料で使い放題、ネット不要。large-v3は**そこそこ強いGPU**が必要。非力なPCだと遅延 |

### 工程4: 翻訳

| 方式 | 特徴 |
|---|---|
| DeepL API(クラウド) | 翻訳品質非常に高い。無料枠あり(月50万文字程度)、超えると課金 |
| ローカルLLM(Ollama) | 無料。DeepLほどの翻訳品質は出にくい場合がある |

### 工程5: UI表示

WPFで字幕のようにオーバーレイ表示。技術的難易度は他工程より低い。

---

## このプロジェクトの本当の難所

難しいのは工程1・5(キャプチャ・UI)ではなく、**工程2〜4の「つなぎ方」**。

- 音声はずっと流れ続けるのに、翻訳は「区切りがついた文」単位でしか処理できない → どこで区切るかの設計
- クラウド版とローカル版で処理の性質(即座に結果が返るか、まとめて返るか)が違う → 同じ「部品」として差し替えられる形にどう設計するか
- 全部をひとつのスレッドで順番にやると、次の音声が来ても処理待ちで取りこぼす → 並行処理が必要

本質的な難しさは「AIの精度」ではなく、**途切れなく流れてくる音声を、遅延と精度のバランスを取りながら、後で部品を差し替えられる形でパイプライン化すること**。

---

## 実装前に固めるべき設計判断ポイント

| 論点 | 内容 | なぜ重要か |
|---|---|---|
| ①セグメント化戦略 | 無音区間でVAD分割するか、Azureのストリーミング認識(部分結果+確定イベント)に任せるか | faster-whisperは自前でチャンク分割が必要。Azureは内蔵の発話区切り機能がある。ISttProviderの入力形式が変わる |
| ②ストリーミング vs バッチ | 部分結果(仮訳)→確定結果を出すか、確定した文だけ翻訳するか | 精度重視なら確定後翻訳が安全。低遅延重視なら部分結果も逐次翻訳 |
| ③スレッド/パイプライン設計 | キャプチャ→STT→翻訳を別スレッドで並列化し、`Channel<T>`でバックプレッシャー制御するか | WASAPIコールバックスレッドで重い処理を行うと音声ドロップが起きる |
| ④GPU/リソース要件 | faster-whisper large-v3をリアルタイムで回すにはGPU(VRAM 8GB+目安)が必要 | ローカル運用の実現性に直結 |
| ⑤コスト管理 | Azure Speech無料枠(月5時間程度)、DeepL無料枠(月50万文字)の消費ペース監視 | 「なるべく無料で運用」の要件に対し使用量トラッキングが必要か |
| ⑥設定切替の粒度 | クラウド/ローカルをアプリ起動時固定にするか、実行中にホットスワップ可能にするか | DIコンテナでの登録方法(Singleton vs Factory)が変わる |

---

## 大まかな進め方(ロードマップ)

1. **音声が拾えることを確認する** — NAudioでWASAPIループバックを試し、ファイルに保存できるところまで作る
2. **文の区切り方を決めて試す** — 無音検出(VAD)で発話単位に分割する簡単な仕組みを作る
3. **STT(音声認識)をまず1種類だけ動かす** — Azure Speechかfaster-whisperのどちらか片方だけを繋いで、英語の文字起こしが画面に出るところまで作る
4. **翻訳をつなげる** — 文字起こし結果をDeepL APIに投げて日本語訳を得る。ここで「聞く→文字にする→訳す」の一本の流れが完成
5. **インターフェースで抽象化する** — 動くものができてから初めて `ISttProvider` / `ITranslator` の形に整理する
6. **もう片方の方式(ローカル/クラウド)を追加する** — 動作確認済みのインターフェースに2つ目の実装を追加し、設定で切り替え可能に
7. **パイプラインを並行処理化して安定させる** — スレッド分離やキュー(Channel)で途切れなく処理できる形に整える

**ポイント**: 最初から「クラウド/ローカル両対応」「完璧な抽象化」を目指さない。まず片方の方式だけで一本の流れを動かしてから、後で部品化・複線化する。

---

## 起動方法

初回のみ、faster-whisperサーバー用のPython仮想環境を作る:
```
python -m venv tools/whisper-server/.venv
tools/whisper-server/.venv/Scripts/pip install -r tools/whisper-server/requirements.txt
```
(初回の文字起こし実行時、Hugging Faceからモデルが自動ダウンロードされるためネット接続が必要。2回目以降はローカルにキャッシュされるので無料でオフライン動作する)

初回のみ、翻訳用にOllamaとモデルを用意する(winget等でインストール後):
```
ollama pull gemma2:9b
```
Ollamaはインストールすると既定でバックグラウンドサービスとして常駐し、`http://localhost:11434` でHTTP APIを待ち受ける。アプリ側は起動しているOllamaにHTTPで問い合わせるだけなので、Ollama自体は事前に起動しておく必要がある(通常はサービスとして自動起動する)。

アプリの起動:
```
dotnet run --project src/TranslationApp/TranslationApp.csproj
```

または `TranslationApp.slnx` をVisual Studioで開いて実行(F5)。起動時にfaster-whisperサーバー(Pythonプロセス)を裏で立ち上げてモデルを読み込むため、「録音開始」ボタンは読み込み完了まで無効化される。

---

## 現在の実装状況(ステップ1〜4: 音声キャプチャ + VAD区切り + STT + 翻訳)

**入力**: なし(UI操作のみ)。「録音開始」ボタン押下時点のPC既定の再生デバイス(スピーカー/ヘッドホン出力)の音声を、WASAPIループバックでシステム全体からキャプチャする。マイク入力ではない。保存先フォルダは画面の「変更...」ボタンから選択可能(既定は `ドキュメント\TranslationApp\recordings`)。

**処理**: 録音した音声を1本の長いファイルにはせず、音量ベースの簡易VAD(`SilenceSegmenter`)で無音区間ごとに発話単位に区切る。区切りが来るたびに `保存先フォルダ\segments\segment_0001_HHmmss.wav` として書き出し、そのファイルをfaster-whisper(`FasterWhisperClient`、モデルは既定で`small`・CPU・int8)に渡して文字起こしを行う。**言語は決め打ちせず自動検出**しており(`model.transcribe()`にlanguage引数を渡さない)、検出結果(`TranscriptionResult.Language`、例:"en","ko","ja")と文字起こし結果をOllama(`OllamaTranslator`、既定モデル`gemma2:9b`)に渡して日本語に翻訳する。これで「聞く→文字にする→訳す」の一本の流れが完成した。

**出力**: 画面に録音状態・検出セグメント数、そして各セグメントの検出言語・文字起こし・日本語訳を一覧表示する。UIオーバーレイ表示(字幕のような常時最前面表示)はまだ行っていない。

**言語を英語決め打ちにしていた問題**: 当初`language="en"`を明示していたため、韓国語などの英語以外の音声も無理やり英語として認識され、文字起こし精度が大きく落ちていた(実際に韓国語話者の音声で問題が発覚)。自動検出に変更し、`OllamaTranslator.TranslateToJapaneseAsync`も検出言語をプロンプトに埋め込む形(例:「Korean text」)に変更した。検出言語が`ja`の場合はOllamaを呼ばずそのまま返す。

**faster-whisperの動かし方**: モデルの読み込みに数秒〜十数秒かかるため、毎セグメントごとにプロセスを立ち上げ直すのではなく、アプリ起動時に常駐プロセス(`tools/whisper-server/server.py`)として立ち上げ、標準入出力でWAVファイルパスと結果(JSON)をやり取りする方式にしている。モデルサイズ・デバイス(cpu/cuda)・compute_typeは`MainWindow_Loaded`内の`FasterWhisperClient.StartAsync`呼び出しで指定する。

**GPU(cuda)への切り替えで文字起こしを大幅高速化**: 当初CPU(`small`・int8)で動かしていたが、1セグメントあたり約3.4〜3.8秒かかっており、Ollama翻訳(約0.7〜1秒)より遥かに遅いボトルネックだった。`device: "cuda"`・`compute_type: "float16"`に切り替えたところ、同じセグメントが初回0.94秒、2回目以降0.26秒まで短縮(実測、RTX 4060/VRAM 8GB)。ただしfaster-whisper(ctranslate2)のCUDA推論には`cublas64_12.dll`/`cudnn64_9.dll`が必要で、システムのCUDA Toolkitではなく`nvidia-cublas-cu12`/`nvidia-cudnn-cu12`をpipでvenvに入れる形にした(`requirements.txt`に追加済み)。これらのDLLはvenv内(`site-packages/nvidia/{cublas,cudnn}/bin`)に入るだけでPATHには通らないため、`ScriptLocator.FindCudaLibraryDirs()`で探して`FasterWhisperClient.StartAsync`がPythonプロセス起動時のPATH環境変数に追加している。CPU運用に戻したい場合は`device: "cpu"`・`compute_type: "int8"`に戻せばよい(CUDAパッケージが無くてもCPU動作には影響しない)。

**Ollamaの動かし方**: Ollamaは常駐サービスとして`http://localhost:11434`でHTTP APIを待ち受けているので、`OllamaTranslator`はそこへ`/api/generate`をPOSTするだけ(faster-whisperのようなプロセス管理は不要)。プロンプトで「日本語のみ・中国語厳禁・翻訳文だけを出力する」よう指示し、`temperature`も0.1まで下げて出力のブレを抑えている。

モデルは最初`qwen2.5:7b`を使っていたが、"I know."のような短い定型フレーズで中国語(`我知道。`)が混ざる問題が実測で確認されたため、`gemma2:9b`に切り替えた(同じテストフレーズで中国語混入なし、自然な日本語を確認)。`OllamaTranslator`のコンストラクタ引数でモデルは差し替え可能。

**常時起動でもメモリが増え続けない設計**: 無音が600ms続くか、無音が来なくても1セグメントが15秒を超えたら強制的に区切ってバッファを空にする(`SilenceSegmenter`内の`SilenceCutMs`/`MaxSegmentMs`)。これにより、録音時間がどれだけ長くてもメモリ使用量は「直近1発話分」で頭打ちになる。

**自分の声を処理対象から外す「処理を一時停止」ボタン**: Discord等の通話アプリでは自分の声も相手の声と同じ出力ストリームに混ざって再生されることがあり、ループバック録音の時点では両者を区別する手がかりが無い(自動の話者識別は別途大掛かりな機能が必要)。そのため自動判定ではなく、自分が話す間だけ画面の「処理を一時停止」ボタンを押して手動でON/OFFする方式にしている。一時停止中に検出されたセグメントは`Segmenter_SegmentReady`の先頭で即returnし、ファイル書き出し・文字起こし・翻訳のいずれも行わない(録音自体は止めない)。

まだ実装していないもの: UIオーバーレイ表示、`ISttProvider`/`ITranslator`による抽象化(現時点ではfaster-whisper・Ollama決め打ち)、並行処理化。

---

## フォルダ構成の方針(C#デスクトップアプリのベストプラクティス)

### 基本方針
- リポジトリ直下は「ソリューションファイル・`.gitignore`・`Docs/`・`src/`・(将来)`tests/`」のみに留める
- `src/`配下に実際の`.csproj`プロジェクトを置き、ドキュメントと分離する
- UIとロジックは、たとえ1プロジェクトでも**フォルダ単位で分離**しておく。今すぐ複数プロジェクトに分けなくても、フォルダを切っておけば将来の分割は「ファイル移動」で済む
- 1ファイル1クラス、ファイル名=クラス名、namespaceはフォルダ構造と一致させる

### 現在(単一プロジェクト)の構成
```
translation-app/
├── src/TranslationApp/
│   ├── App.xaml / App.xaml.cs
│   ├── Views/              — 画面(xaml)。MainWindow.xaml/.xaml.csはUIの取りまとめ役に徹する
│   │   ├── MainWindow.xaml
│   │   └── MainWindow.xaml.cs
│   ├── Audio/               — NAudioキャプチャ・VADなどの音声処理ロジック(UIに依存しない)
│   │   ├── LoopbackRecorder.cs   — WASAPIループバック録音のラッパー
│   │   └── SilenceSegmenter.cs   — 無音検出による発話単位への区切り
│   ├── Stt/                 — STT(音声認識)まわり
│   │   ├── FasterWhisperClient.cs — faster-whisperサーバー(Pythonプロセス)とのやり取り
│   │   └── ScriptLocator.cs       — tools/配下のPythonスクリプトの場所を解決
│   ├── Translation/         — 翻訳まわり
│   │   └── OllamaTranslator.cs    — Ollama(常駐サービス)へのHTTPリクエストで英→日翻訳
│   ├── Models/              — データの入れ物
│   │   └── AudioSegment.cs
│   └── TranslationApp.csproj
└── tools/
    └── whisper-server/      — C#プロジェクトの外にあるPython製の補助プロセス
        ├── server.py            — faster-whisperを常駐させ標準入出力でやり取りするサーバー
        ├── requirements.txt
        └── .venv/               — (.gitignore対象、コミットしない)
```
`MainWindow.xaml.cs`に音声処理ロジックを全部書き込むとUIコードと混ざって後で切り出しにくくなるため、`Audio/`のクラスに分離。`MainWindow`はイベントの配線とファイルI/O・UI更新のみを担当する。STTはC#では完結せずPythonプロセス(faster-whisper)に頼る形になるため、`.csproj`配下ではなくリポジトリ直下の`tools/`に置き、C#側からは`Stt/`配下のクラス経由でプロセスとして呼び出す。

### 将来(ロードマップ⑤: インターフェース抽象化後)の構成
動くものができてから初めて `ISttProvider` / `ITranslator` を導入するタイミングで、以下のようにプロジェクトを分割する想定:

```
src/
├── TranslationApp/                    — WPF UI(Viewと起動時のDI登録のみ)
├── TranslationApp.Core/               — ISttProvider/ITranslator、パイプライン、Models(UI/NAudioに非依存)
├── TranslationApp.Stt.Azure/          — Azure Speech実装
├── TranslationApp.Stt.Whisper/        — faster-whisper実装
├── TranslationApp.Translation.DeepL/
└── TranslationApp.Translation.Ollama/
tests/
└── TranslationApp.Core.Tests/         — Core配下ロジックの単体テスト
```
Coreを何にも依存させないことで、UIなしでロジック単体をテストでき、STT/翻訳の実装ごとに依存パッケージ(Azure SDK、Ollamaクライアント等)を分離できる。
