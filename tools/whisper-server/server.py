"""faster-whisperを常駐プロセスとして動かし、標準入出力でC#側とやり取りするサーバー。

毎回モデルを読み込み直すと(特にlarge-v3では)非常に遅いため、起動時に一度だけ
モデルをロードし、以降はstdinに渡されたリクエストを1行(JSON)ずつ受け取って
文字起こし結果をJSONで1行ずつstdoutへ返す、という常駐サーバー方式にしている。

リクエスト例: {"path": "C:\\...\\segment.wav", "language": "ko"}
  languageは省略可(null/空文字なら自動言語検出)。ISO 639-1コード(例: "en","ko","ja")を
  指定すると、その言語だと決め打ちして認識する(短いセグメントで自動検出が不安定な場合に有効)。

使い方: python server.py [model_size] [device] [compute_type]
  例:   python server.py small cpu int8
        python server.py large-v3 cuda float16
"""

import json
import sys

from faster_whisper import WhisperModel


def main() -> None:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stdin.reconfigure(encoding="utf-8")

    model_size = sys.argv[1] if len(sys.argv) > 1 else "small"
    device = sys.argv[2] if len(sys.argv) > 2 else "cpu"
    compute_type = sys.argv[3] if len(sys.argv) > 3 else "int8"

    model = WhisperModel(model_size, device=device, compute_type=compute_type)

    # C#側はこの1行を見てモデルの読み込み完了(=リクエストを送ってよい)を判断する。
    print("READY", flush=True)

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        try:
            request = json.loads(line)
            wav_path = request["path"]
            # language指定なし(null/空文字) = 自動言語検出。英語決め打ちのままだと、
            # 英語以外(韓国語など)の音声も無理やり英語として認識しようとして精度が
            # 大きく落ちるため、既定は自動検出にしつつ、必要なときだけ決め打ちできるようにしている。
            language = request.get("language") or None

            segments, info = model.transcribe(wav_path, language=language)
            text = "".join(segment.text for segment in segments).strip()
            print(json.dumps({"text": text, "language": info.language}), flush=True)
        except Exception as ex:  # noqa: BLE001 - 呼び出し元(C#)にエラー内容をそのまま伝える
            print(json.dumps({"error": str(ex)}), flush=True)


if __name__ == "__main__":
    main()
