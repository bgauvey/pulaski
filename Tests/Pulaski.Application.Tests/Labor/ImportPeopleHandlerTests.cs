using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Domain;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Labor;

/// <summary>Importing people from HR and ERP sources (D-054): matched on their key, never duplicated, every change an event.</summary>
public class ImportPeopleHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly IPeopleSourceAdapter[] _adapters = [new CsvPeopleSource(), new ScimPeopleSource()];

    public ImportPeopleHandlerTests() => Labor.AddSite(_site);

    private InMemoryLabor Labor => _f.Stores.Labor;

    private Task<ImportPeopleResponse> RunAsync(
        string csv, string? system = "HR1", bool dryRun = false, string source = "csv", Guid? site = null, bool switchOffMissing = false) =>
        new ImportPeopleFileHandler(
                _adapters,
                new ImportPeopleHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events, _f.Ids, Labor, new FixedRequestContext("hr-sync"), _f.Time))
            .HandleAsync(new ImportPeopleFileCommand(site ?? _site, source, csv, system, dryRun) { SwitchOffMissing = switchOffMissing }, CancellationToken.None);

    [Fact]
    public async Task A_full_import_switches_off_the_systems_people_it_no_longer_lists_but_not_someone_clocked_on()
    {
        await RunAsync("employee_number,name,external_key\n1001,Ana Ng,W-1\n1002,Bo Li,W-2\n1003,Cy Ito,W-3\n");
        var local = Labor.AddPerson(_site, "Kept by hand");
        var cy = ByNumber("1003").Value;
        await Labor.InsertSessionAsync(
            new SessionRecord(Guid.CreateVersion7(), _site, cy.Id, Guid.CreateVersion7(), null, "direct", null, HandlerFixture.Now.AddHours(-1), null, 100m,
                DateOnly.FromDateTime(HandlerFixture.Now.UtcDateTime), null, null, "manual", null, false, null, "test"),
            HandlerFixture.Now, CancellationToken.None);

        // Without a system there is no telling who is missing.
        var noSystem = await RunAsync("employee_number,name\n1001,Ana Ng\n", system: null, switchOffMissing: true);
        Assert.Contains("external system", Assert.Single(noSystem.FileProblems), StringComparison.Ordinal);

        var full = await RunAsync("employee_number,name,external_key\n1001,Ana Ng,W-1\n", switchOffMissing: true);

        Assert.True(full.Applied);
        Assert.False(ByNumber("1002").Value.IsActive);
        Assert.True(ByNumber("1003").Value.IsActive);
        Assert.Contains("clocked on", Assert.Single(full.Rows, r => r.Code == "1003").Notes[0], StringComparison.Ordinal);
        Assert.Equal(["active"], Assert.Single(full.Rows, r => r.Code == "1002").Changes);
        Assert.True((await Labor.GetPersonForUpdateAsync(local, CancellationToken.None))!.Value.IsActive);

        // Without the option nobody is switched off.
        await RunAsync("employee_number,name,external_key\n1001,Ana Ng,W-1\n");
        Assert.True(ByNumber("1003").Value.IsActive);
    }

    [Fact]
    public async Task Listed_qualifications_are_granted_once_and_renewed_by_a_later_expiry_never_revoked()
    {
        var fork = new QualificationRecord(Guid.CreateVersion7(), _site, "FORK", "Forklift", null, 12, true);
        await Labor.InsertQualificationAsync(fork, HandlerFixture.Now, CancellationToken.None);

        var bad = await RunAsync("employee_number,name,external_key,qualifications\n9001,Di Oh,W-9,WELD\n");
        Assert.Contains("no qualification WELD", Assert.Single(bad.Rows).Message, StringComparison.Ordinal);

        var first = await RunAsync("employee_number,name,external_key,qualifications\n1001,Ana Ng,W-1,fork\n");
        Assert.Contains("Granted qualification FORK until 2027-10-11.", Assert.Single(first.Rows).Notes);
        var ana = ByNumber("1001").Value;
        var grant = Assert.Single(await Labor.ListGrantsOfPersonAsync(ana.Id, CancellationToken.None));
        Assert.Equal((new DateOnly(2026, 10, 12), (DateOnly?)new DateOnly(2027, 10, 11)), (grant.ValidFrom, grant.ExpiresOn));
        Assert.Equal((EventTypes.QualificationGranted, "CSV import"), (_f.Stores.Events.Appended[^1].Type, _f.Stores.Events.Appended[^1].Reason));

        var again = await RunAsync("employee_number,name,external_key,qualifications\n1001,Ana Ng,W-1,FORK\n");
        Assert.Equal("unchanged", Assert.Single(again.Rows).Action);

        var renewed = await RunAsync("employee_number,name,external_key,qualifications\n1001,Ana Ng,W-1,FORK:2027-10-01:2028-09-30\n");
        Assert.Equal(["qualification FORK until 2028-09-30"], Assert.Single(renewed.Rows).Changes);
        Assert.Equal(2, (await Labor.ListGrantsOfPersonAsync(ana.Id, CancellationToken.None)).Count);

        // Leaving it out revokes nothing.
        await RunAsync("employee_number,name,external_key,qualifications\n1001,Ana Ng,W-1,\n");
        Assert.All(await Labor.ListGrantsOfPersonAsync(ana.Id, CancellationToken.None), g => Assert.Null(g.RevokedAt));
    }

    private Versioned<PersonRecord> ByNumber(string number) => Labor.FindPersonByNumberForUpdateAsync(_site, number, CancellationToken.None).Result!;

    [Fact]
    public async Task Importing_again_matches_on_the_key_so_nobody_is_duplicated_and_changes_are_events()
    {
        var first = await RunAsync("employee_number,name,external_key\n1001,Ana Ng,W-1\n");
        Assert.Equal((true, 1), (first.Applied, first.Created));
        var ana = ByNumber("1001");
        Assert.Equal(("hr", "HR1", "W-1"), (ana.Value.Origin, ana.Value.ExternalSystem, ana.Value.ExternalKey));

        var again = await RunAsync("employee_number,name,external_key\n1001,Ana Ng,W-1\n");
        Assert.Equal(("unchanged", 1), (Assert.Single(again.Rows).Action, _f.Stores.Events.Appended.Count));

        // A new employee number and a new name still find her by her key.
        var renamed = await RunAsync("employee_number,name,external_key,external_version\n2001,Ana Ng-Berg,W-1,5\n");
        Assert.Equal(["employee_number", "name", "external_version"], Assert.Single(renamed.Rows).Changes);
        Assert.Equal((ana.Value.Id, "Ana Ng-Berg"), (ByNumber("2001").Value.Id, ByNumber("2001").Value.DisplayName));
        var update = _f.Stores.Events.Appended[^1];
        Assert.Equal((EventTypes.PersonUpdated, "CSV import"), (update.Type, update.Reason));
    }

    [Fact]
    public async Task A_person_kept_by_hand_is_linked_by_employee_number_keeping_crew_and_sign_in()
    {
        var crew = Labor.AddCrew(_site, "A");
        var bo = Labor.AddPerson(_site, "Bo", crewId: crew);
        var number = Labor.Person(bo).Value.EmployeeNumber;

        var result = await RunAsync($"employee_number,name,external_key\n{number},Bo Lind,W-2\n");

        Assert.Equal(["name", "origin", "external_key"], Assert.Single(result.Rows).Changes);
        var linked = Labor.Person(bo).Value;
        Assert.Equal(("hr", "W-2", crew), (linked.Origin, linked.ExternalKey, linked.CrewId));
    }

    [Fact]
    public async Task Pulaski_fields_in_the_file_are_applied_and_an_imported_person_isnt_matched_as_a_local_one()
    {
        Labor.AddCrew(_site, "NIGHT");
        Labor.AddUser("cy", Guid.CreateVersion7());

        var created = await RunAsync("employee_number,name,crew,username,origin,external_key\n3001,Cy,night,cy,erp,E-3\n");
        Assert.Equal(1, created.Created);
        Assert.Equal("erp", ByNumber("3001").Value.Origin);
        Assert.NotNull(ByNumber("3001").Value.CrewId);
        Assert.NotNull(ByNumber("3001").Value.AppUserId);

        var local = await RunAsync("employee_number,name\n3001,Cy\n", system: null);
        Assert.Contains("comes from HR1", Assert.Single(local.Rows).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_bad_row_stops_the_whole_import_and_every_problem_is_listed()
    {
        var other = Guid.CreateVersion7();
        Labor.AddSite(other);
        await RunAsync("employee_number,name,external_key\n9,Elsewhere,W-9\n", site: other);
        var openId = await AddClockedOnAsync("8001", "W-8");

        var result = await RunAsync(
            "employee_number,name,crew,active,origin,external_key\n" +
            "1,Fine,,,,W-1\n" +
            "2,No crew,MISSING,,,W-2\n" +
            "3,Elsewhere,,,,W-9\n" +
            "8001,Leaving,,no,,W-8\n" +
            "4,Bad origin,,,payroll,W-4\n" +
            "1,Twice,,,,W-5\n");

        Assert.False(result.Applied);
        Assert.Equal(["create", "error", "error", "error", "error", "error"], result.Rows.Select(r => r.Action));
        Assert.Contains("no crew MISSING", result.Rows[1].Message, StringComparison.Ordinal);
        Assert.Contains("another site", result.Rows[2].Message, StringComparison.Ordinal);
        Assert.Contains("still clocked on", result.Rows[3].Message, StringComparison.Ordinal);
        Assert.Contains("origin", result.Rows[4].Message, StringComparison.Ordinal);
        Assert.Contains("appears earlier", result.Rows[5].Message, StringComparison.Ordinal);
        Assert.True(Labor.Person(openId).Value.IsActive);
    }

    [Fact]
    public async Task A_dry_run_reports_without_committing_and_scim_goes_through_the_same_rules()
    {
        var scim = """[{ "id": "u1", "displayName": "Di", "active": false, "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "employeeNumber": "5" } }]""";

        var check = await RunAsync(scim, source: "scim", dryRun: true);
        Assert.Equal((false, 1, 0), (check.Applied, check.Created, _f.Stores.UnitOfWork.Committed));

        var applied = await RunAsync(scim, source: "SCIM");
        Assert.True(applied.Applied);
        Assert.Equal(("u1", false), (ByNumber("5").Value.ExternalKey, ByNumber("5").Value.IsActive));
        Assert.Equal("SCIM import", _f.Stores.Events.Appended[^1].Reason);

        await Assert.ThrowsAsync<NotFoundException>(() => RunAsync("x", source: "payroll"));
    }

    [Fact]
    public async Task On_the_screen_an_imported_person_keeps_their_name_and_status_but_takes_a_crew()
    {
        await RunAsync("employee_number,name,external_key\n6,Ed,W-6\n");
        var ed = ByNumber("6");
        var crew = Labor.AddCrew(_site, "B");
        var handler = new UpdatePersonHandler(_f.Stores.UnitOfWork, Labor, _f.Stores.Events, _f.Events);

        var renamed = await Assert.ThrowsAsync<DomainRuleException>(() =>
            handler.HandleAsync(new UpdatePersonCommand(ed.Value.Id, ed.Version, "Edward", null, null, IsActive: true), CancellationToken.None));
        Assert.Contains("comes from HR1", renamed.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            handler.HandleAsync(new UpdatePersonCommand(ed.Value.Id, ed.Version, "Ed", null, null, IsActive: false), CancellationToken.None));

        await handler.HandleAsync(new UpdatePersonCommand(ed.Value.Id, ed.Version, "Ed", crew, null, IsActive: true), CancellationToken.None);
        Assert.Equal(crew, ByNumber("6").Value.CrewId);
    }

    private async Task<Guid> AddClockedOnAsync(string number, string key)
    {
        await RunAsync($"employee_number,name,external_key\n{number},Leaving,{key}\n");
        var person = ByNumber(number).Value;
        var at = _f.Time.GetUtcNow();
        await Labor.InsertSessionAsync(
            new SessionRecord(Guid.CreateVersion7(), _site, person.Id, Guid.CreateVersion7(), null, "direct", null, at, null, 100m,
                DateOnly.FromDateTime(at.UtcDateTime), null, null, "manual", null, false, null, "test"),
            at, CancellationToken.None);
        return person.Id;
    }
}
