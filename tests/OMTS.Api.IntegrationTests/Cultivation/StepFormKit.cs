using System.Text.Json;
using OMTS.Api.IntegrationTests.Farming;

namespace OMTS.Api.IntegrationTests.Cultivation;

/// <summary>
/// The multipart form of a step that used to be a JSON body (seeding session, cycle start, damage report; owner decision of 12/11/2026: 1 to 3 photos beside it): every property of the body becomes a field,
/// an array one field per item, an object (<c>lanePercents</c>) one JSON field, a null none. <paramref name="photos"/> JPEG parts are added as <c>photos</c>.
/// </summary>
public static class StepFormKit
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static MultipartFormDataContent FormOf(object? body, int photos = 0)
    {
        var form = new MultipartFormDataContent();
        if (body is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(body, Web).EnumerateObject())
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            form.Add(new StringContent(Text(item)), property.Name);
                        }

                        break;
                    case JsonValueKind.Object:
                        form.Add(new StringContent(property.Value.GetRawText()), property.Name);
                        break;
                    default:
                        form.Add(new StringContent(Text(property.Value)), property.Name);
                        break;
                }
            }
        }

        for (var i = 0; i < photos; i++)
        {
            form.Add(FarmingTestKit.FileContent(FarmingTestKit.Jpeg, "image/jpeg"), "photos", $"step-{i + 1}.jpg");
        }

        return form;
    }

    /// <summary>One more text field, as the client typed it.</summary>
    public static MultipartFormDataContent WithField(this MultipartFormDataContent form, string name, string value)
    {
        form.Add(new StringContent(value), name);
        return form;
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };
}
