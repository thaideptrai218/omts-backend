
namespace OMTS.Infrastructure.Ocr;

/// <summary>
/// "Ocr" section: the provider (only <c>Fake</c> until the real OCR provider ticket) and the fake's answers.
/// <c>Fake</c> is set in Development and test settings only; unset or any other value fails closed
/// (<see cref="NotConfiguredOcrService"/>) so a deployment never invents readings.
/// </summary>
public sealed class OcrProviderOptions
{
    public const string SectionName = "Ocr";
    public const string FakeProvider = "Fake";

    public string Provider { get; set; } = "";
    public FakeOcrOptions Fake { get; set; } = new();
}
