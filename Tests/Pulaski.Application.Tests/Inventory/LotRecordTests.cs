using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Lot attributes and certificates (D-056): typed values beside the lot, documents never deleted.</summary>
public class LotRecordTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _resins = Guid.CreateVersion7();
    private readonly Lot _lot;

    public LotRecordTests()
    {
        Records.Sites.Add(_site);
        Records.Classes.Add(_resins);
        _lot = Lot.Create(Guid.CreateVersion7(), _site, Guid.CreateVersion7(), "R-1", Guid.CreateVersion7(), Guid.CreateVersion7(), false, LotOrigin.Received);
        _f.Stores.Inventory.InsertLotAsync(_lot, HandlerFixture.Now, CancellationToken.None).Wait();
        Records.MaterialClasses[_lot.MaterialId] = _resins;
    }

    private InMemoryLotRecords Records => _f.Stores.LotRecords;

    private Task<Contracts.Common.CreatedResponse> DefineAsync(string code, string type, IReadOnlyList<string>? choices = null, IReadOnlyList<Guid>? classes = null) =>
        new CreateLotAttributeHandler(_f.Stores.UnitOfWork, Records, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateLotAttributeCommand(_site, code, code, type, null, choices ?? [], classes is null, classes ?? [], 0), CancellationToken.None);

    private Task<Contracts.Common.VersionResponse> SetAsync(Guid attribute, string? value, long version = 0) =>
        new SetLotAttributeHandler(_f.Stores.UnitOfWork, _f.Stores.Inventory, Records, _f.Stores.Events, _f.Events)
            .HandleAsync(new SetLotAttributeCommand(_lot.Id, attribute, version, value), CancellationToken.None);

    private Task<Contracts.Common.CreatedResponse> AttachAsync(string? fileName, byte[]? content, string? url = null, string? number = null) =>
        new AttachLotDocumentHandler(_f.Stores.UnitOfWork, _f.Stores.Inventory, Records, _f.Stores.Events, _f.Events, _f.Ids, new FixedRequestContext("qa"), _f.Time)
            .HandleAsync(new AttachLotDocumentCommand(_lot.Id, "coa", "CoA", number, null, null, url, fileName, content), CancellationToken.None);

    [Theory]
    [InlineData("number", " 11.20 ", "11.20")]
    [InlineData("date", "2026-10-01", "2026-10-01")]
    [InlineData("boolean", "Y", "yes")]
    [InlineData("text", " Natural ", "Natural")]
    public void Values_are_read_in_their_type_and_shown_the_same_way(string type, string input, string shown)
    {
        var attribute = LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "X", "X", type, null, [], true, [], 0);

        Assert.Equal(shown, attribute.Parse(input).Display);
        Assert.True(attribute.Parse("  ").IsEmpty);
    }

    [Fact]
    public void Wrong_values_and_definitions_are_refused_with_a_reason()
    {
        var mfi = LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "MFI", "Melt index", "number", null, [], true, [], 0);
        Assert.Contains("decimal point", Assert.Throws<DomainRuleException>(() => mfi.Parse("1,5")).Message, StringComparison.Ordinal);
        var grade = LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "G", "Grade", "choice", null, [" A ", "B", "a"], true, [], 0);
        Assert.Equal(["A", "B"], grade.Choices);
        Assert.Equal("B", grade.Parse("b").Text);
        Assert.Contains("one of A, B", Assert.Throws<DomainRuleException>(() => grade.Parse("C")).Message, StringComparison.Ordinal);

        Assert.Throws<DomainRuleException>(() => LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "C", "C", "choice", null, [], true, [], 0));
        Assert.Throws<DomainRuleException>(() => LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "T", "T", "text", Guid.CreateVersion7(), [], true, [], 0));
        Assert.Throws<DomainRuleException>(() => LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "T", "T", "colour", null, [], true, [], 0));
        Assert.Throws<DomainRuleException>(() => LotAttributeDefinition.Create(Guid.CreateVersion7(), _site, "T", "T", "text", null, [], false, [], 0));
    }

    [Fact]
    public async Task A_value_is_set_with_its_version_recorded_as_an_event_and_can_be_cleared()
    {
        var mfi = await DefineAsync("mfi", "number");
        Assert.Equal("MFI", (await Records.GetAttributeAsync(mfi.Id, CancellationToken.None))!.Code);

        var set = await SetAsync(mfi.Id, "11.2");
        await Assert.ThrowsAsync<ConflictException>(() => SetAsync(mfi.Id, "12"));
        var changed = await SetAsync(mfi.Id, "12", set.Version);
        var same = await SetAsync(mfi.Id, "12.0", changed.Version);
        Assert.Equal(changed.Version, same.Version);

        var cleared = await SetAsync(mfi.Id, "", changed.Version);
        Assert.True((await Records.GetValueForUpdateAsync(_lot.Id, mfi.Id, CancellationToken.None))!.Value.IsEmpty);
        var events = _f.Stores.Events.Appended.Where(e => e.Type == EventTypes.LotAttributeSet).ToList();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(_lot.Id, e.Subject));
        Assert.True(cleared.Version > changed.Version);
    }

    [Fact]
    public async Task Only_attributes_offered_for_the_lots_class_and_switched_on_take_a_value()
    {
        var films = Guid.CreateVersion7();
        Records.Classes.Add(films);
        var forFilms = await DefineAsync("GAUGE", "number", classes: [films]);
        var forResins = await DefineAsync("MOISTURE", "number", classes: [_resins]);

        Assert.Contains("isn't recorded", (await Assert.ThrowsAsync<DomainRuleException>(() => SetAsync(forFilms.Id, "1"))).Message, StringComparison.Ordinal);
        var set = await SetAsync(forResins.Id, "0.02");

        var current = (await Records.GetAttributeForUpdateAsync(forResins.Id, CancellationToken.None))!;
        var off = current.Value with { IsActive = false };
        await Records.UpdateAttributeAsync(off, HandlerFixture.Now, CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() => SetAsync(forResins.Id, "0.03", set.Version));
        await SetAsync(forResins.Id, null, set.Version); // clearing is always allowed

        var records = await new GetLotRecordsHandler(Records).HandleAsync(new GetLotRecordsQuery(_lot.Id), CancellationToken.None);
        Assert.Empty(records.Attributes); // not offered, and nothing recorded
    }

    [Fact]
    public async Task A_document_is_a_recognised_file_or_a_reference_and_is_withdrawn_not_deleted()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2];
        var file = await AttachAsync("../../etc/scan.png", png);
        var stored = Records.Document(file.Id);
        Assert.Equal(("scan.png", "image/png", 10, "qa"), (stored.FileName, stored.ContentType, stored.SizeBytes, stored.CreatedBy));
        Assert.Equal(64, stored.Sha256!.Length);

        var invalid = await Assert.ThrowsAsync<ValidationException>(() => AttachAsync("x.pdf", [0x4D, 0x5A, 0, 0]));
        Assert.Contains("File", invalid.Errors.Keys);
        await Assert.ThrowsAsync<ValidationException>(() => AttachAsync(null, null, url: "ftp://files.example/coa"));
        await Assert.ThrowsAsync<ValidationException>(() => AttachAsync(null, null));
        var reference = await AttachAsync(null, null, number: "C-881");
        Assert.False(Records.Document(reference.Id).HasFile);

        var withdraw = new WithdrawLotDocumentHandler(_f.Stores.UnitOfWork, Records, _f.Stores.Events, _f.Events, new FixedRequestContext("qa"), _f.Time);
        await withdraw.HandleAsync(new WithdrawLotDocumentCommand(file.Id, file.Version, "Superseded by rev 2"), CancellationToken.None);
        Assert.Equal((LotDocument.Withdrawn, "Superseded by rev 2"), (Records.Document(file.Id).Status, Records.Document(file.Id).WithdrawReason));
        Assert.Equal(png, Records.ContentOf(file.Id));
        await Assert.ThrowsAsync<ConflictException>(() => withdraw.HandleAsync(new WithdrawLotDocumentCommand(file.Id, file.Version, "Again"), CancellationToken.None));
        Assert.Equal(EventTypes.LotDocumentWithdrawn, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public void Files_are_recognised_by_their_first_bytes_not_their_names()
    {
        Assert.Equal("application/pdf", LotDocumentFiles.Sniff("%PDF-1.7"u8));
        Assert.Equal("image/jpeg", LotDocumentFiles.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(LotDocumentFiles.Sniff("<html>"u8));
        Assert.Null(LotDocumentFiles.Sniff([]));
    }
}
