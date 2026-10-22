using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OMTS.Api.IntegrationTests.Infrastructure;
using static OMTS.Api.IntegrationTests.Infrastructure.AcsTestKit;

namespace OMTS.Api.IntegrationTests.Cooperatives;

/// <summary>Request builders and sample files for the FARM-01 tests.</summary>
internal static class CooperativeProfileRequests
{
    public static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];
    public static readonly byte[] WebP = [.. "RIFF"u8, 0x24, 0, 0, 0, .. "WEBPVP8 "u8];
    public static readonly byte[] Gif = [.. "GIF89a"u8, 1, 0, 1, 0, 0x80, 0];
    public static readonly byte[] Pdf = [.. "%PDF-1.7\n%"u8, 0xE2, 0xE3];

    public static async Task<HttpClient> OwnerAsync(AuthApiFactory api) =>
        await api.LoginAsync((await api.AddAccountAsync(NewCode(), Password)).Username);

    public static async Task<string?> RowVersionAsync(HttpClient owner) =>
        (await owner.GetFromJsonAsync<JsonElement>("/api/cooperative/profile")).GetProperty("rowVersion").GetString();

    /// <summary>A complete, valid profile; <paramref name="change"/> overrides single fields.</summary>
    public static Dictionary<string, object?> Valid(string? rowVersion, Action<Dictionary<string, object?>>? change = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["fullName"] = "Hợp tác xã Nuôi trồng thủy sản Trung Nam",
            ["taxCode"] = "0109876543",
            ["officeAddress"] = "Thôn 3, xã Đông Xá, Vân Đồn, Quảng Ninh",
            ["phoneNumber"] = "0912345678",
            ["email"] = "lienhe@htxtrungnam.vn",
            ["website"] = "https://htxtrungnam.vn",
            ["legalRepresentative"] = "Nguyễn Văn Nam",
            ["businessLines"] = new[] { "Nuôi hàu", "Thương mại thủy sản" },
            ["rowVersion"] = rowVersion,
        };
        change?.Invoke(body);
        return body;
    }

    /// <summary>Multipart upload in field <c>file</c>; the client content type is deliberately always image/png.</summary>
    public static Task<HttpResponseMessage> UploadLogoAsync(HttpClient client, byte[] content, string fileName, string field = "file")
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, field, fileName);
        return client.PostAsync("/api/cooperative/profile/logo", form);
    }
}
