using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Capabilities from a CSV file (D-055): the capability in effect now is changed, added or ended, all or nothing.</summary>
public class ImportCapabilitiesHandlerTests
{
    private const string Header = "material,line,ideal_rate,rate_unit,changeover_minutes,crew_size,preferred,active\n";

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly CapabilityLine _line;
    private readonly Material _profile;

    public ImportCapabilitiesHandlerTests()
    {
        var m = _f.Stores.Units.AddUnit("m", "length", 1m).Id;
        _f.Stores.MaterialAdmin.AddUom("m", id: m);
        _line = Capabilities.AddLine("EXT-01", siteId: _site, siteCode: "S1");
        _profile = Material.CreateLocal(Guid.CreateVersion7(), new MaterialChange("PRF-1", "Profile", null, "finished", null, m, true, null, true));
        _f.Stores.MaterialAdmin.InsertAsync(_profile, HandlerFixture.Now, CancellationToken.None).Wait();
    }

    private InMemoryCapabilities Capabilities => _f.Stores.Capabilities;

    private Task<ImportMaterialsResponse> RunAsync(string csv, bool dryRun = false, IReadOnlyCollection<Guid>? sites = null) =>
        new ImportCapabilitiesHandler(_f.Stores.UnitOfWork, Capabilities, _f.Stores.MaterialAdmin, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ImportCapabilitiesCommand(CapabilityCsv.Parse(csv), dryRun, "CSV import", sites), CancellationToken.None);

    [Fact]
    public async Task A_new_pair_is_added_from_now_and_seeds_the_planning_rate_then_importing_again_changes_nothing()
    {
        var added = await RunAsync(Header + "PRF-1,ext-01,900,m,45,2,yes,\n");

        var row = Assert.Single(added.Rows);
        Assert.Equal(("create", "PRF-1 on ext-01"), (row.Action, row.Code));
        Assert.Contains("Planning rate set to 900 m/h", Assert.Single(row.Notes), StringComparison.Ordinal);
        var capability = Assert.Single(Capabilities.On(_line.Id, _profile.Id));
        Assert.Equal((900m, TimeSpan.FromMinutes(45), 2m, true, HandlerFixture.Now), (capability.IdealRatePerHour, capability.StandardChangeover, capability.StandardCrewSize, capability.IsPreferred, capability.EffectiveFrom));
        Assert.Equal([EventTypes.CapabilityAdded, EventTypes.RunStandardCreated], _f.Stores.Events.Appended.Select(e => e.Type));
        Assert.Equal("CSV import", _f.Stores.Events.Appended[0].Reason);

        var again = await RunAsync(Header + "PRF-1,EXT-01,900,m,45,2,yes,\n");
        Assert.Equal("unchanged", Assert.Single(again.Rows).Action);
        Assert.Equal(2, _f.Stores.Events.Appended.Count);
    }

    [Fact]
    public async Task The_capability_in_effect_is_changed_in_place_keeping_what_the_file_leaves_out_and_can_be_ended()
    {
        await RunAsync(Header + "PRF-1,EXT-01,900,m,45,2,yes,\n");

        var changed = await RunAsync("material,line,ideal_rate,rate_unit\nPRF-1,EXT-01,1000,m\n");
        var row = Assert.Single(changed.Rows);
        Assert.Equal(["ideal_rate"], row.Changes);
        Assert.Contains("Planning rate now 1,000 m/h", Assert.Single(row.Notes), StringComparison.Ordinal);
        var capability = Assert.Single(Capabilities.On(_line.Id, _profile.Id));
        Assert.Equal((1000m, TimeSpan.FromMinutes(45), true), (capability.IdealRatePerHour, capability.StandardChangeover, capability.IsPreferred));
        Assert.Equal(1000m, Capabilities.StandardFor(_line.Id, _profile.Id)!.CalculatedRatePerHour);

        _f.Time.Advance(TimeSpan.FromHours(1));
        var ended = await RunAsync(Header + "PRF-1,EXT-01,,,,,,no\n");
        Assert.Equal(["ended"], Assert.Single(ended.Rows).Changes);
        Assert.Equal(HandlerFixture.Now.AddHours(1), Assert.Single(Capabilities.On(_line.Id, _profile.Id)).EffectiveTo);
        Assert.Equal(EventTypes.CapabilityEnded, _f.Stores.Events.Appended[^1].Type);

        var nothing = await RunAsync(Header + "PRF-1,EXT-01,,,,,,no\n");
        Assert.Equal(("unchanged", "No capability in effect to end."), (Assert.Single(nothing.Rows).Action, nothing.Rows[0].Notes[0]));
    }

    [Fact]
    public async Task Every_bad_row_is_reported_and_nothing_is_saved()
    {
        var other = Guid.CreateVersion7();
        Capabilities.AddLine("EXT-02", siteId: _site, siteCode: "S1");
        Capabilities.AddLine("EXT-02", siteId: other, siteCode: "S2");
        Capabilities.AddLine("PACK-1", siteId: other, siteCode: "S2");
        Capabilities.AddLine("EXTRUSION", canRunJob: false, siteId: _site, siteCode: "S1");

        var result = await RunAsync(
            "material,site,line,ideal_rate,rate_unit\n" +
            "PRF-1,,EXT-01,900,m\n" +
            "PRF-1,,EXT-02,900,m\n" +
            "PRF-1,s2,EXT-02,900,m\n" +
            "PRF-1,,PACK-1,900,m\n" +
            "NOPE,,EXT-01,900,m\n" +
            "PRF-1,,NOPE,900,m\n" +
            "PRF-1,,EXTRUSION,900,m\n" +
            "PRF-1,S1,EXT-02,,m\n" +
            "PRF-1,,EXT-01,900,furlong\n",
            sites: [_site]);

        Assert.False(result.Applied);
        Assert.Equal(["create", "error", "error", "error", "error", "error", "error", "error", "error"], result.Rows.Select(r => r.Action));
        Assert.Contains("several sites (S1, S2)", result.Rows[1].Message, StringComparison.Ordinal);
        Assert.Contains("can't change the capabilities of EXT-02 at S2", result.Rows[2].Message, StringComparison.Ordinal);
        Assert.Contains("PACK-1 at S2", result.Rows[3].Message, StringComparison.Ordinal);
        Assert.Contains("no material NOPE", result.Rows[4].Message, StringComparison.Ordinal);
        Assert.Contains("no line NOPE", result.Rows[5].Message, StringComparison.Ordinal);
        Assert.Contains("doesn't run jobs", result.Rows[6].Message, StringComparison.Ordinal);
        Assert.Contains("ideal_rate: Required", result.Rows[7].Message, StringComparison.Ordinal);
        Assert.Contains("appears earlier", result.Rows[8].Message, StringComparison.Ordinal);
        Assert.Equal(0, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task A_lines_batch_tolerance_is_read_and_refused_for_a_material_not_made_in_batches()
    {
        var row = Assert.Single(CapabilityCsv.Parse("material,line,ideal_rate,rate_unit,batch_tolerance\nPRF-1,EXT-01,900,m,5\n").Rows);
        Assert.Equal(5m, row.BatchTolerancePct!.Value);

        var result = await RunAsync("material,line,ideal_rate,rate_unit,batch_tolerance\nPRF-1,EXT-01,900,m,5\n");
        Assert.Contains("isn't made in batches", Assert.Single(result.Rows).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dry_run_reports_without_committing()
    {
        var result = await RunAsync(Header + "PRF-1,EXT-01,900,m,,,,\n", dryRun: true);

        Assert.Equal((false, 1, 0), (result.Applied, result.Created, _f.Stores.UnitOfWork.Committed));
    }

    [Fact]
    public void The_file_is_read_with_its_problems_and_the_template_lists_every_column()
    {
        var row = Assert.Single(CapabilityCsv.Parse("Item Code;Line Code;Rate;UOM;Changeover;Crew\nPRF-1;EXT-01;1,5;m;x;\n").Rows);
        Assert.Equal(2, row.Problems.Count);
        Assert.Null(row.CrewSize!.Value);
        Assert.Contains("missing", Assert.Single(CapabilityCsv.Parse("material,line,ideal_rate\nA,B,1\n").Problems), StringComparison.Ordinal);
        Assert.Equal(CapabilityCsvFormat.Header, CapabilityCsv.Template);
    }
}
