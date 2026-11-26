using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Ocr.Gemini;
using OMTS.Infrastructure.Ocr.Gemini.Prompts;
using OMTS.Infrastructure.Ocr.Qwen;

namespace OMTS.Infrastructure.UnitTests.Ocr;

/// <summary>Qwen answers go through the same readers and normalisers as Gemini's; the settings are validated at startup.</summary>
public class QwenReadersAndOptionsTests
{
    private static IGeminiVisionClient Qwen(FakeHandler handler)
    {
        var options = new OcrProviderOptions { Provider = OcrProviderOptions.QwenProvider };
        options.Qwen.ApiKey = "sk-test";
        return new QwenVisionClient(
            new HttpClient(handler), Options.Create(options), TimeProvider.System, LoggerFactory.Create(_ => { }).CreateLogger<QwenVisionClient>());
    }

    [Fact]
    public async Task A_meter_answer_from_Qwen_gets_the_same_confidences_as_one_from_Gemini()
    {
        var json = """
            {"ph":{"value":"7,8","uncertain":false},"salinity":{"value":"25.5","uncertain":true},"temperature":null,
             "measured_at":"27/11/2026 08:30","other_numbers":[]}
            """;
        var service = new GeminiMeterOcrService(Qwen(new FakeHandler(_ => Answers.Sse(Answers.Delta("```json\n" + json + "\n```", "stop"), "[DONE]"))));

        var result = await service.RecognizeAsync(new MemoryStream([1, 2, 3]), "image/jpeg", null, CancellationToken.None);

        Assert.Equal(7.8m, result.Ph!.Value);
        Assert.Equal(GeminiMeterOcrService.CertainConfidence, result.Ph.Confidence);
        Assert.Equal(25.5m, result.Salinity!.Value);
        Assert.Equal(GeminiMeterOcrService.UncertainConfidence, result.Salinity.Confidence);
        Assert.Null(result.Temperature);
        Assert.NotNull(result.MeasuredAt);
    }

    [Fact]
    public async Task A_licence_map_answer_from_Qwen_has_the_raw_shape_the_normaliser_expects()
    {
        var json = """
            {"coordinate_system":"VN-2000","central_meridian":"107°45'","location":"Xã Đông Xá","written_total_area":"28,50 ha",
             "zones":[{"name":"Khu 1","written_area":"(Diện tích: 12,50 ha)","corners":[
               {"label":"K1","x":"2.315.000,00","y":"438.000,00","lat":null,"lon":null},
               {"label":"K2","x":"2.315.000,00","y":"438.500,00","lat":null,"lon":null}]}]}
            """;
        var reader = new GeminiZoneMapReader(Qwen(new FakeHandler(_ => Answers.Sse(Answers.Delta(json, "stop"), "[DONE]"))));

        var read = await reader.ReadAsync(new byte[] { 1, 2, 3 }, "image/png", CancellationToken.None);

        Assert.Null(read.ErrorCode);
        Assert.Equal("VN-2000", read.Value!.CoordinateSystem);
        Assert.Equal("28,50 ha", read.Value.WrittenTotalArea);
        var zone = Assert.Single(read.Value.Zones!);
        Assert.Equal("Khu 1", zone.Name);
        Assert.Equal(["K1", "K2"], zone.Corners!.Select(c => c.Label));
        Assert.Equal("2.315.000,00", zone.Corners![0].X);
    }

    [Fact]
    public async Task A_failed_Qwen_read_is_a_failed_scan_with_its_code()
    {
        var reader = new GeminiRaftFormReader(Qwen(new FakeHandler(_ => Answers.Json(HttpStatusCode.TooManyRequests, "{}"))));

        var read = await reader.ReadAsync(new byte[] { 1 }, "image/jpeg", CancellationToken.None);

        Assert.Null(read.Value);
        Assert.Equal(OcrErrorCodes.Unavailable, read.ErrorCode);
    }

    [Fact]
    public void The_schema_is_written_under_the_prompt_in_the_order_of_the_schema()
    {
        var text = QwenPromptText.Compose(MeterPrompt.Text, MeterPrompt.Schema);

        Assert.StartsWith(MeterPrompt.Text.TrimEnd(), text);
        Assert.Contains("ONE JSON object", text);
        Assert.Contains("\"ph\": null or {", text);
        Assert.Contains("\"value\": string or null", text);
        Assert.Contains("\"uncertain\": true or false", text);
        Assert.Contains("\"other_numbers\": [ string, ... ]", text);
        Assert.True(text.IndexOf("\"ph\"", StringComparison.Ordinal) < text.IndexOf("\"salinity\"", StringComparison.Ordinal));
        Assert.True(text.IndexOf("\"salinity\"", StringComparison.Ordinal) < text.IndexOf("\"measured_at\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_licence_map_schema_keeps_its_nested_arrays()
    {
        var text = QwenPromptText.Compose(ZoneMapPrompt.Text, ZoneMapPrompt.Schema);

        Assert.Contains("\"zones\": [ {", text);
        Assert.Contains("\"corners\": [ {", text);
        Assert.Contains("\"written_total_area\": string or null", text);
    }

    [Fact]
    public void Settings_are_checked_for_the_provider_and_for_the_fallback()
    {
        Assert.Null(new OcrProviderOptions().Validate());
        Assert.Null(new OcrProviderOptions { Provider = "Paddle" }.Validate());
        Assert.Equal("Ocr:Fallback must be empty or Qwen.", new OcrProviderOptions { Fallback = "Gemini" }.Validate());
        Assert.Contains("same as", new OcrProviderOptions { Provider = "Qwen", Fallback = "Qwen" }.Validate());

        var withoutQwenKey = new OcrProviderOptions { Provider = "Gemini", Fallback = "Qwen" };
        withoutQwenKey.Gemini.ApiKey = "g";
        Assert.Equal("Ocr:Qwen:ApiKey is not set.", withoutQwenKey.Validate());
        withoutQwenKey.Qwen.ApiKey = "q";
        Assert.Null(withoutQwenKey.Validate());

        Assert.Equal("Ocr:Qwen:ApiKey is not set.", new OcrProviderOptions { Provider = "Qwen" }.Validate());
        var badModel = new OcrProviderOptions { Provider = "Qwen" };
        badModel.Qwen.ApiKey = "q";
        badModel.Qwen.Readers.ZoneMap.Model = "../x";
        Assert.Contains("ZoneMap", badModel.Qwen.Validate());
        badModel.Qwen.Readers.ZoneMap.Model = "qwen3.8-omni-flash";
        badModel.Qwen.BaseUrl = "http://example.com/";
        Assert.Contains("https", badModel.Validate());
    }

    [Fact]
    public void A_fallback_behind_a_missing_provider_is_not_used_and_not_an_error()
    {
        var options = new OcrProviderOptions { Fallback = "Qwen" };

        Assert.False(options.UsesQwenFallback);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void The_defaults_are_the_tested_model_with_a_licence_map_timeout_of_at_least_90_seconds()
    {
        var qwen = new QwenOptions();

        Assert.Equal("https://dashscope-intl.aliyuncs.com/compatible-mode/v1/", qwen.BaseUrl);
        Assert.All(Enum.GetValues<GeminiReader>(), r => Assert.Equal("qwen3.8-omni-flash", qwen.For(r).Model));
        Assert.True(qwen.Readers.ZoneMap.TimeoutSeconds >= 90);
        Assert.True(qwen.DisableThinking);
        Assert.False(qwen.JsonMode);
        Assert.All(Enum.GetValues<GeminiReader>(), r => Assert.True(qwen.For(r).Stream));
    }

    [Fact]
    public void The_read_budget_is_the_meter_timeout_of_the_provider_or_the_scan_budget()
    {
        var gemini = new OcrProviderOptions { Provider = "Gemini" };
        var qwen = new OcrProviderOptions { Provider = "Qwen" };
        qwen.Qwen.Readers.Meter.TimeoutSeconds = 12;

        Assert.Equal(TimeSpan.FromSeconds(25), gemini.ReadBudget(GeminiReader.Meter));
        Assert.Equal(TimeSpan.FromSeconds(12), qwen.ReadBudget(GeminiReader.Meter));
        Assert.Equal(TimeSpan.FromSeconds(115), gemini.ReadBudget(GeminiReader.ZoneMap));
        Assert.Equal(TimeSpan.FromSeconds(115), gemini.ReadBudget(GeminiReader.RaftForm));
    }
}
