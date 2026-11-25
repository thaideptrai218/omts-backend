using OMTS.Application.Features.Farming.Licenses;
using OMTS.Application.Features.Files;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>FARM-03: the scan of a license is optional on create, edit and replace; a file that is sent still has to be a PDF, JPEG or PNG of at most 10 MB.</summary>
public class ZoneLicenseFileTests
{
    [Fact]
    public void No_file_is_no_problem_and_no_upload()
    {
        var fields = new List<string>();

        var file = ZoneLicenseService.CheckFile(fields, null);

        Assert.Null(file);
        Assert.Empty(fields);
    }

    [Fact]
    public void A_pdf_is_accepted()
    {
        var fields = new List<string>();
        var bytes = "%PDF-1.4 license"u8.ToArray();

        var file = ZoneLicenseService.CheckFile(fields, new IncomingFile("license.pdf", bytes.Length, new MemoryStream(bytes)));

        Assert.NotNull(file);
        Assert.Empty(fields);
    }

    [Fact]
    public void A_file_that_is_not_a_document_names_the_file_field()
    {
        var fields = new List<string>();
        var bytes = "MZ not a document"u8.ToArray();

        var file = ZoneLicenseService.CheckFile(fields, new IncomingFile("license.pdf", bytes.Length, new MemoryStream(bytes)));

        Assert.Null(file);
        Assert.Equal(["File"], fields);
    }
}
