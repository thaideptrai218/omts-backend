namespace OMTS.Application.Common.Interfaces.Ocr;

/// <summary>"Ocr" section: how long the recognizer waits for the OCR Service and when a read value is shown as uncertain.</summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>
    /// After this the call is abandoned and the user enters the values manually (MSG-WATER-01). With the Gemini provider it is set to the
    /// meter reader timeout + 2 s, so the client's own timeout answers first.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>A field read with a lower confidence is flagged uncertain so the user checks it against the photo.</summary>
    public double UncertainBelow { get; set; } = 0.8;
}
