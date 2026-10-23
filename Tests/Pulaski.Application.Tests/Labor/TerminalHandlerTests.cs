using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Domain.Labor;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Labor;

/// <summary>Shop-floor terminals and badges (D-049). The fixture's clock is Monday 2026-10-12 14:00 UTC.</summary>
public class TerminalHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Equipment _site;
    private readonly Equipment _line;
    private readonly Equipment _otherLine;

    public TerminalHandlerTests()
    {
        (_, _site, _line) = _f.Stores.Equipment.AddHierarchy();
        _line = _line with { CanRunJob = true };
        _f.Stores.Equipment.Put(_line, 1);
        _otherLine = Equipment.Create(Guid.CreateVersion7(), _site, "EXT-02", "Line 2", null, "work_center", "production_line", true, true, false, null) with { CanRunJob = true };
        _f.Stores.Equipment.Put(_otherLine, 1);

        // Labor knows the site and the line the terminal offers; the other line is outside its scope.
        Labor.AddSite(_site.Id);
        Labor.AddLineWithId(_line.Id, _site.Id, _line.Code);
        Labor.AddLineWithId(_otherLine.Id, _site.Id, _otherLine.Code);
    }

    private InMemoryLabor Labor => _f.Stores.Labor;

    private InMemoryTerminals Terminals => _f.Stores.Terminals;

    private static FixedRequestContext Manager => new("mia", Guid.CreateVersion7());

    private async Task<TerminalRecord> TerminalAsync(string mode = TerminalAuthModes.Badge)
    {
        // Scoped to the line itself, so the other line is outside it.
        var created = await new CreateTerminalHandler(_f.Stores.UnitOfWork, Terminals, _f.Stores.Equipment, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateTerminalCommand("t1", "Line 1 terminal", _line.Id, _line.Id, mode), CancellationToken.None);
        return Terminals.Terminal(created.Id).Value;
    }

    private async Task<Guid> BadgeAsync(Guid person, string value, string? pin = null) =>
        (await new IssueBadgeHandler(_f.Stores.UnitOfWork, Terminals, Labor, new ReversingBadgeHasher(), new PlainPasswordHasher(), Manager, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new IssueBadgeCommand(person, value, pin), CancellationToken.None)).Id;

    private IdentifyBadgeHandler Identify() =>
        new(_f.Stores.UnitOfWork, Terminals, Labor, new ReversingBadgeHasher(), new PlainPasswordHasher(), _f.Stores.Events, _f.Events, _f.Time);

    [Fact]
    public async Task A_terminal_is_enrolled_once_with_a_code_that_runs_out()
    {
        var terminal = await TerminalAsync();
        Assert.Equal(("T1", _site.Id, "terminal:T1"), (terminal.Code, terminal.SiteId, terminal.ActorId));

        var issue = new IssueEnrolmentCodeHandler(_f.Stores.UnitOfWork, Terminals, _f.Stores.Events, _f.Events, _f.Time);
        var code = await issue.HandleAsync(new IssueEnrolmentCodeCommand(terminal.Id), CancellationToken.None);
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", code.Code);
        Assert.Equal(HandlerFixture.Now.AddMinutes(15), code.ExpiresAt);

        var enrol = new EnrolTerminalHandler(_f.Stores.UnitOfWork, Terminals, _f.Stores.Events, _f.Events, _f.Time);
        var enrolled = await enrol.HandleAsync(new EnrolTerminalCommand(" " + code.Code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)), CancellationToken.None);
        Assert.Equal(terminal.Id, enrolled.TerminalId);
        Assert.Equal(terminal.Id, (await Terminals.FindBySecretAsync(TerminalRules.Sha256(enrolled.Secret), CancellationToken.None))!.Id);

        // Used up; and a fresh code that runs out before it is typed doesn't work either.
        await Assert.ThrowsAsync<DomainRuleException>(() => enrol.HandleAsync(new EnrolTerminalCommand(code.Code), CancellationToken.None));
        var late = await issue.HandleAsync(new IssueEnrolmentCodeCommand(terminal.Id), CancellationToken.None);
        _f.Time.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<DomainRuleException>(() => enrol.HandleAsync(new EnrolTerminalCommand(late.Code), CancellationToken.None));

        // Switched off, its secret stops working.
        var update = new UpdateTerminalHandler(_f.Stores.UnitOfWork, Terminals, _f.Stores.Equipment, _f.Stores.Events, _f.Events);
        await update.HandleAsync(new UpdateTerminalCommand(terminal.Id, Terminals.Terminal(terminal.Id).Version, "Line 1", _line.Id, null, TerminalAuthModes.Badge, false), CancellationToken.None);
        Assert.Null(await Terminals.FindBySecretAsync(TerminalRules.Sha256(enrolled.Secret), CancellationToken.None));
    }

    [Fact]
    public async Task A_badge_identifies_its_person_and_wrong_pins_lock_it()
    {
        var terminal = await TerminalAsync(TerminalAuthModes.BadgePlusPin);
        var ann = Labor.AddPerson(_site.Id, "Ann");
        await BadgeAsync(ann, " abc123 ", "4321");

        Assert.Equal(new BadgeIdentity(true, null), await Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "ABC123", null), CancellationToken.None));
        Assert.Equal(new BadgeIdentity(false, ann), await Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "abc123", "4321"), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "nope99", "4321"), CancellationToken.None));
        Assert.Equal(HandlerFixture.Now, Terminals.LastSeen(terminal.Id));

        for (var i = 0; i < TerminalRules.MaxFailedPins; i++)
        {
            await Assert.ThrowsAsync<DomainRuleException>(() => Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "ABC123", "0000"), CancellationToken.None));
        }

        var locked = await Assert.ThrowsAsync<DomainRuleException>(() => Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "ABC123", "4321"), CancellationToken.None));
        Assert.Contains("locked", locked.Message, StringComparison.Ordinal);
        Assert.Contains(_f.Stores.Events.Appended, e => e.Type == EventTypes.BadgePinFailed);

        _f.Time.Advance(TerminalRules.PinLockout);
        Assert.Equal(ann, (await Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "ABC123", "4321"), CancellationToken.None)).PersonId);
        Assert.Equal(0, Terminals.Badges().Single(b => b.RevokedAt is null).FailedPins);
    }

    [Fact]
    public async Task A_new_badge_replaces_the_old_and_a_badge_is_only_ones_own()
    {
        var terminal = await TerminalAsync();
        var ann = Labor.AddPerson(_site.Id, "Ann");
        var bob = Labor.AddPerson(_site.Id, "Bob");
        var first = await BadgeAsync(ann, "AAAA1");
        await BadgeAsync(ann, "AAAA2");

        Assert.NotNull(Terminals.Badge(first).RevokedAt);
        await Assert.ThrowsAsync<DomainRuleException>(() => Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "AAAA1", null), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => BadgeAsync(bob, "aaaa2"));
        await Assert.ThrowsAsync<ValidationException>(() => BadgeAsync(bob, "BBBB1", "12"));

        // A badge of another site's person isn't recognised here.
        var elsewhere = Guid.CreateVersion7();
        Labor.AddSite(elsewhere);
        await BadgeAsync(Labor.AddPerson(elsewhere, "Cy"), "CCCC1");
        await Assert.ThrowsAsync<DomainRuleException>(() => Identify().HandleAsync(new IdentifyBadgeCommand(terminal, "CCCC1", null), CancellationToken.None));
    }

    [Fact]
    public async Task Time_recorded_at_a_terminal_is_the_persons_own_on_the_lines_it_offers()
    {
        var terminal = await TerminalAsync();
        var ann = Labor.AddPerson(_site.Id, "Ann");
        var clockOn = new TerminalClockOnHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Equipment, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        var on = await clockOn.HandleAsync(new TerminalClockOnCommand(terminal, ann, _line.Id), CancellationToken.None);
        var session = Labor.Session(on.Id).Value;
        Assert.Equal((LaborSources.Terminal, "terminal:T1", ann), (session.Source, session.RecordedBy, session.PersonId));
        await Assert.ThrowsAsync<ForbiddenException>(() => clockOn.HandleAsync(new TerminalClockOnCommand(terminal, ann, _otherLine.Id), CancellationToken.None));

        // Breaks, then clocking off; someone else's time can't be ended here.
        await new TerminalStartBreakHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new TerminalStartBreakCommand(terminal, ann, IsPaid: true), CancellationToken.None);
        Assert.Equal((LaborSources.Terminal, true), (Labor.Breaks()[0].Source, Labor.Breaks()[0].IsPaid));
        _f.Time.Advance(TimeSpan.FromMinutes(10));
        await new TerminalEndBreakHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new TerminalEndBreakCommand(terminal, ann), CancellationToken.None);
        Assert.Equal(HandlerFixture.Now.AddMinutes(10), Labor.Breaks()[0].EndedAt);

        var clockOff = new TerminalClockOffHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Time);
        var bob = Labor.AddPerson(_site.Id, "Bob");
        await Assert.ThrowsAsync<ForbiddenException>(() => clockOff.HandleAsync(new TerminalClockOffCommand(terminal, bob, on.Id), CancellationToken.None));
        await clockOff.HandleAsync(new TerminalClockOffCommand(terminal, ann, on.Id), CancellationToken.None);
        Assert.Equal(HandlerFixture.Now.AddMinutes(10), Labor.Session(on.Id).Value.EndedAt);
        await Assert.ThrowsAsync<DomainRuleException>(() => clockOff.HandleAsync(new TerminalClockOffCommand(terminal, ann, on.Id), CancellationToken.None));
    }

    [Fact]
    public async Task A_terminal_belongs_to_a_site_and_its_default_line_is_one_it_offers()
    {
        var create = new CreateTerminalHandler(_f.Stores.UnitOfWork, Terminals, _f.Stores.Equipment, _f.Stores.Events, _f.Events, _f.Ids);
        var enterprise = _f.Stores.Equipment.Find(_site.ParentId!.Value)!.Value;
        await Assert.ThrowsAsync<DomainRuleException>(() => create.HandleAsync(new CreateTerminalCommand("X", "X", enterprise.Id, null, TerminalAuthModes.Badge), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => create.HandleAsync(new CreateTerminalCommand("X", "X", _line.Id, _otherLine.Id, TerminalAuthModes.Badge), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => create.HandleAsync(new CreateTerminalCommand("X", "X", _site.Id, null, "user_login"), CancellationToken.None));

        await create.HandleAsync(new CreateTerminalCommand("hall", "Hall", _site.Id, _otherLine.Id, TerminalAuthModes.Badge), CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => create.HandleAsync(new CreateTerminalCommand("HALL", "Again", _site.Id, null, TerminalAuthModes.Badge), CancellationToken.None));
    }
}
