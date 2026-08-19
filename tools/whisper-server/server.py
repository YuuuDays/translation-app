"""faster-whisperを常駐プロセスとして動かし、標準入出力でC#側とやり取りするサーバー。

毎回モデルを読み込み直すと(特にlarge-v3では)非常に遅いため、起動時に一度だけ
モデルをロードし、以降はstdinに渡されたWAVファイルパスを1行ずつ受け取って
文字起こし結果をJSONで1行ずつstdoutへ返す、という常駐サーバー方式にしている。

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
        wav_path = line.strip()
        if not wav_path:
            continue

        try:
            segments, _info = model.transcribe(wav_path, language="en")
            text = "".join(segment.text for segment in segments).strip()
            print(json.dumps({"text": text}), flush=True)
        except Exception as ex:  # noqa: BLE001 - 呼び出し元(C#)にエラー内容をそのまま伝える
            print(json.dumps({"error": str(ex)}), flush=True)


if __name__ == "__main__":
    main()
