using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Changeover rules maintained through the handlers (D-074) and picked up by the schedule's projection (D-015).</summary>
[Collection(DatabaseTests.Name)]
public sealed class ChangeoverRuleTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Rules_made_changed_switched_off_and_imported_size_the_changeovers_the_schedule_shows()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "CENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "CS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "CL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        Guid profiles = Guid.CreateVersion7(), white = Guid.CreateVersion7(), black = Guid.CreateVersion7();
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync(
                """
                INSERT INTO core.material_class (id, code, name) VALUES (@profiles, 'CPRO-' || @s, 'Profiles');
                INSERT INTO core.material (id, code, name, material_type, base_uom_id, material_class_id)
                VALUES (@white, 'CWH-' || @s, 'White', 'finished', @uom, @profiles), (@black, 'CBK-' || @s, 'Black', 'finished', @uom, @profiles);
                INSERT INTO core.run_standard (equipment_id, material_id, uom_id, calculated_rate_per_hour, calculation_basis, calculated_at)
                VALUES (@line, @white, @uom, 1000, 'ideal_rate', now()), (@line, @black, @uom, 1000, 'ideal_rate', now());
                INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id)
                VALUES (@wo, @site, 'CWO-' || @s, @white, 2000, @uom);
                INSERT INTO core.job (site_id, work_order_id, job_number, job_type, equipment_id, material_id, planned_qty, uom_id, sequence_rank)
                VALUES (@site, @wo, 'CJ1-' || @s, 'production', @line, @white, 1000, @uom, 1),
                       (@site, @wo, 'CJ2-' || @s, 'production', @line, @black, 1000, @uom, 2);
                """,
                new { profiles, white, black, s = _s, uom = db.UomId, line = line.Id, site = site.Id, wo = Guid.CreateVersion7() });
        }

        async Task<(TimeSpan Length, string? Source)> ChangeoverAsync()
        {
            var timeline = await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(line.Id));
            var placeholder = timeline.Segments.SingleOrDefault(s => s.Kind == SegmentKind.ChangeoverPlaceholder);
            return placeholder is null ? (TimeSpan.Zero, null) : (placeholder.End - placeholder.Start, placeholder.ChangeoverSource);
        }

        // No rule and no standard changeover: none on the schedule.
        Assert.Equal((TimeSpan.Zero, (string?)null), await ChangeoverAsync());

        // Anything to the class, every line: 45 min.
        var toClass = await db.SendAsync(new CreateChangeoverRuleCommand(site.Id, null, null, null, null, profiles, 45, "Purge"));
        Assert.Equal((TimeSpan.FromMinutes(45), ChangeoverSource.Rule), await ChangeoverAsync());

        // The same pattern again is refused; a material to itself, or another site's line, too.
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateChangeoverRuleCommand(site.Id, null, null, null, null, profiles, 30, null)));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateChangeoverRuleCommand(site.Id, null, white, null, white, null, 30, null)));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateChangeoverRuleCommand(site.Id, enterprise.Id, null, null, null, null, 30, null)));

        // White to black on this line is more specific: it wins.
        var specific = await db.SendAsync(new CreateChangeoverRuleCommand(site.Id, line.Id, white, null, black, null, 20, null));
        Assert.Equal(TimeSpan.FromMinutes(20), (await ChangeoverAsync()).Length);

        // Changed, then switched off: the class rule applies again. A stale version is refused.
        var changed = await db.SendAsync(new UpdateChangeoverRuleCommand(specific.Id, specific.Version, 25, "Measured", true));
        Assert.Equal(TimeSpan.FromMinutes(25), (await ChangeoverAsync()).Length);
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new UpdateChangeoverRuleCommand(specific.Id, specific.Version, 30, null, true)));
        await db.SendAsync(new UpdateChangeoverRuleCommand(specific.Id, changed.Version, 25, "Measured", false));
        Assert.Equal(TimeSpan.FromMinutes(45), (await ChangeoverAsync()).Length);

        // A file: a dry run saves nothing; errors keep the whole file out; then it changes the class rule and adds one.
        var file = $"line,from_material,to_class,to_material,minutes,note\n,,CPRO-{_s},,50,\nCL-{_s},CBK-{_s},,CWH-{_s},15,Back to white\n";
        var dry = await db.SendAsync(new ImportChangeoverRulesCommand(site.Id, ChangeoverRuleCsv.Parse(file), DryRun: true));
        Assert.Equal((false, 1, 1, 0), (dry.Applied, dry.Created, dry.Updated, dry.Errors));
        Assert.Equal(["minutes", "note"], dry.Rows[0].Changes);
        Assert.Equal(TimeSpan.FromMinutes(45), (await ChangeoverAsync()).Length);

        var bad = await db.SendAsync(new ImportChangeoverRulesCommand(site.Id, ChangeoverRuleCsv.Parse(file + "NOPE,,,,5,\n"), DryRun: false));
        Assert.Equal((false, 1), (bad.Applied, bad.Errors));
        Assert.Contains("There is no line at this site NOPE.", bad.Rows.Single(r => r.Action == "error").Message, StringComparison.Ordinal);

        var applied = await db.SendAsync(new ImportChangeoverRulesCommand(site.Id, ChangeoverRuleCsv.Parse(file), DryRun: false));
        Assert.True(applied.Applied);
        Assert.Equal(TimeSpan.FromMinutes(50), (await ChangeoverAsync()).Length);

        var rules = await QueryAsync<ListChangeoverRulesQuery, IReadOnlyList<ChangeoverRuleDto>>(new ListChangeoverRulesQuery(site.Id));
        Assert.Equal(3, rules.Count);
        Assert.False(rules[^1].IsActive);
        var classRule = rules.Single(r => r.Id == toClass.Id);
        // The file has a note column and that row's cell is blank, so it cleared the note.
        Assert.Equal(("CPRO-" + _s, 50, (string?)null), (classRule.ToMaterialClassCode, classRule.DurationMinutes, classRule.Note));
    }
}
