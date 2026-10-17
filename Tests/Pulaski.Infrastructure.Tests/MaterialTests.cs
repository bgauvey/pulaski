using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Materials;
using Pulaski.Application.ReferenceData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Materials management end to end (D-033): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class MaterialTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private async Task<string> UnitCodeAsync()
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<string>("SELECT code FROM core.uom WHERE id = @Id", new { Id = db.UomId }) ?? "";
    }

    [Fact]
    public async Task Materials_and_classes_are_created_changed_and_listed()
    {
        CreatedResponse resins = await db.SendAsync(new CreateMaterialClassCommand("RES-" + _s, "Resins"));
        CreatedResponse created = await db.SendAsync(new CreateMaterialCommand(
            "MAT-" + _s, "Test resin", "For the test", "raw", resins.Id, db.UomId, true, 90));

        var detail = await QueryAsync<GetMaterialQuery, MaterialDetailDto>(new GetMaterialQuery(created.Id));
        Assert.Equal(("Test resin", "For the test", "Resins", 90, "local"), (detail.Material.Name, detail.Material.Description, detail.Material.MaterialClassName, detail.Material.ShelfLifeDays!.Value, detail.Material.Origin));
        Assert.False(detail.Usage.InUse);

        var update = new UpdateMaterialCommand(created.Id, detail.Material.Version, "MAT2-" + _s, "Renamed resin", null, "raw", resins.Id, db.UomId, false, null, true);
        var changed = await db.SendAsync(update);
        Assert.Empty(changed.StillUsedBy);

        var classes = await QueryAsync<ListMaterialClassesQuery, IReadOnlyList<MaterialClassDto>>(new ListMaterialClassesQuery());
        var listedClass = Assert.Single(classes, c => c.Id == resins.Id);
        Assert.Equal((1, true), (listedClass.MaterialCount, listedClass.IsActive));
        await db.SendAsync(new UpdateMaterialClassCommand(resins.Id, listedClass.Version, "Resins (old)", false));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CreateMaterialCommand("MAT3-" + _s, "X", null, "raw", resins.Id, db.UomId, true, null)));

        var materials = await QueryAsync<ListMaterialsQuery, IReadOnlyList<MaterialDto>>(new ListMaterialsQuery(IncludeInactive: true));
        var listed = Assert.Single(materials, m => m.Id == created.Id);
        Assert.Equal(("MAT2-" + _s, false, "Resins (old)"), (listed.Code, listed.IsLotTracked, listed.MaterialClassName));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new CreateMaterialCommand("MAT2-" + _s, "Dup", null, "raw", null, db.UomId, true, null)));
    }

    [Fact]
    public async Task Use_fixes_the_base_unit_and_code_and_a_deactivation_reports_what_still_uses_it()
    {
        CreatedResponse created = await db.SendAsync(new CreateMaterialCommand("USE-" + _s, "Used", null, "raw", null, db.UomId, true, null));
        CreatedResponse site = await db.SendAsync(new Application.EquipmentModel.CreateEquipmentCommand(
            null, "ENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse plant = await db.SendAsync(new Application.EquipmentModel.CreateEquipmentCommand(
            site.Id, "SITE-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse store = await db.SendAsync(new Application.EquipmentModel.CreateEquipmentCommand(
            plant.Id, "ST-" + _s, "Store", null, "work_center", "storage_zone", false, false, true, null));
        await db.SendAsync(new ReceiveLotCommand(created.Id, store.Id, "L-" + _s, 25m, db.UomId));
        CreatedResponse line = await db.SendAsync(new Application.EquipmentModel.CreateEquipmentCommand(
            plant.Id, "LN-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));
        await db.ExecuteAsync(
            $"""
            INSERT INTO core.equipment_capability (equipment_id, material_id, ideal_rate_per_hour, rate_uom_id, effective_from)
            VALUES ('{line.Id}', '{created.Id}', 100, '{db.UomId}', now() - interval '1 day')
            """);

        var detail = await QueryAsync<GetMaterialQuery, MaterialDetailDto>(new GetMaterialQuery(created.Id));
        Assert.Equal((true, 1, 25m), (detail.Usage.InUse, detail.Usage.LotsOnHand, detail.Usage.QuantityOnHand));

        CreatedResponse other = await db.SendAsync(new Application.Units.CreateUnitCommand("o" + _s, "Other", "count", 1m));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateMaterialCommand(
            created.Id, detail.Material.Version, "USE-" + _s, "Used", null, "raw", null, other.Id, true, null, true)));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateMaterialCommand(
            created.Id, detail.Material.Version, "USE2-" + _s, "Used", null, "raw", null, db.UomId, true, null, true)));

        var off = await db.SendAsync(new UpdateMaterialCommand(created.Id, detail.Material.Version, "USE-" + _s, "Used", null, "raw", null, db.UomId, true, null, false));
        Assert.Equal(["25 on hand in 1 lot", "a capability on 1 line"], off.StillUsedBy);
    }

    [Fact]
    public async Task An_import_is_idempotent_and_leaves_pulaskis_fields_alone()
    {
        var unit = await UnitCodeAsync();
        var first = await db.SendAsync(new ImportMaterialCommand("ERP-" + _s, "1001", "v1", "ERP-" + _s, "From the ERP", unit, "finished"));
        Assert.True(first.Created);
        var detail = await QueryAsync<GetMaterialQuery, MaterialDetailDto>(new GetMaterialQuery(first.Id));
        await db.SendAsync(new UpdateMaterialCommand(first.Id, detail.Material.Version, detail.Material.Code, detail.Material.Name, "Pulaski note",
            "finished", null, db.UomId, false, 30, true));

        var again = await db.SendAsync(new ImportMaterialCommand("ERP-" + _s, "1001", "v2", "ERP-" + _s, "From the ERP, renamed", unit, "finished"));
        Assert.Equal((first.Id, true), (again.Id, again.Changed));
        var after = (await QueryAsync<GetMaterialQuery, MaterialDetailDto>(new GetMaterialQuery(first.Id))).Material;
        Assert.Equal(("From the ERP, renamed", "Pulaski note", 30, false, "v2", "erp"), (after.Name, after.Description, after.ShelfLifeDays!.Value, after.IsLotTracked, after.ExternalVersion, after.Origin));

        var repeat = await db.SendAsync(new ImportMaterialCommand("ERP-" + _s, "1001", "v2", "ERP-" + _s, "From the ERP, renamed", unit, "finished"));
        Assert.False(repeat.Changed);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateMaterialCommand(
            first.Id, after.Version, after.Code, "Local rename", after.Description, "finished", null, db.UomId, false, 30, true)));
    }
}
