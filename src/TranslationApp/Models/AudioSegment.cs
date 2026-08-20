using NAudio.Wave;

namespace TranslationApp.Models;

/// <summary>無音区間で区切られた1発話分の音声データ。</summary>
public sealed record AudioSegment(int Number, byte[] Data, WaveFormat Format);
