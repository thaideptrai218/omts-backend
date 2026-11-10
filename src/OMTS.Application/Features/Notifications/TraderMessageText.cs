using System.Globalization;

namespace OMTS.Application.Features.Notifications;

/// <summary>Texts shortened for a message to a trader's phone (MSG-SALE-03, MSG-CLAIM-02): the whole text stays in the notification list and on the record.</summary>
public static class TraderMessageText
{
    /// <summary>The text up to <paramref name="maxLength"/> characters and "...": cut between whole characters as the reader sees them, never half an emoji, never a letter without its mark.</summary>
    public static string? Shorten(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        var end = 0;
        for (var next = 0; next <= maxLength; next += StringInfo.GetNextTextElementLength(text.AsSpan(next)))
        {
            end = next;
        }

        return text[..end].TrimEnd() + "...";
    }
}
