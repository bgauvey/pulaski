using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Labor;

/// <summary>
/// The labor gaps of #84 (D-095): other sites people work at, planned breaks, more kinds of time at terminals and qualification expiry
/// notices. The fixture's clock is Monday 2026-10-12 14:00 UTC.
/// </summary>
public class LaborGapsTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private readonly HandlerFixture _f = new();
    private readonly Guid _home = Guid.CreateVersion7();
    private readonly Guid _other = Guid.CreateVersion7();
    private readonly Guid _homeLine;
    private readonly Guid _otherLine;

    public LaborGapsTests()
    {
        Labor.AddSite(_home);
        Labor.AddSite(_other);
        _homeLine = Labor.AddLine(_home, "EXT-01");
        _otherLine = Labor.AddLine(_other, "PACK-9");
    }

    private InMemoryLabor Labor => _f.Stores.Labor;

    private ClockOnHandler ClockOn() => new(_f.Stores.UnitOfWork, Labor, new FixedRequestContext("sam", Guid.CreateVersion7()), _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private SetPersonSitesHandler SetSites() => new(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Time);

    [Fact]
    public async Task Someone_works_at_another_site_only_once_listed_there_and_that_time_belongs_to_it()
    {
        var ann = Labor.AddPerson(_home, "Ann");
        var input = new LaborInput(_otherLine, null, LaborTypes.Direct, null, 100, null, null);

        Assert.Contains("doesn't work at that site", (await Assert.ThrowsAsync<DomainRuleException>(() =>
            ClockOn().HandleAsync(new ClockOnCommand(ann, input), CancellationToken.None))).Message, StringComparison.Ordinal);

        await SetSites().HandleAsync(new SetPersonSitesCommand(ann, [_other, _home]), CancellationToken.None);
        var on = await ClockOn().HandleAsync(new ClockOnCommand(ann, input), CancellationToken.None);
        Assert.Equal(_other, Labor.Session(on.Id).Value.SiteId);
        Assert.Single(_f.Stores.Events.Appended, e => e.Type == EventTypes.PersonSitesChanged);

        // Listed there, with their own site named; their sessions anywhere are theirs to see.
        var there = Assert.Single(await Labor.ListPeopleAsync(_other, CancellationToken.None));
        Assert.Equal((ann, "HOME"), (there.Id, there.HomeSiteCode));
        Assert.Single(await Labor.ListOpenSessionsAsync(_home, ann, CancellationToken.None));

        // A site they are clocked on at can't be taken away.
        await Assert.ThrowsAsync<DomainRuleException>(() => SetSites().HandleAsync(new SetPersonSitesCommand(ann, []), CancellationToken.None));
    }

    [Fact]
    public void Planned_breaks_fall_at_their_clock_time_inside_each_shift_and_must_fit_it()
    {
        var night = new ShiftDefinition("N", "Nights", new TimeOnly(22, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4, 5, 6], null,
            [new PlannedBreak("Meal", new TimeOnly(2, 0), TimeSpan.FromMinutes(30), IsPaid: false)]);
        var schedule = new ShiftSchedule("NIGHT", "Nights", 7, new DateOnly(2026, 10, 12), [night]);

        var shift = schedule.Expand(Utc, new DateTimeOffset(2026, 10, 12, 23, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 13, 1, 0, 0, TimeSpan.Zero))
            .Single(s => s.ProductionDate == new DateOnly(2026, 10, 12));
        var meal = Assert.Single(shift.Breaks);
        Assert.Equal((new DateTimeOffset(2026, 10, 13, 2, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 13, 2, 30, 0, TimeSpan.Zero)), (meal.Start, meal.End));
        Assert.True(meal.IsNear(meal.Start.AddMinutes(-10), TimeSpan.FromMinutes(10)));
        Assert.Empty(schedule.Problems());

        var outside = schedule with { Shifts = [night with { Breaks = [new PlannedBreak("Late", new TimeOnly(7, 0), TimeSpan.FromMinutes(30), false)] }] };
        Assert.Contains(outside.Problems(), p => p.Contains("isn't inside the shift", StringComparison.Ordinal));
        var clash = schedule with
        {
            Shifts = [night with { Breaks = [new PlannedBreak("A", new TimeOnly(2, 0), TimeSpan.FromMinutes(30), false), new PlannedBreak("B", new TimeOnly(2, 15), TimeSpan.FromMinutes(15), false)] }],
        };
        Assert.Contains(clash.Problems(), p => p.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void A_planned_break_is_missed_by_whoever_was_on_all_through_it_without_one()
    {
        var lunch = new PlannedBreakWindow("Lunch", new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 12, 12, 30, 0, TimeSpan.Zero), false);
        var (ann, bob, cy) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var day = new DateTimeOffset(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        var sessions = new[] { (ann, day, (DateTimeOffset?)day.AddHours(8)), (bob, day, day.AddHours(8)), (cy, day.AddHours(7), day.AddHours(8)) };
        var breaks = new[] { new BreakSpan(bob, lunch.Start.AddMinutes(5), lunch.Start.AddMinutes(35), false) };

        var missed = PlannedBreakCheck.Missed(sessions, breaks, [lunch], day.AddHours(9));
        Assert.Equal([(ann, "Lunch")], missed);
        Assert.Empty(PlannedBreakCheck.Missed(sessions, breaks, [lunch], lunch.Start.AddMinutes(10)));
    }

    [Fact]
    public async Task At_a_terminal_setup_goes_to_the_lines_job_and_indirect_time_needs_a_reason()
    {
        var (_, site, line) = _f.Stores.Equipment.AddHierarchy();
        line = line with { CanRunJob = true };
        _f.Stores.Equipment.Put(line, 1);
        Labor.AddSite(site.Id);
        Labor.AddLineWithId(line.Id, site.Id, line.Code);
        var terminal = new TerminalRecord(Guid.CreateVersion7(), site.Id, "T1", "Terminal", line.Id, line.Id, TerminalAuthModes.Badge, true, HandlerFixture.Now);
        var ann = Labor.AddPerson(site.Id, "Ann");
        var clockOn = new TerminalClockOnHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Equipment, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        await Assert.ThrowsAsync<DomainRuleException>(() => clockOn.HandleAsync(new TerminalClockOnCommand(terminal, ann, line.Id, LaborTypes.Setup), CancellationToken.None));
        var job = Labor.AddJob(site.Id, "J-1", line.Id);
        var setup = await clockOn.HandleAsync(new TerminalClockOnCommand(terminal, ann, line.Id, LaborTypes.Setup), CancellationToken.None);
        Assert.Equal((LaborTypes.Setup, (Guid?)job), (Labor.Session(setup.Id).Value.LaborType, Labor.Session(setup.Id).Value.JobId));

        var bob = Labor.AddPerson(site.Id, "Bob");
        await Assert.ThrowsAsync<ValidationException>(() => clockOn.HandleAsync(new TerminalClockOnCommand(terminal, bob, line.Id, LaborTypes.Indirect), CancellationToken.None));
        var reason = Labor.AddReason(site.Id);
        var indirect = await clockOn.HandleAsync(new TerminalClockOnCommand(terminal, bob, line.Id, LaborTypes.Indirect, reason), CancellationToken.None);
        Assert.Equal(reason, Labor.Session(indirect.Id).Value.ReasonCodeId);
    }

    [Fact]
    public async Task An_expiry_notice_goes_once_to_the_person_and_the_sites_people_managers_and_not_for_a_renewed_grant()
    {
        var mail = new FakeMailOutbox();
        var notices = new QualificationExpiryNotices(_f.Stores.UnitOfWork, Labor, mail, _f.Stores.Events, _f.Events, _f.Time);
        var fork = new QualificationRecord(Guid.CreateVersion7(), _home, "FORK", "Forklift", null, 12, true, NoticeDays: 14);
        await Labor.InsertQualificationAsync(fork, HandlerFixture.Now, CancellationToken.None);
        var (ann, bob) = (Labor.AddPerson(_home, "Ann"), Labor.AddPerson(_home, "Bob"));
        Labor.PersonEmails[ann] = "ann@example.com";
        Labor.PeopleManagers[_home] = ["lead@example.com"];
        async Task Grant(Guid person, DateOnly expires) => await Labor.InsertGrantAsync(
            new GrantRecord(Guid.CreateVersion7(), person, fork.Id, new DateOnly(2025, 10, 1), expires, null, "test", null, null, null), HandlerFixture.Now, CancellationToken.None);

        await Grant(ann, new DateOnly(2026, 10, 20));
        await Grant(bob, new DateOnly(2026, 10, 20));
        await Grant(bob, new DateOnly(2027, 10, 19));
        await Grant(Labor.AddPerson(_home, "Cy"), new DateOnly(2026, 12, 1));

        // No mail server: nothing sent, nothing marked.
        mail.CanSend = false;
        Assert.Equal(0, await notices.SendDueAsync(CancellationToken.None));
        mail.CanSend = true;

        Assert.Equal(1, await notices.SendDueAsync(CancellationToken.None));
        Assert.Equal(["ann@example.com", "lead@example.com"], mail.Sent.Select(m => m.Recipient));
        Assert.Contains("Ann: FORK Forklift, expires 2026-10-20", mail.Sent[1].Body, StringComparison.Ordinal);
        Assert.Equal(0, await notices.SendDueAsync(CancellationToken.None));
        Assert.Equal(EventTypes.QualificationExpiryNoticed, _f.Stores.Events.Appended[^1].Type);
    }
}
