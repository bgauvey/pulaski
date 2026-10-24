using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Crews, people and labor end to end (D-040): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class LaborTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6];

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task A_crew_on_shift_is_checked_in_its_time_is_approved_and_corrected()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "LENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "LS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "LL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));

        CreatedResponse red = await db.SendAsync(new CreateCrewCommand(site.Id, "red", "Red crew"));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new CreateCrewCommand(site.Id, "RED", "Again")));
        CreatedResponse ann = await db.SendAsync(new CreatePersonCommand(site.Id, "100-" + _s, "Ann", red.Id, null));
        CreatedResponse bob = await db.SendAsync(new CreatePersonCommand(site.Id, "101-" + _s, "Bob", red.Id, null));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreatePersonCommand(site.Id, "102-" + _s, "Cy", null, "no-such-user")));

        // A day shift worked by RED, every day; the site follows it.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        CreatedResponse schedule = await db.SendAsync(new CreateShiftScheduleCommand(site.Id, "DAY", "Days", null, 1, today.AddDays(-10),
            [new ShiftDto { Code = "D", Name = "Day", Start = "00:00", Minutes = 1440, Days = [0], Crew = "RED" }]));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new CreateShiftScheduleCommand(site.Id, "BAD", "Bad", null, 1, today,
            [new ShiftDto { Code = "D", Start = "00:00", Minutes = 600, Days = [0], Crew = "NOPE" }])));
        var siteVersion = (await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(site.Id))).Version;
        await db.SendAsync(new SetEquipmentCalendarCommand(site.Id, siteVersion, CalendarModes.Schedule, schedule.Id));

        var crews = await QueryAsync<ListCrewsQuery, IReadOnlyList<CrewDto>>(new ListCrewsQuery(site.Id));
        Assert.Equal(("RED", 2), (Assert.Single(crews).Code, crews[0].Members));

        // Yesterday's shift: checked in at 01:00, one out at 05:00; the line shows today's shift and crew.
        var yesterday = new DateTimeOffset(today.AddDays(-1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var checkIn = await db.SendAsync(new CheckInCommand(line.Id, [ann.Id, bob.Id], yesterday.AddHours(1)));
        Assert.Equal("Checked 2 people in.", checkIn.Notes[0]);
        var lines = await QueryAsync<ListLineLaborQuery, IReadOnlyList<LineLaborDto>>(new ListLineLaborQuery(site.Id));
        var onLine = Assert.Single(lines);
        Assert.Equal(("D", "RED", "Red crew", 2), (onLine.Shift!.ShiftCode, onLine.Shift.CrewCode, onLine.Shift.CrewName, onLine.Sessions.Count));
        var bobs = onLine.Sessions.Single(s => s.PersonId == bob.Id);
        await db.SendAsync(new ClockOffCommand(bobs.Id, bobs.Version, yesterday.AddHours(5), OwnOnly: false));

        var key = new ShiftKey(site.Id, today.AddDays(-1), schedule.Id, "D");
        var shifts = await QueryAsync<ListLaborShiftsQuery, IReadOnlyList<LaborShiftDto>>(new ListLaborShiftsQuery(site.Id, today.AddDays(-1)));
        var shift = Assert.Single(shifts);
        Assert.Equal(("D", "RED", 2, 1, false), (shift.ShiftCode, shift.CrewCode, shift.Sessions, shift.OpenSessions, shift.IsApproved));

        // Approval ends Ann's open time when the shift ended (midnight) and locks the shift.
        var approved = await db.SendAsync(new ApproveShiftCommand(key, "OK"));
        Assert.Single(approved.Notes);
        var review = await QueryAsync<GetShiftLaborQuery, ShiftLaborDto>(new GetShiftLaborQuery(key));
        var anns = review.Sessions.Single(s => s.PersonId == ann.Id);
        Assert.Equal((yesterday.AddDays(1), true, 23m), (anns.EndedAt, anns.IsApproved, anns.Hours));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new ApproveShiftCommand(key, null)));

        // A correction replaces Bob's session; the report counts the correction.
        await db.SendAsync(new CorrectLaborSessionCommand(bobs.Id, false, line.Id, null, LaborTypes.Direct, null, yesterday.AddHours(1), yesterday.AddHours(7), 100, "Stayed till 07:00"));
        var report = await QueryAsync<GetLaborReportQuery, LaborReportDto>(new GetLaborReportQuery(site.Id, today.AddDays(-1), today.AddDays(-1)));
        Assert.Equal(6m, report.People.Single(p => p.PersonId == bob.Id).TotalHours);
        Assert.Equal(23m + 6m, Assert.Single(report.Shifts).Hours);

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM core.labor_session WHERE site_id = @Id", new { site.Id }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM core.labor_approval WHERE site_id = @Id", new { site.Id }));
    }

    [Fact]
    public async Task Breaks_come_out_of_worked_time_and_lines_warn_who_lacks_a_required_qualification()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "QENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "QS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse area = await db.SendAsync(new CreateEquipmentCommand(site.Id, "QA-" + _s, "Area", null, "area", "area", false, false, false, null));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(area.Id, "QL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse ann = await db.SendAsync(new CreatePersonCommand(site.Id, "200-" + _s, "Ann", null, null));
        CreatedResponse bob = await db.SendAsync(new CreatePersonCommand(site.Id, "201-" + _s, "Bob", null, null));

        // The area requires FORK, so its line does; Ann holds it, Bob doesn't.
        CreatedResponse fork = await db.SendAsync(new CreateQualificationCommand(site.Id, "fork", "Forklift", "Counterbalance", 12));
        await db.SendAsync(new SetEquipmentQualificationCommand(area.Id, fork.Id, true));
        CreatedResponse granted = await db.SendAsync(new GrantQualificationCommand(ann.Id, fork.Id, null, null, null));
        var requirements = await QueryAsync<GetEquipmentQualificationsQuery, EquipmentQualificationsDto>(new GetEquipmentQualificationsQuery(line.Id));
        Assert.Equal(("FORK", $"QA-{_s}"), (Assert.Single(requirements.Inherited).Code, requirements.Inherited[0].FromEquipmentCode));

        var started = DateTimeOffset.UtcNow.AddHours(-3);
        var checkIn = await db.SendAsync(new CheckInCommand(line.Id, [ann.Id, bob.Id], started));
        Assert.Contains(checkIn.Notes, n => n == $"Bob isn't qualified for QL-{_s}: FORK Forklift (not held).");

        // A 30-minute unpaid break an hour in; then an open paid one.
        await db.SendAsync(new StartBreakCommand(ann.Id, false, started.AddHours(1), started.AddHours(1.5), "Lunch"));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new StartBreakCommand(ann.Id, false, started.AddHours(1.25), started.AddHours(2), null)));
        var coffee = await db.SendAsync(new StartBreakCommand(ann.Id, true, started.AddHours(2.5), null, null));

        var onLine = Assert.Single(await QueryAsync<ListLineLaborQuery, IReadOnlyList<LineLaborDto>>(new ListLineLaborQuery(site.Id)));
        var anns = onLine.Sessions.Single(s => s.PersonId == ann.Id);
        Assert.Equal((2m, 1m), (anns.Hours, anns.BreakHours));
        Assert.Empty(anns.MissingQualifications);
        Assert.Equal(["FORK Forklift (not held)"], onLine.Sessions.Single(s => s.PersonId == bob.Id).MissingQualifications);
        Assert.Equal(coffee.Id, Assert.Single(onLine.Breaks).Id);

        // Clocking Ann off ends her open break with it; the report keeps breaks apart from worked time.
        await db.SendAsync(new ClockOffCommand(anns.Id, anns.Version, null, OwnOnly: false));
        var report = await QueryAsync<GetLaborReportQuery, LaborReportDto>(new GetLaborReportQuery(site.Id, DateOnly.FromDateTime(started.UtcDateTime), DateOnly.FromDateTime(DateTime.UtcNow)));
        var annRow = report.People.Single(p => p.PersonId == ann.Id);
        Assert.Equal((2m, 0.5m, 0.5m), (annRow.TotalHours, annRow.PaidBreakHours, annRow.UnpaidBreakHours));

        var grants = await QueryAsync<ListQualificationGrantsQuery, IReadOnlyList<QualificationGrantDto>>(new ListQualificationGrantsQuery(site.Id, null, fork.Id, null));
        Assert.Equal((granted.Id, GrantStatus.Valid), (Assert.Single(grants).Id, grants[0].Status));
        await db.SendAsync(new RevokeQualificationGrantCommand(granted.Id, grants[0].Version, "Expired licence"));
        var listed = Assert.Single(await QueryAsync<ListQualificationsQuery, IReadOnlyList<QualificationDto>>(new ListQualificationsQuery(site.Id)));
        Assert.Equal(("FORK", 0), (listed.Code, listed.Holders));
        Assert.Equal([$"QA-{_s}"], listed.RequiredOn);
    }

    [Fact]
    public async Task A_terminal_is_enrolled_and_a_badge_clocks_its_person_on_and_off()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "TENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "TS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "TL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse ann = await db.SendAsync(new CreatePersonCommand(site.Id, "300-" + _s, "Ann", null, null));

        CreatedResponse created = await db.SendAsync(new CreateTerminalCommand("hall", "Hall", site.Id, line.Id, TerminalAuthModes.BadgePlusPin));
        var code = await db.SendAsync(new IssueEnrolmentCodeCommand(created.Id));
        var enrolled = await db.SendAsync(new EnrolTerminalCommand(code.Code));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new EnrolTerminalCommand(code.Code)));

        var terminal = await TerminalAsync(enrolled.Secret);
        Assert.Equal(("HALL", created.Id), (terminal.Code, terminal.Id));
        var listed = Assert.Single(await QueryAsync<ListTerminalsQuery, IReadOnlyList<TerminalDto>>(new ListTerminalsQuery(site.Id)));
        Assert.Equal(($"TL-{_s}", (DateTimeOffset?)null), (listed.DefaultCode, listed.EnrolmentExpiresAt));
        Assert.NotNull(listed.EnrolledAt);

        await db.SendAsync(new IssueBadgeCommand(ann.Id, "B-" + _s, "2468"));
        Assert.Equal(new BadgeIdentity(true, null), await db.SendAsync(new IdentifyBadgeCommand(terminal, "B-" + _s, null)));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new IdentifyBadgeCommand(terminal, "B-" + _s, "1111")));
        var who = await db.SendAsync(new IdentifyBadgeCommand(terminal, "b-" + _s, "2468"));
        Assert.Equal(ann.Id, who.PersonId);

        var on = await db.SendAsync(new TerminalClockOnCommand(terminal, ann.Id, line.Id));
        await db.SendAsync(new TerminalStartBreakCommand(terminal, ann.Id, false));
        var status = await QueryAsync<GetTerminalStatusQuery, TerminalStatusDto>(new GetTerminalStatusQuery(terminal, ann.Id));
        Assert.Equal(("Ann", on.Id, $"TL-{_s}"), (status.PersonName, Assert.Single(status.Sessions).Id, Assert.Single(status.Lines).Code));
        Assert.NotNull(status.OpenBreak);
        Assert.Equal((LaborSources.Terminal, "terminal:HALL"), (status.Sessions[0].Source, status.Sessions[0].RecordedBy));

        await db.SendAsync(new TerminalClockOffCommand(terminal, ann.Id, on.Id));
        status = await QueryAsync<GetTerminalStatusQuery, TerminalStatusDto>(new GetTerminalStatusQuery(terminal, ann.Id));
        Assert.Empty(status.Sessions);
        Assert.Null(status.OpenBreak);

        var badges = await QueryAsync<ListBadgesQuery, IReadOnlyList<BadgeDto>>(new ListBadgesQuery(site.Id));
        Assert.Equal(("Ann", true), (Assert.Single(badges).PersonName, badges[0].HasPin));
        await db.SendAsync(new RevokeBadgeCommand(badges[0].Id, "Lost"));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new IdentifyBadgeCommand(terminal, "B-" + _s, "2468")));
    }

    private async Task<TerminalRecord> TerminalAsync(string secret)
    {
        await using var scope = db.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ITerminalReadStore>().FindBySecretAsync(TerminalRules.Sha256(secret), CancellationToken.None))!;
    }

    [Fact]
    public async Task People_import_from_hr_links_a_hand_kept_person_and_matches_on_the_key_again()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "PENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "PS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        await db.SendAsync(new CreateCrewCommand(site.Id, "blue", "Blue crew"));
        CreatedResponse ann = await db.SendAsync(new CreatePersonCommand(site.Id, "500-" + _s, "Ann", null, null));
        var system = "HR-" + _s;

        var csv = $"employee_number,name,crew,external_key\n500-{_s},Ann Berg,BLUE,W-1-{_s}\n501-{_s},Bob,,W-2-{_s}\n";
        ImportPeopleResponse check = await db.SendAsync(new ImportPeopleFileCommand(site.Id, "csv", csv, system, DryRun: true));
        Assert.Equal((false, 1, 1, 0), (check.Applied, check.Created, check.Updated, check.Errors));

        ImportPeopleResponse applied = await db.SendAsync(new ImportPeopleFileCommand(site.Id, "csv", csv, system, DryRun: false));
        Assert.True(applied.Applied);
        var people = await QueryAsync<ListPeopleQuery, IReadOnlyList<PersonDto>>(new ListPeopleQuery(site.Id));
        Assert.Equal(2, people.Count);
        var linked = Assert.Single(people, p => p.Id == ann.Id);
        Assert.Equal(("Ann Berg", "hr", system, $"W-1-{_s}", "BLUE"), (linked.DisplayName, linked.Origin, linked.ExternalSystem, linked.ExternalKey, linked.CrewCode));

        // SCIM for the same system, with a new employee number: the key finds Bob, so nobody is duplicated.
        var scim = $$"""
            { "Resources": [{ "externalId": "W-2-{{_s}}", "displayName": "Bob", "active": false, "meta": { "version": "7" },
              "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "employeeNumber": "601-{{_s}}" } }] }
            """;
        ImportPeopleResponse again = await db.SendAsync(new ImportPeopleFileCommand(site.Id, "scim", scim, system, DryRun: false));
        Assert.Equal(["employee_number", "active", "external_version"], Assert.Single(again.Rows).Changes);
        people = await QueryAsync<ListPeopleQuery, IReadOnlyList<PersonDto>>(new ListPeopleQuery(site.Id));
        Assert.Equal(2, people.Count);
        Assert.Contains(people, p => p.EmployeeNumber == "601-" + _s && !p.IsActive && p.ExternalVersion == "7");

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var reasons = await connection.QueryAsync<string>(
            "SELECT reason FROM evt.event WHERE subject = ANY(@Ids) AND type = 'pulaski.person.updated.v1' ORDER BY position",
            new { Ids = people.Select(p => p.Id).ToArray() });
        Assert.Equal(["CSV import", "SCIM import"], reasons);
    }
}
