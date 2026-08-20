namespace TranslationApp.Models;

/// <summary>faster-whisperによる文字起こし結果と、自動検出された音声の言語(ISO 639-1、例: "en","ko","ja")。</summary>
public sealed record TranscriptionResult(string Text, string Language);
