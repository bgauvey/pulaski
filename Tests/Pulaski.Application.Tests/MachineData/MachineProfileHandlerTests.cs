using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.MachineData;
using Pulaski.Domain;
using Pulaski.Domain.MachineData;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>Machine profiles, their import, and the machine data on equipment (D-041).</summary>
public class MachineProfileHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _otherSite = Guid.CreateVersion7();
    private readonly Guid _jam;
    private readonly Guid _starved;
    private readonly Guid _setup;

    public MachineProfileHandlerTests()
    {
        Machines.AddSite(_site);
        Machines.AddSite(_otherSite);
        _jam = Machines.AddReason(null, "JAM");
        _starved = Machines.AddReason(_site, "STARVED");
        _setup = Machines.AddReason(null, "SETUP", UtilizationClasses.Planned);
    }

    private InMemoryMachineData Machines => _f.Stores.MachineData;

    private static CancellationToken Ct => CancellationToken.None;

    private CreateMachineProfileHandler Create() => new(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events, _f.Ids);

    private SetProfileStateHandler SetState() => new(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events);

    private SetProfileCodeHandler SetCode() => new(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events);

    private ImportMachineProfileHandler Import() => new(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events);

    private SetEquipmentMachineDataHandler SetMachineData() => new(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events);

    private SetStopReasonOverrideHandler SetOverride() => new(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events);

    private async Task<Guid> NewProfileAsync(string code = "PACKER-A", Guid? site = null) =>
        (await Create().HandleAsync(new CreateMachineProfileCommand(site ?? _site, code, "Case packer A", "Vendor A", "CP-200", null), Ct)).Id;

    private long Version(Guid profileId) => Machines.Profile(profileId).Version;

    [Fact]
    public async Task A_new_profile_starts_with_PackMLs_own_states()
    {
        var id = (await Create().HandleAsync(new CreateMachineProfileCommand(_site, " packer-a ", " Case packer A ", " ", "CP-200", null), Ct)).Id;

        var profile = Machines.Profile(id).Value;
        Assert.Equal(("PACKER-A", "Case packer A", (string?)null, "CP-200"), (profile.Code, profile.Name, profile.Vendor, profile.Model));
        var states = Machines.States(id);
        Assert.Equal(18, states.Count);
        Assert.All(states, s => Assert.Equal(s.RawValue, s.PackMlState));
        Assert.Equal(EventTypes.MachineProfileCreated, _f.Stores.Events.Appended[^1].Type);

        await Assert.ThrowsAsync<ConflictException>(() => NewProfileAsync());
        await Assert.ThrowsAsync<DomainRuleException>(() => NewProfileAsync("OTHER", Guid.CreateVersion7()));
        await Assert.ThrowsAsync<ValidationException>(() => NewProfileAsync("bad code!"));
    }

    [Fact]
    public async Task A_state_value_maps_to_a_PackML_state_and_a_reason_of_the_site()
    {
        var id = await NewProfileAsync();

        // A controller that calls 3 Running: it is PackML Execute.
        await SetState().HandleAsync(new SetProfileStateCommand(id, Version(id), 3, "Running", 6, null, true), Ct);
        await SetState().HandleAsync(new SetProfileStateCommand(id, Version(id), 5, "Suspended", 5, _starved, true), Ct);

        Assert.Equal(("Running", 6), (Machines.States(id).Single(s => s.RawValue == 3).Name, Machines.States(id).Single(s => s.RawValue == 3).PackMlState));
        Assert.Equal(_starved, Machines.States(id).Single(s => s.RawValue == 5).ReasonCodeId);

        var other = Machines.AddReason(_otherSite, "ELSEWHERE");
        await Assert.ThrowsAsync<DomainRuleException>(() => SetState().HandleAsync(new SetProfileStateCommand(id, Version(id), 5, "Suspended", 5, other, true), Ct));
        await Assert.ThrowsAsync<ValidationException>(() => SetState().HandleAsync(new SetProfileStateCommand(id, Version(id), 20, "Odd", 18, null, true), Ct));
        await Assert.ThrowsAsync<ConflictException>(() => SetState().HandleAsync(new SetProfileStateCommand(id, Version(id) - 1, 3, "Run", 6, null, true), Ct));
    }

    [Fact]
    public async Task Every_change_to_the_lists_moves_the_profiles_version_and_no_change_writes_nothing()
    {
        var id = await NewProfileAsync();
        var before = Version(id);

        var changed = await SetCode().HandleAsync(new SetProfileCodeCommand(id, before, StopCodeKinds.StopReason, 101, "Infeed jam", "Mechanical", _jam, true), Ct);
        Assert.NotEqual(before, changed.Version);
        Assert.Equal(EventTypes.MachineProfileCodeSet, _f.Stores.Events.Appended[^1].Type);

        var events = _f.Stores.Events.Appended.Count;
        var same = await SetCode().HandleAsync(new SetProfileCodeCommand(id, changed.Version, StopCodeKinds.StopReason, 101, " Infeed jam ", "Mechanical", _jam, true), Ct);
        Assert.Equal(changed.Version, same.Version);
        Assert.Equal(events, _f.Stores.Events.Appended.Count);

        await Assert.ThrowsAsync<ValidationException>(() =>
            SetCode().HandleAsync(new SetProfileCodeCommand(id, same.Version, "fault", 1, "Odd", null, null, true), Ct));
    }

    [Fact]
    public async Task A_profile_in_use_cant_be_switched_off()
    {
        var id = await NewProfileAsync();
        var packer = Machines.AddEquipment(_site, "PACK-01");
        await SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(packer, Machines.Equipment(packer).Version, id, StateDerivations.OwnTags, null, 300), Ct);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() =>
            new UpdateMachineProfileHandler(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events)
                .HandleAsync(new UpdateMachineProfileCommand(id, Version(id), "Case packer A", null, null, null, IsActive: false), Ct));
        Assert.Contains("PACK-01", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Operators_are_offered_the_ticked_reasons()
    {
        var id = await NewProfileAsync();
        var handler = new SetProfileReasonsHandler(_f.Stores.UnitOfWork, Machines, _f.Stores.Events, _f.Events);

        var first = await handler.HandleAsync(new SetProfileReasonsCommand(id, Version(id), [_jam, _setup]), Ct);
        await handler.HandleAsync(new SetProfileReasonsCommand(id, first.Version, [_setup]), Ct);

        Assert.Equal([_setup], await Machines.ListOfferedAsync(id, Ct));
        var off = Machines.AddReason(null, "OLD", isActive: false);
        await Assert.ThrowsAsync<DomainRuleException>(() => handler.HandleAsync(new SetProfileReasonsCommand(id, Version(id), [_setup, off]), Ct));
    }

    [Fact]
    public async Task An_import_checks_first_and_saves_only_when_asked()
    {
        var id = await NewProfileAsync();
        var csv = MachineProfileCsv.Parse("kind,value,name,category,reason\nstop_reason,101,Infeed jam,Mechanical,jam\nstop_reason,102,Glue low,Supplies,\n");

        var dry = await Import().HandleAsync(new ImportMachineProfileCommand(id, Version(id), csv, DryRun: true), Ct);
        Assert.Equal((false, 2, 0), (dry.Applied, dry.Created, dry.Errors));
        Assert.Empty(Machines.Codes(id));

        var applied = await Import().HandleAsync(new ImportMachineProfileCommand(id, Version(id), csv, DryRun: false), Ct);
        Assert.True(applied.Applied);
        Assert.Equal(Version(id), applied.Version);
        Assert.Equal([(101, (Guid?)_jam), (102, null)], Machines.Codes(id).Select(c => (c.RawValue, c.ReasonCodeId)));
        Assert.Equal(EventTypes.MachineProfileImported, _f.Stores.Events.Appended[^1].Type);

        // The same file again changes nothing.
        var again = await Import().HandleAsync(new ImportMachineProfileCommand(id, applied.Version, csv, DryRun: false), Ct);
        Assert.Equal((2, applied.Version), (again.Unchanged, again.Version));
    }

    [Fact]
    public async Task Each_kind_in_the_file_replaces_its_list_and_the_others_stay()
    {
        var id = await NewProfileAsync();
        await SetCode().HandleAsync(new SetProfileCodeCommand(id, Version(id), StopCodeKinds.StopReason, 101, "Infeed jam", null, _jam, true), Ct);
        await SetCode().HandleAsync(new SetProfileCodeCommand(id, Version(id), StopCodeKinds.Alarm, 9, "Door open", null, null, true), Ct);

        // States 0 to 17 become a controller's own four; stop reason 101 is left out; the alarm isn't mentioned.
        var csv = MachineProfileCsv.Parse("""
            kind,value,name,packml_state,reason
            state,0,No signal,Undefined,
            state,1,Stopped,2,
            state,2,Running,Execute,
            state,3,Starved,Suspended,STARVED
            stop_reason,205,Label jam,,JAM
            """);
        var result = await Import().HandleAsync(new ImportMachineProfileCommand(id, Version(id), csv, DryRun: false), Ct);

        Assert.True(result.Applied);
        Assert.Equal(14 + 1, result.SwitchedOff);
        var active = Machines.States(id).Where(s => s.IsActive).ToList();
        Assert.Equal([(0, 0), (1, 2), (2, 6), (3, 5)], active.Select(s => (s.RawValue, s.PackMlState)));
        Assert.Equal(_starved, active[3].ReasonCodeId);
        Assert.Equal([(9, true), (101, false), (205, true)],
            Machines.Codes(id).OrderBy(c => c.RawValue).Select(c => (c.RawValue, c.IsActive)));
    }

    [Fact]
    public async Task One_bad_row_stops_the_whole_import()
    {
        var id = await NewProfileAsync();
        var csv = MachineProfileCsv.Parse("kind,value,name,reason\nstop_reason,1,Jam,JAM\nstop_reason,1,Jam again,\nstop_reason,2,Odd,NOPE\nwarning,3,,\n");

        var result = await Import().HandleAsync(new ImportMachineProfileCommand(id, Version(id), csv, DryRun: false), Ct);

        Assert.False(result.Applied);
        Assert.Equal(3, result.Errors);
        Assert.Contains(result.Rows, r => r.Message?.Contains("appears earlier", StringComparison.Ordinal) == true);
        Assert.Contains(result.Rows, r => r.Message?.Contains("NOPE isn't an active utilization reason", StringComparison.Ordinal) == true);
        Assert.Empty(Machines.Codes(id));
    }

    [Fact]
    public async Task Equipment_takes_a_profile_of_its_site_once_it_tracks_state()
    {
        var id = await NewProfileAsync();
        var elsewhere = await NewProfileAsync("PACKER-B", _otherSite);
        var packer = Machines.AddEquipment(_site, "PACK-01");
        var rack = Machines.AddEquipment(_site, "RACK-01", tracksState: false);

        await SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(packer, Machines.Equipment(packer).Version, id, StateDerivations.OwnTags, null, 600), Ct);

        Assert.Equal((id, TimeSpan.FromMinutes(10)), (Machines.Equipment(packer).Value.MachineProfileId, Machines.Equipment(packer).Value.MicroStopThreshold));
        Assert.Equal(EventTypes.EquipmentMachineDataSet, _f.Stores.Events.Appended[^1].Type);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(rack, Machines.Equipment(rack).Version, id, StateDerivations.OwnTags, null, 300), Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(packer, Machines.Equipment(packer).Version, elsewhere, StateDerivations.OwnTags, null, 300), Ct));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(packer, Machines.Equipment(packer).Version, id, StateDerivations.OwnTags, null, 3601), Ct));
        await Assert.ThrowsAsync<ConflictException>(() =>
            SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(packer, 1, id, StateDerivations.OwnTags, null, 300), Ct));
    }

    [Fact]
    public async Task A_line_takes_its_state_from_its_machines_or_one_constraint_machine_below_it()
    {
        var line = Machines.AddEquipment(_site, "PACK-LINE");
        var filler = Machines.AddEquipment(_site, "FILL-01", parentId: line);
        var conveyor = Machines.AddEquipment(_site, "CONV-01", tracksState: false, parentId: line);
        var elsewhere = Machines.AddEquipment(_site, "EXT-01");

        await SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(line, Machines.Equipment(line).Version, null, StateDerivations.Series, null, 300), Ct);
        Assert.Equal(StateDerivations.Series, Machines.Equipment(line).Value.StateDerivation);

        await SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(line, Machines.Equipment(line).Version, null, StateDerivations.ConstraintMachine, filler, 300), Ct);
        Assert.Equal(filler, Machines.Equipment(line).Value.ConstraintEquipmentId);

        foreach (var wrong in new[] { conveyor, elsewhere })
        {
            await Assert.ThrowsAsync<DomainRuleException>(() =>
                SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(line, Machines.Equipment(line).Version, null, StateDerivations.ConstraintMachine, wrong, 300), Ct));
        }

        await Assert.ThrowsAsync<ValidationException>(() =>
            SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(line, Machines.Equipment(line).Version, null, StateDerivations.ConstraintMachine, null, 300), Ct));
        await Assert.ThrowsAsync<ValidationException>(() =>
            SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(line, Machines.Equipment(line).Version, null, StateDerivations.Parallel, filler, 300), Ct));
    }

    [Fact]
    public async Task A_machine_can_give_one_of_its_profiles_stop_codes_its_own_reason()
    {
        var id = await NewProfileAsync();
        await SetCode().HandleAsync(new SetProfileCodeCommand(id, Version(id), StopCodeKinds.StopReason, 101, "Infeed jam", null, _jam, true), Ct);
        var packer = Machines.AddEquipment(_site, "PACK-01");

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            SetOverride().HandleAsync(new SetStopReasonOverrideCommand(packer, Machines.Equipment(packer).Version, 101, _starved, true), Ct));

        await SetMachineData().HandleAsync(new SetEquipmentMachineDataCommand(packer, Machines.Equipment(packer).Version, id, StateDerivations.OwnTags, null, 300), Ct);
        var before = Machines.Equipment(packer).Version;
        var set = await SetOverride().HandleAsync(new SetStopReasonOverrideCommand(packer, before, 101, _starved, true), Ct);

        Assert.NotEqual(before, set.Version);
        Assert.Equal((_starved, true), Machines.Override(packer, 101));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            SetOverride().HandleAsync(new SetStopReasonOverrideCommand(packer, set.Version, 999, _starved, true), Ct));

        var removed = await SetOverride().HandleAsync(new SetStopReasonOverrideCommand(packer, set.Version, 101, null, false), Ct);
        Assert.Equal(((Guid?)null, false), Machines.Override(packer, 101));
        Assert.Equal(EventTypes.EquipmentStopReasonOverrideSet, _f.Stores.Events.Appended[^1].Type);
        Assert.NotEqual(set.Version, removed.Version);
    }
}
