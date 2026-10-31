using OMTS.Application.Features.Files.Models;
using OMTS.Application.Features.Files;

namespace OMTS.Application.UnitTests.Features.Files;

public class FileValidatorTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D];
    private static readonly byte[] Pdf = "%PDF-1.7\n%..."u8.ToArray();
    private static readonly byte[] WebP = "RIFF\x10\0\0\0WEBPVP8 "u8.ToArray();
    private static readonly byte[] Exe = [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0, 4, 0, 0, 0];

    [Fact]
    public void Real_format_is_read_from_the_content()
    {
        Assert.Equal(FileFormat.Jpeg, FileValidator.DetectFormat(Jpeg));
        Assert.Equal(FileFormat.Png, FileValidator.DetectFormat(Png));
        Assert.Equal(FileFormat.Pdf, FileValidator.DetectFormat(Pdf));
        Assert.Equal(FileFormat.WebP, FileValidator.DetectFormat(WebP));
        Assert.Null(FileValidator.DetectFormat(Exe));
    }

    [Fact]
    public void Renamed_executable_is_rejected_as_a_photo()
    {
        var result = FileValidator.Validate([File("photo.jpg", Exe)], FileRules.Photos);

        Assert.False(result.IsValid);
        Assert.Equal("photo.jpg", result.InvalidFileName);
    }

    [Fact]
    public void Pdf_is_a_document_but_not_a_photo()
    {
        Assert.True(FileValidator.Validate([File("license.pdf", Pdf)], FileRules.Documents).IsValid);
        Assert.False(FileValidator.Validate([File("license.pdf", Pdf)], FileRules.Photos).IsValid);
    }

    [Fact]
    public void File_over_10_MB_is_rejected()
    {
        var file = new IncomingFile("big.jpg", FileRules.TenMegabytes + 1024 * 1024, new MemoryStream(Jpeg));

        var result = FileValidator.Validate([file], FileRules.Photos);

        Assert.False(result.IsValid);
        Assert.Equal("big.jpg", result.InvalidFileName);
    }

    [Fact]
    public void More_than_10_files_per_save_are_rejected()
    {
        var files = Enumerable.Range(0, 11).Select(i => File($"p{i}.png", Png)).ToList();

        Assert.False(FileValidator.Validate(files, FileRules.Photos).IsValid);
    }

    [Fact]
    public void Valid_file_reports_its_content_type_and_keeps_the_stream_position()
    {
        var file = File("meter.png", Png);

        var result = FileValidator.Validate([file], FileRules.Photos);

        Assert.True(result.IsValid);
        Assert.Equal("image/png", result.Files[0].ContentType);
        Assert.Equal(0, file.Content.Position);
    }

    private static IncomingFile File(string name, byte[] content) => new(name, content.Length, new MemoryStream(content));
}
