using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Domain;
using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Commands through the real handlers, SQL and transactions, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class PersistenceTests(DatabaseFixture db)
{
    [Fact]
    public async Task Event_log_is_append_only_for_the_app()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync();

        foreach (var sql in new[] { "UPDATE evt.event SET reason = 'x'", "DELETE FROM evt.event" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync(sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task App_cannot_delete_master_data()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("DELETE FROM core.equipment"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Create_writes_row_closure_and_event_in_one_transaction()
    {
        var (_, site, line) = await CreateHierarchyAsync();

        var reads = db.Services.GetRequiredService<IEquipmentReadStore>();
        var all = await reads.ListAsync(siteId: null, CancellationToken.None);
        var ids = all.Select(e => e.Id).ToList();
        Assert.True(ids.IndexOf(site) < ids.IndexOf(line));

        var row = (await reads.GetAsync(line, CancellationToken.None))!;
        Assert.Equal(2, row.Depth);
        Assert.Equal(site, row.SiteId);

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM core.equipment_closure WHERE descendant_id = @line", new { line }));

        var evt = await connection.QuerySingleAsync<(string Type, long Sequence, string ActorId, string Data)>(
            "SELECT type, sequence, actor_id, data::text FROM evt.event WHERE subject = @line", new { line });
        Assert.Equal(EventTypes.EquipmentCreated, evt.Type);
        Assert.Equal(1, evt.Sequence);
        Assert.Equal("test-user", evt.ActorId);
        Assert.Contains("\"after\"", evt.Data, StringComparison.Ordinal);
        Assert.Contains("\"equipmentRole\": \"production_line\"", evt.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_checks_version_and_records_before_and_after()
    {
        var (_, _, line) = await CreateHierarchyAsync();
        var reads = db.Services.GetRequiredService<IEquipmentReadStore>();
        var current = (await reads.GetAsync(line, CancellationToken.None))!;

        var updated = await db.SendAsync(
            new UpdateEquipmentCommand(line, current.Version, "Renamed", null, true, true, false, true));

        Assert.NotEqual(current.Version, updated.Version);
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(
            new UpdateEquipmentCommand(line, current.Version, "Again", null, true, true, false, true)));

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var events = (await connection.QueryAsync<(long Sequence, string Data)>(
            "SELECT sequence, data::text FROM evt.event WHERE subject = @line ORDER BY sequence", new { line })).ToList();
        Assert.Equal([1L, 2L], events.Select(e => e.Sequence));
        Assert.Contains("\"before\"", events[1].Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Duplicate_code_is_a_conflict_and_nothing_is_written()
    {
        var (_, site, _) = await CreateHierarchyAsync();
        var area = new CreateEquipmentCommand(site, "DUP", "Area", null, "area", "area", false, false, false, null);
        await db.SendAsync(area);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(area));
        Assert.Contains("equipment code", ex.Message, StringComparison.Ordinal);

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM core.equipment WHERE site_id = @site AND code = 'DUP'", new { site }));
    }

    [Fact]
    public async Task Uncommitted_unit_of_work_rolls_back()
    {
        var (_, site, _) = await CreateHierarchyAsync();
        var siteRow = (await db.Services.GetRequiredService<IEquipmentReadStore>().GetAsync(site, CancellationToken.None))!;
        var parent = new Equipment(site, siteRow.ParentId, site, siteRow.Code, siteRow.Name, null, "site", "site", false, false, false, "UTC", true);
        var area = Equipment.Create(Guid.CreateVersion7(), parent, "GONE", "Gone", null, "area", "area", false, false, false, null);

        await using (var scope = db.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var repository = scope.ServiceProvider.GetRequiredService<IEquipmentRepository>();
            await using var transaction = await unitOfWork.BeginAsync(CancellationToken.None);
            await repository.InsertAsync(area, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        Assert.Null(await db.Services.GetRequiredService<IEquipmentReadStore>().GetAsync(area.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Writes_outside_a_unit_of_work_are_refused()
    {
        await using var scope = db.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.GetForUpdateAsync(Guid.CreateVersion7(), CancellationToken.None));
    }

    [Fact]
    public async Task Child_above_parent_level_is_a_rule_violation()
    {
        var (_, _, line) = await CreateHierarchyAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(
            new CreateEquipmentCommand(line, "BAD", "Bad", null, "area", "area", false, false, false, null)));
    }

    [Fact]
    public async Task Work_order_round_trips_times_and_records_events_in_sequence()
    {
        var (_, site, line) = await CreateHierarchyAsync();
        var start = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(-5));

        var created = await db.SendAsync(new CreateWorkOrderCommand(
            site, "WO-" + Guid.NewGuid().ToString("N")[..8], db.MaterialId, 1500.5m, db.UomId, line,
            start, start.AddHours(8), DueAt: null, Priority: 2));

        var reads = db.Services.GetRequiredService<IWorkOrderReadStore>();
        var row = (await reads.GetAsync(created.Id, CancellationToken.None))!;
        Assert.Equal(start, row.PlannedStart);
        Assert.Equal(TimeSpan.Zero, row.PlannedStart!.Value.Offset);
        Assert.Null(row.DueAt);
        Assert.Equal(1500.5m, row.PlannedQty);
        Assert.Equal("planned", row.Status);
        Assert.Equal(created.Version, row.Version);

        await db.SendAsync(new ChangeWorkOrderStatusCommand(created.Id, created.Version, "cancelled", "ready"));
        await Assert.ThrowsAsync<ConflictException>(
            () => db.SendAsync(new ChangeWorkOrderStatusCommand(created.Id, created.Version, "cancelled", null)));

        Assert.Contains(await reads.ListAsync(site, "cancelled", CancellationToken.None), w => w.Id == created.Id);

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var events = (await connection.QueryAsync<(string Type, long Sequence, string? Reason)>(
            "SELECT type, sequence, reason FROM evt.event WHERE subject = @Id ORDER BY sequence",
            new { created.Id })).ToList();
        Assert.Equal(
            [(EventTypes.WorkOrderCreated, 1L), (EventTypes.WorkOrderStatusChanged, 2L)],
            events.Select(e => (e.Type, e.Sequence)));
        Assert.Equal("ready", events[1].Reason);
    }

    [Fact]
    public async Task Work_order_target_outside_site_is_a_rule_violation()
    {
        var (_, site, _) = await CreateHierarchyAsync();
        var (_, _, otherLine) = await CreateHierarchyAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateWorkOrderCommand(
            site, "WO-X", db.MaterialId, 1, db.UomId, otherLine, null, null, null, 0)));
    }

    [Fact]
    public async Task Reference_data_lists_seeded_rows()
    {
        var reads = db.Services.GetRequiredService<IReferenceDataReadStore>();

        Assert.Contains(await reads.ListUomsAsync(CancellationToken.None), u => u.Id == db.UomId);
        var material = Assert.Single(await reads.ListMaterialsAsync(false, CancellationToken.None), m => m.Id == db.MaterialId);
        Assert.Equal("m", material.BaseUomCode);
    }

    /// <summary>A fresh enterprise → site → line through the real handlers, with unique codes.</summary>
    private async Task<(Guid Enterprise, Guid Site, Guid Line)> CreateHierarchyAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(
            null, "ENT-" + suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(
            enterprise.Id, "SITE-" + suffix, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(
            site.Id, "LINE-" + suffix, "Line", null, "work_center", "production_line", true, true, false, null));
        return (enterprise.Id, site.Id, line.Id);
    }
}
