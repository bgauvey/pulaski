using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Labor;

/// <summary>Breaks (D-047) and qualifications (D-048). The fixture's clock is Monday 2026-10-12 14:00 UTC; shift A ran 06:00–14:00.</summary>
public class BreakAndQualificationHandlerTests
{
    private static readonly DateTimeOffset Now = HandlerFixture.Now;
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _area;
    private readonly Guid _line;
    private readonly Guid _schedule;

    public BreakAndQualificationHandlerTests()
    {
        Labor.AddSite(_site);
        _schedule = Labor.AddSchedule(new ShiftSchedule("2X8", "Two shifts", 7, new DateOnly(2026, 10, 12),
        [
            new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4]),
            new("B", "Lates", new TimeOnly(14, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4]),
        ]));
        _area = Labor.AddLine(_site, "EXTRUSION");
        _line = Labor.AddLine(_site, "EXT-01", _schedule);
        Labor.SetParent(_line, _area);
    }

    private InMemoryLabor Labor => _f.Stores.Labor;

    private static FixedRequestContext Supervisor => new("sam", Guid.CreateVersion7());

    private ClockOnHandler ClockOn() => new(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private StartBreakHandler StartBreak(IRequestContext? as_ = null) => new(_f.Stores.UnitOfWork, Labor, as_ ?? Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private EndBreakHandler EndBreak() => new(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Time);

    private async Task<Guid> OnLineAsync(Guid person, double hoursAgo)
    {
        var started = await ClockOn().HandleAsync(new ClockOnCommand(person, new LaborInput(_line, null, LaborTypes.Direct, null, 100, Now.AddHours(-hoursAgo), null)), CancellationToken.None);
        return started.Id;
    }

    [Fact]
    public async Task A_break_starts_on_time_on_a_line_comes_out_of_worked_hours_and_leaves_the_session_alone()
    {
        var ann = Labor.AddPerson(_site, "Ann");
        await Assert.ThrowsAsync<DomainRuleException>(() => StartBreak().HandleAsync(new StartBreakCommand(ann, false, null, null, null), CancellationToken.None));

        var session = await OnLineAsync(ann, 7);
        var lunch = await StartBreak().HandleAsync(new StartBreakCommand(ann, false, Now.AddHours(-4), Now.AddHours(-3.5), "Lunch"), CancellationToken.None);
        var recorded = Labor.Break(lunch.Id).Value;
        Assert.Equal(("A", LaborSources.Supervisor, false), (recorded.ShiftCode, recorded.Source, recorded.IsPaid));

        // One break at a time: overlapping is refused; a new one now is fine, and ending it works once.
        await Assert.ThrowsAsync<DomainRuleException>(() => StartBreak().HandleAsync(new StartBreakCommand(ann, true, Now.AddHours(-3.75), Now.AddHours(-3), null), CancellationToken.None));
        var coffee = await StartBreak().HandleAsync(new StartBreakCommand(ann, true, Now.AddMinutes(-10), null, null), CancellationToken.None);
        await EndBreak().HandleAsync(new EndBreakCommand(coffee.Id, coffee.Version, null, OwnOnly: false), CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() => EndBreak().HandleAsync(new EndBreakCommand(coffee.Id, Labor.Break(coffee.Id).Version, null, false), CancellationToken.None));

        var shift = await new GetShiftLaborHandler(Labor, _f.Time).HandleAsync(new GetShiftLaborQuery(Labor.Session(session).Value.Shift), CancellationToken.None);
        var worked = Assert.Single(shift.Sessions);
        Assert.Equal((Now.AddHours(-7), (DateTimeOffset?)null, 100m), (Labor.Session(session).Value.StartedAt, Labor.Session(session).Value.EndedAt, Labor.Session(session).Value.AllocationPct));
        Assert.Equal((6.33m, 0.67m), (worked.Hours, worked.BreakHours));
        Assert.Equal((0.17m, 0.5m, 6.33m), (shift.Shift.PaidBreakHours, shift.Shift.UnpaidBreakHours, shift.Shift.Hours));
        Assert.Equal(2, shift.Breaks.Count);
    }

    [Fact]
    public async Task Leaving_the_last_line_ends_the_break_and_approval_locks_breaks_until_corrected()
    {
        var ann = Labor.AddPerson(_site, "Ann");
        var session = await OnLineAsync(ann, 7);
        var open = await StartBreak().HandleAsync(new StartBreakCommand(ann, false, Now.AddHours(-1), null, null), CancellationToken.None);

        var clockOff = new ClockOffHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Time);
        await clockOff.HandleAsync(new ClockOffCommand(session, Labor.Session(session).Version, Now.AddMinutes(-30), OwnOnly: false), CancellationToken.None);
        Assert.Equal(Now.AddMinutes(-30), Labor.Break(open.Id).Value.EndedAt);

        var key = Labor.Session(session).Value.Shift;
        await new ApproveShiftHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ApproveShiftCommand(key, null), CancellationToken.None);

        var update = new UpdateBreakHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Time);
        await Assert.ThrowsAsync<DomainRuleException>(() => update.HandleAsync(
            new UpdateBreakCommand(open.Id, Labor.Break(open.Id).Version, Now.AddHours(-1), Now.AddMinutes(-45), false, null), CancellationToken.None));

        var correct = new CorrectBreakHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        await Assert.ThrowsAsync<ValidationException>(() => correct.HandleAsync(new CorrectBreakCommand(open.Id, true, Now, Now, false, " "), CancellationToken.None));
        var voided = await correct.HandleAsync(new CorrectBreakCommand(open.Id, true, Now, Now, false, "Wasn't a break"), CancellationToken.None);
        Assert.Equal((true, (Guid?)open.Id), (Labor.Break(voided.Id).Value.IsVoid, Labor.Break(voided.Id).Value.CorrectsBreakId));

        // The voided break no longer counts: the session is worked in full.
        var shift = await new GetShiftLaborHandler(Labor, _f.Time).HandleAsync(new GetShiftLaborQuery(key), CancellationToken.None);
        Assert.Equal((6.5m, 0m), (shift.Sessions[0].Hours, shift.Sessions[0].BreakHours));
        await Assert.ThrowsAsync<DomainRuleException>(() => correct.HandleAsync(new CorrectBreakCommand(open.Id, true, Now, Now, false, "Again"), CancellationToken.None));
    }

    [Fact]
    public async Task Qualifications_are_granted_and_revoked_and_a_line_warns_who_lacks_them()
    {
        var create = new CreateQualificationHandler(_f.Stores.UnitOfWork, Labor, Labor, _f.Stores.Events, _f.Events, _f.Ids);
        var fork = await create.HandleAsync(new CreateQualificationCommand(_site, "fork", "Forklift", null, 12), CancellationToken.None);
        await Assert.ThrowsAsync<ConflictException>(() => create.HandleAsync(new CreateQualificationCommand(_site, "FORK", "Again", null, null), CancellationToken.None));

        // The area requires it, so its lines do.
        await Labor.SetRequirementAsync(_area, fork.Id, true, Now, CancellationToken.None);

        var ann = Labor.AddPerson(_site, "Ann");
        var bob = Labor.AddPerson(_site, "Bob");
        var grant = new GrantQualificationHandler(_f.Stores.UnitOfWork, Labor, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        var granted = await grant.HandleAsync(new GrantQualificationCommand(ann, fork.Id, null, null, "Course 41"), CancellationToken.None);
        Assert.Equal((new DateOnly(2026, 10, 12), (DateOnly?)new DateOnly(2027, 10, 11)), (Labor.Grant(granted.Id).Value.ValidFrom, Labor.Grant(granted.Id).Value.ExpiresOn));
        await Assert.ThrowsAsync<ValidationException>(() =>
            grant.HandleAsync(new GrantQualificationCommand(bob, fork.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 1), null), CancellationToken.None));

        var checkIn = new CheckInHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        var result = await checkIn.HandleAsync(new CheckInCommand(_line, [ann, bob], null), CancellationToken.None);
        Assert.Equal(2, Labor.Sessions().Count);
        Assert.Contains(result.Notes, n => n == "Bob isn't qualified for EXT-01: FORK Forklift (not held).");
        Assert.DoesNotContain(result.Notes, n => n.StartsWith("Ann", StringComparison.Ordinal));

        var onNow = await new ListLineLaborHandler(Labor, _f.Time).HandleAsync(new ListLineLaborQuery(_site), CancellationToken.None);
        var sessions = onNow.Single(l => l.LineId == _line).Sessions;
        Assert.Equal(["FORK Forklift (not held)"], sessions.Single(s => s.PersonName == "Bob").MissingQualifications);
        Assert.Empty(sessions.Single(s => s.PersonName == "Ann").MissingQualifications);

        // Revoked, Ann lacks it too; the list says who holds it and where it is required.
        await new RevokeQualificationGrantHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Time)
            .HandleAsync(new RevokeQualificationGrantCommand(granted.Id, Labor.Grant(granted.Id).Version, "Incident"), CancellationToken.None);
        var grants = await new ListQualificationGrantsHandler(Labor, Labor, _f.Time).HandleAsync(new ListQualificationGrantsQuery(_site, ann, null, null), CancellationToken.None);
        Assert.Equal(GrantStatus.Revoked, Assert.Single(grants).Status);
        var listed = Assert.Single(await new ListQualificationsHandler(Labor, Labor, _f.Time).HandleAsync(new ListQualificationsQuery(_site), CancellationToken.None));
        Assert.Equal(("FORK", 0), (listed.Code, listed.Holders));
        Assert.Equal(["EXTRUSION"], listed.RequiredOn);

        var line = await new GetEquipmentQualificationsHandler(Labor).HandleAsync(new GetEquipmentQualificationsQuery(_line), CancellationToken.None);
        Assert.Empty(line.Own);
        Assert.Equal("EXTRUSION", Assert.Single(line.Inherited).FromEquipmentCode);
    }

    [Fact]
    public async Task Expiring_grants_are_listed_and_requirements_need_the_equipments_site()
    {
        var fork = await new CreateQualificationHandler(_f.Stores.UnitOfWork, Labor, Labor, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateQualificationCommand(_site, "FORK", "Forklift", null, null), CancellationToken.None);
        var ann = Labor.AddPerson(_site, "Ann");
        var grant = new GrantQualificationHandler(_f.Stores.UnitOfWork, Labor, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        await grant.HandleAsync(new GrantQualificationCommand(ann, fork.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 30), null), CancellationToken.None);

        var expiring = await new ListQualificationGrantsHandler(Labor, Labor, _f.Time).HandleAsync(new ListQualificationGrantsQuery(_site, null, null, 30), CancellationToken.None);
        Assert.Equal(GrantStatus.Valid, Assert.Single(expiring).Status);
        Assert.Empty(await new ListQualificationGrantsHandler(Labor, Labor, _f.Time).HandleAsync(new ListQualificationGrantsQuery(_site, null, null, 7), CancellationToken.None));

        var (_, otherSite, otherLine) = _f.Stores.Equipment.AddHierarchy();
        var set = new SetEquipmentQualificationHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Equipment, _f.Stores.Events, _f.Events);
        await Assert.ThrowsAsync<DomainRuleException>(() => set.HandleAsync(new SetEquipmentQualificationCommand(otherLine.Id, fork.Id, true), CancellationToken.None));
        Assert.NotEqual(_site, otherSite.Id);
    }
}
