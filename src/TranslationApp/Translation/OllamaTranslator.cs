using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranslationApp.Translation;

/// <summary>
/// ローカルで動くOllama(既定ではhttp://localhost:11434)にHTTPでプロンプトを投げ、
/// 検出された言語のテキストを日本語に翻訳する。クラウドAPIを使わないため無料・オフラインで動く。
/// </summary>
public sealed class OllamaTranslator : IDisposable
{
    // temperatureが既定(0.8)のままだと、特に短い/曖昧な文で出力が安定せず、
    // 中国語が混ざったり無関係な多言語混在になったりすることがあったため、
    // 「創造性より忠実さ」を優先してほぼ決定的な出力になるよう下げている。
    private const double Temperature = 0.1;

    // faster-whisperが返すISO 639-1言語コードを、プロンプトで使う英語の言語名に変換する。
    // 一覧に無い言語コードはそのままプロンプトに埋め込む(モデルが認識できる可能性が高いため)。
    private static readonly Dictionary<string, string> LanguageNames = new()
    {
        ["en"] = "English",
        ["ko"] = "Korean",
        ["zh"] = "Chinese",
        ["es"] = "Spanish",
        ["fr"] = "French",
        ["de"] = "German",
    };

    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OllamaTranslator(string model = "gemma2:9b", string endpoint = "http://localhost:11434")
    {
        _model = model;
        _httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
    }

    /// <summary>sourceLanguageはfaster-whisperが返すISO 639-1言語コード(例: "en","ko")。</summary>
    public async Task<string> TranslateToJapaneseAsync(string sourceText, string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return string.Empty;
        }

        // 既に日本語ならOllamaを呼ぶまでもない。
        if (sourceLanguage == "ja")
        {
            return sourceText;
        }

        var languageName = LanguageNames.GetValueOrDefault(sourceLanguage, sourceLanguage);

        // 出力を「翻訳文だけ」に絞り、かつ中国語への混同(qwen系モデルで時々発生)を防ぐため
        // 言語を名指しで縛っている。
        var prompt =
            $"You are a professional translator specializing in {languageName}-to-Japanese translation.\n" +
            $"Translate the following {languageName} text into natural, fluent Japanese.\n" +
            "Rules:\n" +
            "- Output Japanese only. Never output Chinese characters or any other language.\n" +
            "- Output ONLY the translation itself: no explanations, no romanization, no quotation marks.\n" +
            "- If the input is a short or ambiguous fragment, translate it as naturally as possible without adding meaning that isn't there.\n\n" +
            $"{languageName}: {sourceText}\nJapanese:";

        var request = new OllamaGenerateRequest(_model, prompt, Stream: false, new OllamaOptions(Temperature));
        var requestJson = JsonSerializer.Serialize(request);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        // stream: falseにしているので、Ollamaは生成完了まで待ってから1つのJSONで返す。
        using var response = await _httpClient.PostAsync("/api/generate", content);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<OllamaGenerateResponse>(responseJson);
        return result?.Response?.Trim() ?? string.Empty;
    }

    public void Dispose() => _httpClient.Dispose();

    private sealed record OllamaGenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("options")] OllamaOptions Options);

    private sealed record OllamaOptions(
        [property: JsonPropertyName("temperature")] double Temperature);

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}
