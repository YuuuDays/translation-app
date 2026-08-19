using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TranslationApp.Translation;

/// <summary>
/// ローカルで動くOllama(既定ではhttp://localhost:11434)にHTTPでプロンプトを投げ、
/// 英語のテキストを日本語に翻訳する。クラウドAPIを使わないため無料・オフラインで動く。
/// </summary>
public sealed class OllamaTranslator : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _model;

    public OllamaTranslator(string model = "qwen2.5:7b", string endpoint = "http://localhost:11434")
    {
        _model = model;
        _httpClient = new HttpClient { BaseAddress = new Uri(endpoint) };
    }

    public async Task<string> TranslateToJapaneseAsync(string englishText)
    {
        if (string.IsNullOrWhiteSpace(englishText))
        {
            return string.Empty;
        }

        // 出力を「翻訳文だけ」に絞るため、説明や前置きを付けないようプロンプトで明示的に指示する。
        var prompt =
            "Translate the following English text into natural, fluent Japanese. " +
            "Output only the Japanese translation, with no explanation and no quotation marks.\n\n" +
            $"English: {englishText}\nJapanese:";

        var requestJson = JsonSerializer.Serialize(new OllamaGenerateRequest(_model, prompt, Stream: false));
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
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record OllamaGenerateResponse(
        [property: JsonPropertyName("response")] string? Response);
}
