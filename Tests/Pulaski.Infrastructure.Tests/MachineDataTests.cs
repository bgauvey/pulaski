using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Utilization reasons, machine profiles and equipment machine data end to end (D-050): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class MachineDataTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private Task<MachineProfileDetailDto> ProfileAsync(Guid id) => QueryAsync<GetMachineProfileQuery, MachineProfileDetailDto>(new GetMachineProfileQuery(id));

    private Task<EquipmentDto> EquipmentAsync(Guid id) => QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(id));

    [Fact]
    public async Task A_profile_is_imported_given_to_machines_and_overridden_on_one()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "MENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "MS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "ML-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse packer = await db.SendAsync(new CreateEquipmentCommand(line.Id, "MP-" + _s, "Case packer", null, "work_unit", "machine", true, false, false, null));

        // Utilization reasons carry a class; other kinds don't.
        CreatedResponse jam = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "JAM-" + _s, "Jam", false, 10, UtilizationClasses.Downtime));
        CreatedResponse setup = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "SET-" + _s, "Setup", false, 20, UtilizationClasses.Planned));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "NOCLS-" + _s, "No class", false, 30)));
        await Assert.ThrowsAsync<ValidationException>(() => db.SendAsync(new CreateReasonCodeCommand(site.Id, "scrap", "CLS-" + _s, "Class", false, 30, UtilizationClasses.Runtime)));
        var reasons = await QueryAsync<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>>(new ListReasonCodesQuery("utilization", site.Id));
        Assert.Equal("planned", reasons.Single(r => r.Id == setup.Id).UtilizationClass);

        CreatedResponse profile = await db.SendAsync(new CreateMachineProfileCommand(site.Id, "cp-" + _s, "Case packer", "Vendor A", "CP-200", null));
        var detail = await ProfileAsync(profile.Id);
        Assert.Equal((18, "Execute"), (detail.States.Count, detail.States[6].PackMlName));
        Assert.Equal(profile.Version, detail.Profile.Version);

        var csv = MachineProfileCsv.Parse($"""
            kind,value,name,packml_state,category,reason
            state,0,No signal,0,,
            state,1,Stopped,Stopped,,
            state,2,Running,Execute,,
            stop_reason,101,Infeed jam,,Mechanical,JAM-{_s}
            stop_reason,102,Glue low,,Supplies,
            alarm,9,Door open,,Safety,
            """);
        var dry = await db.SendAsync(new ImportMachineProfileCommand(profile.Id, detail.Profile.Version, csv, DryRun: true));
        Assert.Equal((false, 3, 3, 15), (dry.Applied, dry.Created, dry.Updated, dry.SwitchedOff));
        Assert.Empty((await ProfileAsync(profile.Id)).Codes);

        var imported = await db.SendAsync(new ImportMachineProfileCommand(profile.Id, detail.Profile.Version, csv, DryRun: false));
        Assert.True(imported.Applied);
        detail = await ProfileAsync(profile.Id);
        Assert.Equal(imported.Version, detail.Profile.Version);
        Assert.Equal([(0, 0), (1, 2), (2, 6)], detail.States.Where(s => s.IsActive).Select(s => (s.RawValue, s.PackMlState)));
        Assert.Equal([("stop_reason", 101, "JAM-" + _s), ("stop_reason", 102, null), ("alarm", 9, null)], detail.Codes.Select(c => (c.Kind, c.RawValue, c.ReasonCode)));
        var listed = Assert.Single(await QueryAsync<ListMachineProfilesQuery, IReadOnlyList<MachineProfileDto>>(new ListMachineProfilesQuery(site.Id)));
        Assert.Equal((2, 1, 0), (listed.StopCodeCount, listed.UnmappedStopCodeCount, listed.MachineCount));

        // A stale version is refused.
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new SetProfileCodeCommand(profile.Id, detail.Profile.Version - 1, "stop_reason", 102, "Glue low", null, jam.Id, true)));
        await db.SendAsync(new SetProfileReasonsCommand(profile.Id, detail.Profile.Version, [setup.Id]));
        Assert.Equal([setup.Id], (await ProfileAsync(profile.Id)).OfferedReasonIds);

        // The packer takes the profile and a 10-minute threshold; new equipment starts at 5 minutes, its own state.
        var packerNow = await EquipmentAsync(packer.Id);
        Assert.Equal((300, "own_tags", (Guid?)null), (packerNow.MicroStopThresholdSeconds, packerNow.StateDerivation, packerNow.MachineProfileId));
        await db.SendAsync(new SetEquipmentMachineDataCommand(packer.Id, packerNow.Version, profile.Id, StateDerivations.OwnTags, null, 600));
        packerNow = await EquipmentAsync(packer.Id);
        Assert.Equal((600, (Guid?)profile.Id), (packerNow.MicroStopThresholdSeconds, packerNow.MachineProfileId));

        // The line follows its packer.
        var lineNow = await EquipmentAsync(line.Id);
        await Assert.ThrowsAsync<Pulaski.Domain.DomainRuleException>(() =>
            db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, null, StateDerivations.ConstraintMachine, site.Id, 300)));
        await db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, null, StateDerivations.ConstraintMachine, packer.Id, 120));
        Assert.Equal(("constraint_machine", (Guid?)packer.Id, 120), ((await EquipmentAsync(line.Id)).StateDerivation, (await EquipmentAsync(line.Id)).ConstraintEquipmentId, (await EquipmentAsync(line.Id)).MicroStopThresholdSeconds));

        // On this packer, glue low is a jam.
        await db.SendAsync(new SetStopReasonOverrideCommand(packer.Id, packerNow.Version, 102, jam.Id, true));
        var machine = Assert.Single((await ProfileAsync(profile.Id)).Machines);
        Assert.Equal((102, "JAM-" + _s), (Assert.Single(machine.Overrides).RawValue, machine.Overrides[0].ReasonCode));
        await db.SendAsync(new SetStopReasonOverrideCommand(packer.Id, machine.Version, 102, null, false));
        Assert.Empty(Assert.Single((await ProfileAsync(profile.Id)).Machines).Overrides);

        // A profile in use stays on.
        var current = (await ProfileAsync(profile.Id)).Profile;
        await Assert.ThrowsAsync<Pulaski.Domain.DomainRuleException>(() =>
            db.SendAsync(new UpdateMachineProfileCommand(profile.Id, current.Version, current.Name, current.Vendor, current.Model, null, IsActive: false)));
    }
}
