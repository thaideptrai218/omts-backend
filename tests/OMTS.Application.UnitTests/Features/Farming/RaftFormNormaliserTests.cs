using OMTS.Application.Features.Farming.Rafts.FormScan;

namespace OMTS.Application.UnitTests.Features.Farming;

/// <summary>The raft form fields are copied as written: trimmed, blank as null, never interpreted (contract 26/11/2026).</summary>
public class RaftFormNormaliserTests
{
    [Fact]
    public void Fields_are_trimmed_and_kept_as_written()
    {
        var reading = RaftFormNormaliser.Normalise(new RaftFormReading(" B-01 ", "24 m x 10 m", " 6", "Gần cửa sông ", "Bè mới", "10,1234", "106.5678 "));

        Assert.Equal(new RaftFormReading("B-01", "24 m x 10 m", "6", "Gần cửa sông", "Bè mới", "10,1234", "106.5678"), reading);
    }

    [Fact]
    public void Blank_fields_are_null()
    {
        var reading = RaftFormNormaliser.Normalise(new RaftFormReading("", "  ", null, "\t", "", null, " "));

        Assert.Equal(new RaftFormReading(null, null, null, null, null, null, null), reading);
    }

    [Fact]
    public void A_field_longer_than_the_raft_form_accepts_is_cut()
    {
        var reading = RaftFormNormaliser.Normalise(new RaftFormReading(new string('B', 80), null, null, new string('v', 700), new string('n', 3000), null, null));

        Assert.Equal(50, reading.Code!.Length);
        Assert.Equal(500, reading.Location!.Length);
        Assert.Equal(2000, reading.Notes!.Length);
    }
}
