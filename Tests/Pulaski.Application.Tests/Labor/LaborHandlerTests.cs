using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Labor;

/// <summary>Recording, approving and correcting labor (D-040). The fixture's clock is Monday 2026-10-12 14:00 UTC.</summary>
public class LaborHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _line;
    private readonly Guid _schedule;
    private readonly Guid _userId = Guid.CreateVersion7();

    public LaborHandlerTests()
    {
        Labor.AddSite(_site);
        _schedule = Labor.AddSchedule(new ShiftSchedule("2X8", "Two shifts", 7, new DateOnly(2026, 10, 12),
        [
            new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4], "RED"),
            new("B", "Lates", new TimeOnly(14, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4], "BLUE"),
        ]));
        _line = Labor.AddLine(_site, "EXT-01", _schedule);
    }

    private InMemoryLabor Labor => _f.Stores.Labor;

    private static FixedRequestContext Supervisor => new("sam", Guid.CreateVersion7());

    private ClockOnHandler ClockOn(IRequestContext? as_ = null) => new(_f.Stores.UnitOfWork, Labor, as_ ?? Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private ClockOffHandler ClockOff(IRequestContext? as_ = null) => new(_f.Stores.UnitOfWork, Labor, as_ ?? Supervisor, _f.Stores.Events, _f.Events, _f.Time);

    private ApproveShiftHandler Approve() => new(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private static LaborInput Direct(Guid line, DateTimeOffset? at = null, decimal pct = 100) => new(line, null, LaborTypes.Direct, null, pct, at, null);

    [Fact]
    public async Task Someone_clocked_on_belongs_to_the_shift_running_then()
    {
        var person = Labor.AddPerson(_site, "Oli", userId: _userId);

        var result = await ClockOn(new FixedRequestContext("oli", _userId)).HandleAsync(new ClockOnCommand(null, Direct(_line)), CancellationToken.None);

        var session = Labor.Session(result.Id).Value;
        Assert.Equal((person, "B", (Guid?)_schedule, new DateOnly(2026, 10, 12), LaborSources.Self),
            (session.PersonId, session.ShiftCode, session.ShiftScheduleId, session.ProductionDate, session.Source));
        Assert.Equal(EventTypes.LaborStarted, _f.Stores.Events.Appended[^1].Type);

        // Not twice on the same line, and not beyond all their time elsewhere.
        await Assert.ThrowsAsync<DomainRuleException>(() => ClockOn().HandleAsync(new ClockOnCommand(person, Direct(_line)), CancellationToken.None));
        var other = Labor.AddLine(_site, "EXT-02");
        await Assert.ThrowsAsync<DomainRuleException>(() => ClockOn().HandleAsync(new ClockOnCommand(person, Direct(other, pct: 50)), CancellationToken.None));
    }

    [Fact]
    public async Task Someone_who_isnt_a_person_cant_clock_on_and_only_ends_their_own_time()
    {
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            ClockOn(new FixedRequestContext("nobody", Guid.CreateVersion7())).HandleAsync(new ClockOnCommand(null, Direct(_line)), CancellationToken.None));

        var someoneElse = Labor.AddPerson(_site, "Pat");
        var started = await ClockOn().HandleAsync(new ClockOnCommand(someoneElse, Direct(_line)), CancellationToken.None);
        Labor.AddPerson(_site, "Oli", userId: _userId);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            ClockOff(new FixedRequestContext("oli", _userId)).HandleAsync(new ClockOffCommand(started.Id, started.Version, null, OwnOnly: true), CancellationToken.None));

        var ended = await ClockOff().HandleAsync(new ClockOffCommand(started.Id, started.Version, null, OwnOnly: false), CancellationToken.None);
        Assert.Equal(HandlerFixture.Now, Labor.Session(ended.Id).Value.EndedAt);
    }

    [Fact]
    public async Task A_crew_is_checked_in_at_once_and_checked_out_at_once()
    {
        var a = Labor.AddPerson(_site, "Ann");
        var b = Labor.AddPerson(_site, "Bob");
        var checkIn = new CheckInHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

        var first = await checkIn.HandleAsync(new CheckInCommand(_line, [a, b], null), CancellationToken.None);
        Assert.Equal("Checked 2 people in.", first.Notes[0]);
        Assert.All(Labor.Sessions(), s => Assert.Equal(LaborSources.Crew, s.Source));

        var again = await checkIn.HandleAsync(new CheckInCommand(_line, [a], null), CancellationToken.None);
        Assert.Contains("Ann was already on.", again.Notes);

        var checkOut = new CheckOutHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Time);
        await checkOut.HandleAsync(new CheckOutCommand(_line, null), CancellationToken.None);
        Assert.All(Labor.Sessions(), s => Assert.NotNull(s.EndedAt));
    }

    [Fact]
    public async Task Approving_a_shift_ends_its_open_time_at_the_shift_end_and_locks_it()
    {
        // Shift A ran 06:00-14:00; it is 14:00 now.
        var person = Labor.AddPerson(_site, "Ann");
        var started = await ClockOn().HandleAsync(new ClockOnCommand(person, Direct(_line, HandlerFixture.Now.AddHours(-7))), CancellationToken.None);
        var key = Labor.Session(started.Id).Value.Shift;
        Assert.Equal("A", key.ShiftCode);

        var tooEarly = new ShiftKey(_site, new DateOnly(2026, 10, 12), _schedule, "B");
        await Assert.ThrowsAsync<DomainRuleException>(() => Approve().HandleAsync(new ApproveShiftCommand(tooEarly, null), CancellationToken.None));

        var approved = await Approve().HandleAsync(new ApproveShiftCommand(key, "Fine"), CancellationToken.None);
        Assert.Single(approved.Notes);
        Assert.Equal(HandlerFixture.Now, Labor.Session(started.Id).Value.EndedAt);
        await Assert.ThrowsAsync<ConflictException>(() => Approve().HandleAsync(new ApproveShiftCommand(key, null), CancellationToken.None));

        // Locked: no change, no new time in it; a correction replaces it and needs a reason.
        var session = Labor.Session(started.Id);
        var update = new UpdateLaborSessionHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Time);
        await Assert.ThrowsAsync<DomainRuleException>(() => update.HandleAsync(new UpdateLaborSessionCommand(started.Id, session.Version, _line, null, LaborTypes.Direct, null,
            session.Value.StartedAt, session.Value.EndedAt, 50, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            ClockOn().HandleAsync(new ClockOnCommand(Labor.AddPerson(_site, "Late"), Direct(_line, HandlerFixture.Now.AddHours(-3))), CancellationToken.None));

        var correct = new CorrectLaborSessionHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        await Assert.ThrowsAsync<ValidationException>(() => correct.HandleAsync(new CorrectLaborSessionCommand(started.Id, false, _line, null, LaborTypes.Direct, null,
            session.Value.StartedAt, session.Value.EndedAt!.Value, 50, ""), CancellationToken.None));
        var corrected = await correct.HandleAsync(new CorrectLaborSessionCommand(started.Id, false, _line, null, LaborTypes.Direct, null,
            session.Value.StartedAt, session.Value.EndedAt!.Value, 50, "Half the time on the other line"), CancellationToken.None);
        Assert.Equal((LaborSources.Correction, (Guid?)started.Id, 50m), (Labor.Session(corrected.Id).Value.Source, Labor.Session(corrected.Id).Value.CorrectsSessionId, Labor.Session(corrected.Id).Value.AllocationPct));
        await Assert.ThrowsAsync<DomainRuleException>(() => correct.HandleAsync(new CorrectLaborSessionCommand(started.Id, true, _line, null, LaborTypes.Direct, null,
            session.Value.StartedAt, session.Value.EndedAt!.Value, 100, "Again"), CancellationToken.None));
    }

    [Fact]
    public async Task The_report_counts_corrections_instead_of_what_they_replace()
    {
        var person = Labor.AddPerson(_site, "Ann");
        var job = Labor.AddJob(_site, "WO-1-10", _line);
        Labor.AddRun(new RunRow(Guid.CreateVersion7(), job, "WO-1-10", "PRF", _line, "EXT-01", HandlerFixture.Now.AddHours(-8), HandlerFixture.Now.AddHours(-4), 2));
        var started = await ClockOn().HandleAsync(new ClockOnCommand(person, Direct(_line, HandlerFixture.Now.AddHours(-8))), CancellationToken.None);
        await ClockOff().HandleAsync(new ClockOffCommand(started.Id, Labor.Session(started.Id).Version, HandlerFixture.Now.AddHours(-2), false), CancellationToken.None);

        var report = new GetLaborReportHandler(Labor, _f.Time);
        var before = await report.HandleAsync(new GetLaborReportQuery(_site, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12)), CancellationToken.None);
        var row = Assert.Single(before.Jobs);
        Assert.Equal((4m, 4m, 8m, 200m), (row.RunHours, row.DirectHours, row.StandardHours!.Value, row.EfficiencyPct!.Value));
        Assert.Equal((6m, 2m), (Assert.Single(before.People).TotalHours, before.People[0].IdleHours));

        await Approve().HandleAsync(new ApproveShiftCommand(Labor.Session(started.Id).Value.Shift, null), CancellationToken.None);
        var correct = new CorrectLaborSessionHandler(_f.Stores.UnitOfWork, Labor, Supervisor, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);
        await correct.HandleAsync(new CorrectLaborSessionCommand(started.Id, true, _line, null, LaborTypes.Direct, null, default, default, 100, "Wasn't here"), CancellationToken.None);

        var after = await report.HandleAsync(new GetLaborReportQuery(_site, new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12)), CancellationToken.None);
        Assert.Empty(after.Jobs);
        Assert.Empty(after.People);
        Assert.True(Assert.Single(after.Shifts).IsApproved);
    }
}
