using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Reason trees (D-062) against the real schema: tree order and paths on read, groups never chosen, history unchanged by moves.</summary>
[Collection(DatabaseTests.Name)]
public sealed class ReasonTreeTests(DatabaseFixture db)
{
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    [Fact]
    public async Task Reasons_move_between_groups_while_history_keeps_naming_the_same_reason()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "RENT-" + _suffix, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "RSITE-" + _suffix, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse stores = await db.SendAsync(new CreateEquipmentCommand(site.Id, "RRM-" + _suffix, "Stores", null, "work_center", "storage_zone", false, false, true, null));
        CreatedResponse material = await db.SendAsync(new CreateMaterialCommand("RMAT-" + _suffix, "Resin", null, "raw", null, db.UomId, true, null));
        const string Kind = "inventory_adjustment";

        var damage = await db.SendAsync(new CreateReasonCodeCommand(site.Id, Kind, "DMG-" + _suffix, "Damaged", false, 10));
        var lot = await db.SendAsync(new ReceiveLotCommand(material.Id, stores.Id, "RL-" + _suffix, 10m, db.UomId));
        await db.SendAsync(new WriteOffLotCommand(lot.LotId, lot.Version, 2m, db.UomId, damage.Id));

        // A group, a group inside it, and the reason filed in the inner one.
        var handling = await db.SendAsync(new CreateReasonCodeCommand(site.Id, Kind, "HND-" + _suffix, "Handling", false, 5, IsGroup: true));
        var forklift = await db.SendAsync(new CreateReasonCodeCommand(site.Id, Kind, "FLT-" + _suffix, "Forklift", false, 1, IsGroup: true, ParentId: handling.Id));
        var all = await QueryAsync<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>(new ListAllReasonCodesQuery(site.Id));
        var version = all.Single(r => r.Id == damage.Id).Version;
        await db.SendAsync(new MoveReasonCodeCommand(damage.Id, version, forklift.Id));

        // The admin list in tree order, with depth and path.
        var mine = (await QueryAsync<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>(new ListAllReasonCodesQuery(site.Id)))
            .Where(r => r.Code.EndsWith(_suffix, StringComparison.Ordinal)).ToList();
        Assert.Equal([handling.Id, forklift.Id, damage.Id], mine.Select(r => r.Id));
        Assert.Equal([0, 1, 2], mine.Select(r => r.Depth));
        Assert.Equal("Handling › Forklift", mine[2].GroupPath);
        Assert.False(mine[0].IsSelectable);

        // Pickers offer the reason with its path, never a group.
        var offered = await QueryAsync<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>>(new ListReasonCodesQuery(Kind, site.Id));
        Assert.Equal("Handling › Forklift", Assert.Single(offered, r => r.Id == damage.Id).GroupPath);
        Assert.DoesNotContain(offered, r => r.Id == handling.Id || r.Id == forklift.Id);

        // The write-off made before any of this still names the same reason.
        var history = (await QueryAsync<GetLotQuery, LotDto>(new GetLotQuery(lot.LotId))).History;
        Assert.Contains(history, e => e.ReasonCode == "DMG-" + _suffix && e.ReasonName == "Damaged");

        // A group can't be chosen; a group can't go inside what it holds; a group with something on in it can't be switched off.
        var current = await db.SendAsync(new ReceiveLotCommand(material.Id, stores.Id, "RL2-" + _suffix, 5m, db.UomId));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new WriteOffLotCommand(current.LotId, current.Version, 1m, db.UomId, handling.Id)));
        all = await QueryAsync<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>(new ListAllReasonCodesQuery(site.Id));
        var outer = all.Single(r => r.Id == handling.Id);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new MoveReasonCodeCommand(handling.Id, outer.Version, forklift.Id)));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UpdateReasonCodeCommand(handling.Id, outer.Version, "Handling", false, 5, false)));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new MoveReasonCodeCommand(damage.Id, version, null)));
    }
}
