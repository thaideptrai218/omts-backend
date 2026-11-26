using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Ocr.Gemini;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>WATER-01 on Gemini: the mapping of the model's answer, and the recognize endpoint end to end over a fake HTTP handler.</summary>
public class GeminiOcrServiceTests
{
    private sealed class StubClient(string? json, string? errorCode = null) : IGeminiVisionClient
    {
        public GeminiReader? Reader { get; private set; }

        public Task<GeminiJsonResult> GenerateJsonAsync(
            GeminiReader reader, ReadOnlyMemory<byte> image, string mimeType, string prompt, JsonElement responseSchema, CancellationToken cancellationToken)
        {
            Reader = reader;
            return Task.FromResult(json is null
                ? GeminiJsonResult.Failure(errorCode!, TimeSpan.Zero)
                : new GeminiJsonResult(true, JsonSerializer.Deserialize<JsonElement>(json), null, null, TimeSpan.Zero));
        }
    }

    private static Task<OcrResult> ReadAsync(StubClient client) =>
        new GeminiMeterOcrService(client).RecognizeAsync(new MemoryStream([1, 2, 3]), "image/jpeg", null, CancellationToken.None);

    private const string Certain = """
        {"ph":{"value":"7.84","uncertain":false},"salinity":{"value":"25,5","uncertain":false},"temperature":{"value":"28.5","uncertain":false},
         "measured_at":"2026-11-26 08:15","other_numbers":["MI170"]}
        """;

    [Fact]
    public async Task Values_the_model_is_sure_of_get_confidence_095_and_decimals_are_parsed_by_the_server()
    {
        var client = new StubClient(Certain);

        var result = await ReadAsync(client);

        Assert.True(result.Succeeded);
        Assert.Equal(new OcrReading<decimal>(7.84m, 0.95), result.Ph);
        Assert.Equal(new OcrReading<decimal>(25.5m, 0.95), result.Salinity);
        Assert.Equal(new OcrReading<decimal>(28.5m, 0.95), result.Temperature);
        Assert.Equal(new DateTimeOffset(2026, 11, 26, 8, 15, 0, TimeSpan.FromHours(7)), result.MeasuredAt!.Value);
        Assert.Equal(GeminiReader.Meter, client.Reader);
    }

    [Fact]
    public async Task A_value_flagged_uncertain_gets_confidence_05_so_the_user_is_asked_to_check_it()
    {
        var result = await ReadAsync(new StubClient("""
            {"ph":{"value":"7.8","uncertain":true},"salinity":null,"temperature":{"value":null,"uncertain":false},"measured_at":null,"other_numbers":[]}
            """));

        Assert.Equal(new OcrReading<decimal>(7.8m, 0.5), result.Ph);
        Assert.Null(result.Salinity);
        Assert.Null(result.Temperature);
        Assert.Null(result.MeasuredAt);
    }

    [Theory]
    [InlineData("412")] // a plausible digit string is passed on: the recognizer's ranges decide
    [InlineData("7.8")]
    public void The_digits_are_parsed_as_the_display_shows_them(string text)
    {
        Assert.Equal(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture), GeminiMeterOcrService.ParseDecimal(text));
    }

    [Theory]
    [InlineData("pH 7.8")]
    [InlineData("7.8 pH")]
    [InlineData("-7.8")]
    [InlineData("1.234.5")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]
    public void Text_that_is_no_plain_number_is_not_read(string? text)
    {
        Assert.Null(GeminiMeterOcrService.ParseDecimal(text));
    }

    [Theory]
    [InlineData("2026-11-26 08:15")]
    [InlineData("26/11/2026 08:15")]
    [InlineData("26/11/2026 8:15:30")]
    public async Task The_measurement_time_is_read_in_the_common_display_formats_as_local_time(string text)
    {
        var result = await ReadAsync(new StubClient($$"""{"ph":null,"salinity":null,"temperature":null,"measured_at":"{{text}}","other_numbers":[]}"""));

        Assert.Equal(new DateTimeOffset(2026, 11, 26, 8, 15, 0, TimeSpan.FromHours(7)).Date, result.MeasuredAt!.Value.Date);
    }

    [Theory]
    [InlineData(OcrErrorCodes.Unavailable)]
    [InlineData(OcrErrorCodes.Timeout)]
    [InlineData(OcrErrorCodes.BadResponse)]
    [InlineData(OcrErrorCodes.NotConfigured)]
    public async Task A_failure_of_the_client_is_a_failed_result_with_the_same_code(string code)
    {
        var result = await ReadAsync(new StubClient(null, code));

        Assert.False(result.Succeeded);
        Assert.Equal(code, result.ErrorCode);
    }

    [Fact]
    public async Task The_recognize_endpoint_reads_a_photo_through_Gemini_and_an_out_of_range_value_is_unread()
    {
        // pH 15.2 cannot be a pH: WaterMeterRecognizer treats it as not read (MSG-WATER-01), whatever the model says.
        var handler = new FakeGeminiHandler(_ => AnswerOf("""
            {"ph":{"value":"15.2","uncertain":false},"salinity":{"value":"21","uncertain":true},"temperature":{"value":"27.5","uncertain":false},"measured_at":null,"other_numbers":[]}
            """));
        using var api = Factory(handler);
        var z = await api.ZoneAsync();

        var response = await z.RecognizeAsync();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ocr = body.GetProperty("ocr");
        Assert.Equal("Success", ocr.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, ocr.GetProperty("ph").ValueKind);
        Assert.Equal(21m, ocr.GetProperty("salinity").GetProperty("value").GetDecimal());
        Assert.True(ocr.GetProperty("salinity").GetProperty("uncertain").GetBoolean());
        Assert.Equal(0.5, ocr.GetProperty("salinity").GetProperty("confidence").GetDouble());
        Assert.False(ocr.GetProperty("temperature").GetProperty("uncertain").GetBoolean());
        Assert.Single(handler.Requests);
        Assert.Equal("test-key", handler.Requests.Single().ApiKeyHeader);
    }

    [Fact]
    public async Task When_Gemini_is_down_recognize_still_answers_the_photo_key_and_asks_for_manual_entry()
    {
        var handler = new FakeGeminiHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") });
        using var api = Factory(handler);
        var z = await api.ZoneAsync();

        var response = await z.RecognizeAsync();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(body.GetProperty("photoKey").GetString()));
        Assert.Equal("Failed", body.GetProperty("ocr").GetProperty("status").GetString());
        Assert.Equal(2, handler.Requests.Count); // the 503 was retried once
    }

    [Fact]
    public async Task The_api_key_header_is_redacted_in_the_HttpClient_trace_logs()
    {
        var handler = new FakeGeminiHandler(_ => AnswerOf("""{"ph":null,"salinity":null,"temperature":null,"measured_at":null,"other_numbers":[]}"""));
        using var api = new WaterApiFactory(
            new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Trace" },
            services =>
            {
                services.PostConfigure<OcrProviderOptions>(o =>
                {
                    o.Provider = OcrProviderOptions.GeminiProvider;
                    (o.Gemini.ApiKey, o.Gemini.RetryPauseMs) = ("test-key", 0);
                });
                services.AddHttpClient<GeminiVisionClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
            });
        var z = await api.ZoneAsync();

        await z.RecognizeAsync();

        Assert.Equal("test-key", handler.Requests.Single().ApiKeyHeader);
        // The framework's own request log is on (the header name shows, with a masked value) and the key is nowhere.
        Assert.Contains(api.Logs.Lines, line => line.Contains("x-goog-api-key: *", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(api.Logs.Lines, line => line.Contains("test-key", StringComparison.Ordinal));
    }

    private static HttpResponseMessage AnswerOf(string modelJson) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = modelJson } } }, finishReason = "STOP" } } }),
            Encoding.UTF8, "application/json"),
    };

    /// <summary>The API as Production-like Gemini wiring (key from settings) with the Google endpoint replaced by <paramref name="handler"/>.</summary>
    private static WaterApiFactory Factory(FakeGeminiHandler handler) => new(
        services: services =>
        {
            // PostConfigure, not settings: appsettings.Development.json of the test host sets Ocr:Provider to Fake.
            services.PostConfigure<OcrProviderOptions>(o =>
            {
                o.Provider = OcrProviderOptions.GeminiProvider;
                (o.Gemini.ApiKey, o.Gemini.RetryPauseMs) = ("test-key", 0);
            });
            services.AddHttpClient<GeminiVisionClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        });
}
