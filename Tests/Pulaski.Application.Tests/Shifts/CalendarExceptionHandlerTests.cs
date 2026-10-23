using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Shifts;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Shifts;

/// <summary>Maintaining calendar exceptions: holidays and shutdowns taken out of a site's calendar (D-043).</summary>
public class CalendarExceptionHandlerTests
{
    private static readonly DateTimeOffset Christmas = new(2026, 12, 25, 6, 0, 0, TimeSpan.Zero);

    private readonly HandlerFixture _f = new();
    private readonly Equipment _enterprise;
    private readonly Equipment _site;
    private readonly Equipment _line;

    public CalendarExceptionHandlerTests()
    {
        (_enterprise, _site, _line) = _f.Stores.Equipment.AddHierarchy();
        _f.Stores.CalendarExceptions.SiteOf = id => _f.Stores.Equipment.Find(id)?.Value.SiteId;
    }

    private CreateCalendarExceptionHandler Create() =>
        new(_f.Stores.UnitOfWork, _f.Stores.CalendarExceptions, _f.Stores.Equipment, _f.Stores.Events, _f.Events, _f.Ids);

    private UpdateCalendarExceptionHandler Update() => new(_f.Stores.UnitOfWork, _f.Stores.CalendarExceptions, _f.Stores.Equipment, _f.Stores.Events, _f.Events);

    private Task<Contracts.Common.CreatedResponse> AddAsync(Guid scope, string kind = CalendarExceptionKind.Holiday, int hours = 24) =>
        Create().HandleAsync(new CreateCalendarExceptionCommand(scope, kind, Christmas, Christmas.AddHours(hours), "  Christmas  "), CancellationToken.None);

    [Fact]
    public async Task An_exception_is_saved_on_a_site_or_anything_in_it_and_listed_by_site()
    {
        var site = await AddAsync(_site.Id);
        var line = await AddAsync(_line.Id, CalendarExceptionKind.PlannedShutdown, hours: 48);

        var saved = _f.Stores.CalendarExceptions.Get(site.Id)!.Value;
        Assert.Equal((CalendarExceptionKind.Holiday, "Christmas", true), (saved.Kind, saved.Note, saved.IsActive));
        Assert.Equal(EventTypes.CalendarExceptionCreated, _f.Stores.Events.Appended[^1].Type);

        var listed = await new ListCalendarExceptionsHandler(_f.Stores.CalendarExceptions)
            .HandleAsync(new ListCalendarExceptionsQuery(_site.Id, Christmas.AddHours(30), IncludeRemoved: false), CancellationToken.None);
        Assert.Equal([line.Id], listed.Select(x => x.Id));
    }

    [Fact]
    public async Task Removing_switches_it_off_and_it_can_be_put_back()
    {
        var created = await AddAsync(_site.Id);
        UpdateCalendarExceptionCommand Change(long version, bool active, int hours = 24) =>
            new(created.Id, version, CalendarExceptionKind.Holiday, Christmas, Christmas.AddHours(hours), "Christmas", active);

        var removed = await Update().HandleAsync(Change(created.Version, active: false), CancellationToken.None);
        Assert.False(_f.Stores.CalendarExceptions.Get(created.Id)!.Value.IsActive);
        Assert.Equal(EventTypes.CalendarExceptionRemoved, _f.Stores.Events.Appended[^1].Type);

        var back = await Update().HandleAsync(Change(removed.Version, active: true, hours: 36), CancellationToken.None);
        Assert.Equal(Christmas.AddHours(36), _f.Stores.CalendarExceptions.Get(created.Id)!.Value.EndsAt);
        Assert.Equal(EventTypes.CalendarExceptionUpdated, _f.Stores.Events.Appended[^1].Type);

        // Unchanged saves nothing; a stale version is refused.
        Assert.Equal(back.Version, (await Update().HandleAsync(Change(back.Version, active: true, hours: 36), CancellationToken.None)).Version);
        await Assert.ThrowsAsync<ConflictException>(() => Update().HandleAsync(Change(created.Version, active: true), CancellationToken.None));
    }

    [Fact]
    public async Task Bad_exceptions_are_refused()
    {
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(_enterprise.Id));
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(Guid.CreateVersion7()));
        await Assert.ThrowsAsync<ValidationException>(() => AddAsync(_site.Id, "vacation"));
        await Assert.ThrowsAsync<ValidationException>(() => AddAsync(_site.Id, hours: 0));
        await Assert.ThrowsAsync<ValidationException>(() => AddAsync(_site.Id, hours: 24 * 367));

        _f.Stores.Equipment.Put(_line with { IsActive = false }, version: 2);
        await Assert.ThrowsAsync<DomainRuleException>(() => AddAsync(_line.Id));
    }
}
