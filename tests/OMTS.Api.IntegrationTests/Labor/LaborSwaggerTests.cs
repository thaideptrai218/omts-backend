using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;

namespace OMTS.Api.IntegrationTests.Labor;

/// <summary>Definition of Done: Swagger documents every LAB-02…LAB-10 endpoint.</summary>
public class LaborSwaggerTests(AuthApiFactory factory) : IClassFixture<AuthApiFactory>
{
    [Fact]
    public async Task Swagger_document_lists_every_labor_endpoint()
    {
        var response = await factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        foreach (var (path, method) in new[]
        {
            ("/api/labor/workers", "post"), ("/api/labor/workers/{id}", "put"), ("/api/labor/workers/{id}/deactivate", "post"),
            ("/api/labor/workers/{id}/reactivate", "post"), ("/api/labor/teams", "get"), ("/api/labor/teams/{id}/deactivate", "post"),
            ("/api/zones/{zoneId}/work-assignments", "post"), ("/api/zones/{zoneId}/work-assignments/{id}", "put"),
            ("/api/zones/{zoneId}/work-assignments/{id}/cancel", "post"), ("/api/zones/{zoneId}/work-assignments/{id}/completion", "post"),
            ("/api/zones/{zoneId}/work-assignments/{id}/completion/evidence", "post"), ("/api/zones/{zoneId}/work-assignments/assignees", "get"),
            ("/api/zones/{zoneId}/attendance", "get"), ("/api/zones/{zoneId}/attendance/{date}", "put"),
            ("/api/zones/{zoneId}/attendance/records/{recordId}/evidence", "post"), ("/api/zones/{zoneId}/attendance/people", "get"),
            ("/api/pay-rates", "post"), ("/api/pay-rates/bulk", "post"), ("/api/pay-rates/people", "get"), ("/api/pay-rates/{id}", "put"),
            ("/api/payrolls", "post"), ("/api/payrolls/{period}", "get"), ("/api/payrolls/{period}/refresh", "post"),
            ("/api/payrolls/{period}/payslips/{payslipId}/lines", "post"), ("/api/payrolls/{period}/finalize", "post"),
            ("/api/payrolls/{period}/send-back", "post"), ("/api/payrolls/{period}/reopen", "post"),
            ("/api/payslips", "get"), ("/api/payslips/{id}/payments", "post"), ("/api/payslips/payments/bulk", "post"),
            ("/api/labor-payments/{id}/reverse", "post"), ("/api/labor-payments/{id}/documents", "post"),
            ("/api/salary-advances", "post"), ("/api/me/pay/attendance", "get"), ("/api/me/pay/payslips", "get"),
            ("/api/me/pay/payslips/{id}", "get"), ("/api/me/pay/advances", "get"),
        })
        {
            Assert.True(paths.TryGetProperty(path, out var item) && item.TryGetProperty(method, out _), $"{method} {path}");
        }
    }
}
