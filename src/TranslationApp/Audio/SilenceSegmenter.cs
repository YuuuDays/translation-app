using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using TranslationApp.Models;

namespace TranslationApp.Audio;

/// <summary>
/// 音量ベースの簡易VAD(発話区間検出)。連続する音声チャンクを無音の長さで発話単位に区切り、
/// 区切りが来るたびにSegmentReadyでその区間だけを渡してバッファを空にする。
/// これにより、常時起動でもメモリ使用量は「直近1発話分(最大でもMaxSegmentMs分)」で頭打ちになる。
/// </summary>
public sealed class SilenceSegmenter
{
    // 無音とみなす音量のしきい値(RMS、0.0〜1.0)。値を下げるほど小さい音も「発話中」として拾う。
    private const float SilenceRmsThreshold = 0.02f;

    // この長さだけ無音が連続したら、そこまでを1セグメントとして区切る。
    private const int SilenceCutMs = 600;

    // 無音が来なくても、1セグメントがこの長さを超えたら強制的に区切る。
    // これが無いと、しゃべりっぱなしで無音が来ない限りバッファが際限なく伸びてしまう。
    private const int MaxSegmentMs = 15000;

    private readonly MemoryStream _buffer = new();
    private bool _hasSpeech;
    private double _silenceDurationMs;
    private double _segmentDurationMs;
    private int _segmentCount;

    public event Action<AudioSegment>? SegmentReady;

    public void Reset()
    {
        _buffer.SetLength(0);
        _hasSpeech = false;
        _silenceDurationMs = 0;
        _segmentDurationMs = 0;
        _segmentCount = 0;
    }

    public void AddAudio(ReadOnlySpan<byte> chunk, WaveFormat format)
    {
        var chunkDurationMs = chunk.Length / (double)format.AverageBytesPerSecond * 1000.0;
        var isSilent = ComputeRms(chunk, format) < SilenceRmsThreshold;

        if (!isSilent)
        {
            _hasSpeech = true;
            _silenceDurationMs = 0;
        }
        else if (_hasSpeech)
        {
            _silenceDurationMs += chunkDurationMs;
        }

        // 発話が始まる前の無音はバッファに入れず捨てる(区切り待ちの間にメモリを使わないため)。
        if (!_hasSpeech)
        {
            return;
        }

        _buffer.Write(chunk);
        _segmentDurationMs += chunkDurationMs;

        if (_silenceDurationMs >= SilenceCutMs || _segmentDurationMs >= MaxSegmentMs)
        {
            Flush(format);
        }
    }

    /// <summary>録音停止時、区切りが来ないまま残っている分を最後のセグメントとして書き出す。</summary>
    public void FlushRemaining(WaveFormat format)
    {
        if (_hasSpeech && _buffer.Length > 0)
        {
            Flush(format);
        }
    }

    private void Flush(WaveFormat format)
    {
        _segmentCount++;
        SegmentReady?.Invoke(new AudioSegment(_segmentCount, _buffer.ToArray(), format));

        _buffer.SetLength(0);
        _hasSpeech = false;
        _silenceDurationMs = 0;
        _segmentDurationMs = 0;
    }

    // WASAPIの共有モードは通常32bit float(IEEE Float)、まれに16bit PCMで音声データが渡ってくるため、
    // どちらの形式でも音量(RMS: 二乗平均平方根)を0.0〜1.0の範囲で計算できるようにしている。
    private static float ComputeRms(ReadOnlySpan<byte> buffer, WaveFormat format)
    {
        if (buffer.Length == 0)
        {
            return 0f;
        }

        double sumOfSquares = 0;
        int sampleCount;

        if (format.Encoding == WaveFormatEncoding.Pcm && format.BitsPerSample == 16)
        {
            var samples = MemoryMarshal.Cast<byte, short>(buffer);
            foreach (var sample in samples)
            {
                var normalized = sample / 32768.0;
                sumOfSquares += normalized * normalized;
            }
            sampleCount = samples.Length;
        }
        else
        {
            // IEEE Float(32bit)を既定として扱う。
            var samples = MemoryMarshal.Cast<byte, float>(buffer);
            foreach (var sample in samples)
            {
                sumOfSquares += (double)sample * sample;
            }
            sampleCount = samples.Length;
        }

        return sampleCount == 0 ? 0f : (float)Math.Sqrt(sumOfSquares / sampleCount);
    }
}
