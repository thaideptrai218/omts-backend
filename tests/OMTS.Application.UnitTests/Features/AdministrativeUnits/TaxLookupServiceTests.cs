using Microsoft.Extensions.Caching.Memory;
using OMTS.Application.Common.Interfaces.Addresses;
using OMTS.Application.Common.Results;
using OMTS.Application.Features.TaxLookup;
using OMTS.Application.UnitTests.Fakes;

namespace OMTS.Application.UnitTests.Features.AdministrativeUnits;

public sealed class TaxLookupServiceTests
{
    private static (TaxLookupService Service, FakeTaxRegistry Registry, FakeAdministrativeUnitSource Source) Build()
    {
        var registry = new FakeTaxRegistry();
        var source = new FakeAdministrativeUnitSource();
        var cache = new MemoryCache(new MemoryCacheOptions());
        return (new TaxLookupService(registry, AddressTestData.Service(source, cache), cache), registry, source);
    }

    [Theory]
    [InlineData("0100109106")]
    [InlineData("0100109106-001")]
    [InlineData(" 0100109106 ")]
    public async Task A_tax_code_of_10_digits_or_10_digits_and_a_branch_is_looked_up(string taxCode)
    {
        var (service, registry, _) = Build();

        var result = await service.LookupAsync(taxCode, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, registry.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123456789")]
    [InlineData("01001091061")]
    [InlineData("012345678901")]
    [InlineData("0100109106-01")]
    [InlineData("0100109106-0011")]
    [InlineData("0100109106_001")]
    [InlineData("01001091O6")]
    [InlineData("../0100109106")]
    public async Task Any_other_tax_code_is_refused_on_the_tax_code_field_and_the_registry_is_not_asked(string? taxCode)
    {
        var (service, registry, _) = Build();

        var result = await service.LookupAsync(taxCode, CancellationToken.None);

        Assert.Equal(ErrorKind.Validation, result.Error!.Kind);
        Assert.Equal("MSG-GEN-01", result.Error.Message.Code);
        Assert.Equal("Tax code", result.Error.Details!["field"]);
        Assert.Equal(0, registry.Calls);
    }

    [Fact]
    public async Task The_company_has_its_address_split_into_the_codes_of_the_list()
    {
        var (service, _, _) = Build();

        var found = (await service.LookupAsync("0100109106", CancellationToken.None)).Value!;

        Assert.Equal("TẬP ĐOÀN CÔNG NGHIỆP - VIỄN THÔNG QUÂN ĐỘI", found.Name);
        Assert.Equal("Số 5, Phố Nguyễn Hữu An, Phường Cầu Giấy, TP Hà Nội", found.Address);
        Assert.Equal(new AddressUnitRef(1, "Thành phố Hà Nội"), found.Province);
        Assert.Equal(new AddressUnitRef(10, "Phường Cầu Giấy"), found.Ward);
        Assert.Equal("Số 5, Phố Nguyễn Hữu An", found.Street);
        Assert.Equal("NNT đang hoạt động", found.Status);
    }

    [Fact]
    public async Task An_address_the_list_does_not_know_is_given_whole_as_the_street()
    {
        var (service, registry, _) = Build();
        registry.Address = "Số 1, Phường Mỹ Đình 1, Quận Nam Từ Liêm, Thành phố Cũ";

        var found = (await service.LookupAsync("0100109106", CancellationToken.None)).Value!;

        Assert.Null(found.Province);
        Assert.Null(found.Ward);
        Assert.Equal(registry.Address, found.Street);
    }

    [Fact]
    public async Task A_company_found_is_kept_for_the_next_lookup()
    {
        var (service, registry, _) = Build();

        await service.LookupAsync("0100109106", CancellationToken.None);
        await service.LookupAsync("0100109106", CancellationToken.None);
        await service.LookupAsync("0100109106-001", CancellationToken.None);

        Assert.Equal(2, registry.Calls);
    }

    [Fact]
    public async Task A_company_is_asked_for_again_after_a_day()
    {
        var registry = new FakeTaxRegistry();
        var clock = new TestClock();
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
        var service = new TaxLookupService(registry, AddressTestData.Service(new FakeAdministrativeUnitSource(), cache), cache);

        await service.LookupAsync("0100109106", CancellationToken.None);
        clock.Advance(TaxLookupService.CacheTime + TimeSpan.FromMinutes(1));
        await service.LookupAsync("0100109106", CancellationToken.None);

        Assert.Equal(2, registry.Calls);
    }

    [Fact]
    public async Task A_tax_code_nobody_holds_is_not_found_with_a_message_and_not_kept()
    {
        var (service, registry, _) = Build();
        registry.Outcome = TaxRegistryOutcome.NotFound;

        var result = await service.LookupAsync("0100109106", CancellationToken.None);
        await service.LookupAsync("0100109106", CancellationToken.None);

        Assert.Equal(ErrorKind.NotFound, result.Error!.Kind);
        Assert.Contains("Không tìm thấy", result.Error.Message.Text);
        Assert.Equal(2, registry.Calls);
    }

    [Fact]
    public async Task A_registry_that_is_down_answers_service_unavailable_and_is_not_kept()
    {
        var (service, registry, _) = Build();
        registry.Outcome = TaxRegistryOutcome.Unavailable;

        var down = await service.LookupAsync("0100109106", CancellationToken.None);
        registry.Outcome = TaxRegistryOutcome.Found;
        var up = await service.LookupAsync("0100109106", CancellationToken.None);

        Assert.Equal(ErrorKind.ServiceUnavailable, down.Error!.Kind);
        Assert.True(up.IsSuccess);
    }

    [Fact]
    public async Task The_company_is_still_given_while_the_address_list_is_down()
    {
        var (service, _, source) = Build();
        source.Down = true;

        var found = (await service.LookupAsync("0100109106", CancellationToken.None)).Value!;

        Assert.Null(found.Province);
        Assert.Equal(found.Address, found.Street);
    }

    private sealed class TestClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => UtcNow += by;
    }
}
