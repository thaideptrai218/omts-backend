using System.Globalization;

namespace OMTS.Application.Common.Interfaces.Ocr;

/// <summary>
/// The part of the meter photo that shows the display, as fractions of the photo (0…1), chosen by the user in the Mini App
/// (WATER-01 step 5). The stored evidence photo is always the whole photo. The Gemini provider has no image library to crop with, so it accepts the crop and ignores it (it may become a prompt hint if that proves to help).
/// </summary>
public sealed record OcrCrop(double Left, double Top, double Right, double Bottom)
{
    /// <summary>A crop narrower or lower than this (5 % of the photo) cannot hold a readable display.</summary>
    public const double MinSize = 0.05;

    /// <summary>
    /// Parses "left,top,right,bottom" (invariant decimals). Empty means no crop; anything else that is not four numbers with
    /// 0 ≤ left &lt; right ≤ 1, 0 ≤ top &lt; bottom ≤ 1 and at least <see cref="MinSize"/> each way is invalid.
    /// </summary>
    public static bool TryParse(string? text, out OcrCrop? crop)
    {
        crop = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var parts = text.Split(',');
        var values = new double[4];
        if (parts.Length != 4)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i]))
            {
                return false;
            }
        }

        var (left, top, right, bottom) = (values[0], values[1], values[2], values[3]);
        if (left < 0 || top < 0 || right > 1 || bottom > 1 || right - left < MinSize || bottom - top < MinSize)
        {
            return false;
        }

        crop = new OcrCrop(left, top, right, bottom);
        return true;
    }

    /// <summary>The same "left,top,right,bottom" form that <see cref="TryParse"/> reads.</summary>
    public override string ToString() =>
        string.Join(',', new[] { Left, Top, Right, Bottom }.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture)));
}
