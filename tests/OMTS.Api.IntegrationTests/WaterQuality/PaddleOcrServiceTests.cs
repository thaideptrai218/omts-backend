using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OMTS.Application.Common.Interfaces.Ocr;
using OMTS.Infrastructure.Ocr;
using OMTS.Infrastructure.Ocr.Paddle;

namespace OMTS.Api.IntegrationTests.WaterQuality;

/// <summary>The PaddleOCR sidecar adapter against a stubbed sidecar: request shape, answer parsing, and failures that never throw.</summary>
public class PaddleOcrServiceTests
{
    private const string Answer = """
        {"width": 538, "height": 221, "ms": 412, "lines": [
          {"text": "1033", "score": 0.9989, "box": [185, 27, 521, 184]},
          {"text": "pH", "score": 0.997, "box": [33, 113, 111, 174]},
          {"text": "28.6°C", "score": 0.95, "box": [200, 190, 400, 215]}
        ]}
        """;

    [Fact]
    public async Task The_photo_and_the_crop_are_posted_and_the_lines_become_suggestions()
    {
        HttpRequestMessage? sent = null;
        byte[]? body = null;
        var service = Service(async request =>
        {
            sent = request;
            body = await request.Content!.ReadAsByteArrayAsync();
            return Json(Answer);
        });
        var photo = new MemoryStream([1, 2, 3]);

        var result = await service.RecognizeAsync(photo, "image/jpeg", new OcrCrop(0.1, 0.25, 0.9, 0.8), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, sent!.Method);
        Assert.Equal("/recognize", sent.RequestUri!.AbsolutePath);
        Assert.Equal("?crop=0.1%2C0.25%2C0.9%2C0.8", sent.RequestUri.Query);
        Assert.Equal("image/jpeg", sent.Content!.Headers.ContentType!.MediaType);
        Assert.Equal([1, 2, 3], body);
        Assert.True(result.Succeeded);
        Assert.Equal(10.33m, result.Ph!.Value);
        Assert.Equal(28.6m, result.Temperature!.Value);
        Assert.Null(result.Salinity);
        Assert.Null(result.MeasuredAt);
        Assert.True(photo.CanRead); // the caller's stream is left open
    }

    [Fact]
    public async Task Without_a_crop_the_whole_photo_is_read()
    {
        Uri? uri = null;
        var service = Service(request =>
        {
            uri = request.RequestUri;
            return Task.FromResult(Json("""{"lines": [], "ms": 5}"""));
        });

        var result = await service.RecognizeAsync(new MemoryStream([1]), "image/png", null, CancellationToken.None);

        Assert.Equal("", uri!.Query);
        Assert.True(result.Succeeded); // read, nothing found: the recognizer names every value as not read
        Assert.Null(result.Ph);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\": \"ocr failed\"}", PaddleOcrService.UnavailableError)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\": \"unreadable image\"}", PaddleOcrService.UnavailableError)]
    [InlineData(HttpStatusCode.OK, "not json", PaddleOcrService.BadResponseError)]
    [InlineData(HttpStatusCode.OK, "{\"width\": 1}", PaddleOcrService.BadResponseError)]
    public async Task A_sidecar_error_or_an_answer_that_is_not_understood_is_a_failed_result(HttpStatusCode status, string answer, string error)
    {
        var service = Service(_ => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") }));

        var result = await service.RecognizeAsync(new MemoryStream([1]), "image/png", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(error, result.ErrorCode);
    }

    [Fact]
    public async Task An_unreachable_sidecar_or_an_HTTP_timeout_is_a_failed_result()
    {
        var down = await Service(_ => throw new HttpRequestException("connection refused"))
            .RecognizeAsync(new MemoryStream([1]), "image/png", null, CancellationToken.None);
        var slow = await Service(async _ => { await Task.Delay(TimeSpan.FromSeconds(5)); return Json(Answer); }, timeout: TimeSpan.FromMilliseconds(200))
            .RecognizeAsync(new MemoryStream([1]), "image/png", null, CancellationToken.None);

        Assert.Equal(PaddleOcrService.UnavailableError, down.ErrorCode);
        Assert.Equal(PaddleOcrService.TimeoutError, slow.ErrorCode);
    }

    [Fact]
    public async Task The_callers_cancellation_still_cancels()
    {
        using var cancel = new CancellationTokenSource();
        var service = Service(async request => { await cancel.CancelAsync(); await Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None); return Json(Answer); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecognizeAsync(new MemoryStream([1]), "image/png", null, cancel.Token));
    }

    private static PaddleOcrService Service(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer, TimeSpan? timeout = null) =>
        new(new HttpClient(new StubHandler(answer)) { BaseAddress = new Uri("http://ocr:8000/"), Timeout = timeout ?? TimeSpan.FromSeconds(10) },
            Options.Create(new OcrProviderOptions { Provider = OcrProviderOptions.PaddleProvider }),
            NullLogger<PaddleOcrService>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = answer(request);
            // Honours the token like a real handler: HttpClient's timeout and the caller's cancellation both end the wait.
            return await response.WaitAsync(cancellationToken);
        }
    }
}
