using Dapper;
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
using Pulaski.Domain;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Stops from machine data, coding them and the status board end to end (D-050): real SQL, as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class StopTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private async Task<IReadOnlyList<DowntimeDto>> StopsAsync(ListStopsQuery query) => (await QueryAsync<ListStopsQuery, StopPageDto>(query)).Stops;

    [Fact]
    public async Task Stops_are_opened_closed_mapped_coded_and_shown_on_the_board()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "SENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "SS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "SL-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(line.Id, "SF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse capper = await db.SendAsync(new CreateEquipmentCommand(line.Id, "SC-" + _s, "Capper", null, "work_unit", "machine", true, false, false, null));

        CreatedResponse jam = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "SJAM-" + _s, "Jam", false, 10, UtilizationClasses.Downtime));
        CreatedResponse starved = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "SSTV-" + _s, "Starved", false, 20, UtilizationClasses.Downtime));
        CreatedResponse other = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "SOTH-" + _s, "Other", true, 30, UtilizationClasses.Downtime));

        // The filler's profile: code 101 is a jam, Suspended is starved; other is offered too.
        CreatedResponse profile = await db.SendAsync(new CreateMachineProfileCommand(site.Id, "SP-" + _s, "Filler", null, null, null));
        // A file's states replace the profile's, so it lists every state the filler sends.
        var csv = MachineProfileCsv.Parse(
            $"kind,value,name,packml_state,reason\nstop_reason,101,Infeed jam,,SJAM-{_s}\nstate,2,Stopped,Stopped,\nstate,5,Suspended,Suspended,SSTV-{_s}\nstate,6,Execute,Execute,\n");
        var imported = await db.SendAsync(new ImportMachineProfileCommand(profile.Id, profile.Version, csv, DryRun: false));
        await db.SendAsync(new SetProfileReasonsCommand(profile.Id, imported.Version, [other.Id]));
        var fillerNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(filler.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(filler.Id, fillerNow.Version, profile.Id, StateDerivations.OwnTags, null, 120));
        var lineNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(line.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(line.Id, lineNow.Version, null, StateDerivations.Series, null, 300));

        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "SG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "B", "Broker", EdgeProtocols.SparkplugB, new Dictionary<string, string> { ["host"] = "b" }));
        CreatedResponse fillState = await db.SendAsync(new CreateEquipmentTagCommand(filler.Id, TagRoles.State, connection.Id, "P/N/F:State", 1, 0, null));
        CreatedResponse fillStop = await db.SendAsync(new CreateEquipmentTagCommand(filler.Id, TagRoles.StopReason, connection.Id, "P/N/F:Stop", 1, 0, null));
        CreatedResponse capState = await db.SendAsync(new CreateEquipmentTagCommand(capper.Id, TagRoles.State, connection.Id, "P/N/C:State", 1, 0, null));

        var now = DateTimeOffset.UtcNow;
        var t0 = now.AddMinutes(-40);
        EdgeSignalDto S(long seq, Guid tag, int minutes, decimal value) => new() { Sequence = seq, TagId = tag, OccurredAt = t0.AddMinutes(minutes), Value = value };
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, Guid.CreateVersion7(), null,
        [
            S(1, fillState.Id, 0, 6), S(2, capState.Id, 0, 6),
            S(3, fillState.Id, 5, 2), S(4, fillStop.Id, 5, 101), S(5, fillState.Id, 15, 6),   // a 10-minute jam
            S(6, fillState.Id, 20, 5), S(7, fillState.Id, 21, 6),                             // a 1-minute micro-stop
            S(8, fillState.Id, 30, 5),                                                         // starved, ongoing for 10 minutes
        ]));

        var all = await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, line.Id, now.AddHours(-1), now.AddMinutes(1), false, true)));
        Assert.Equal([(30, false, "SSTV-" + _s), (20, true, "SSTV-" + _s), (5, false, "SJAM-" + _s)],
            all.Select(s => ((int)Math.Round((s.StartedAt - t0).TotalMinutes), s.IsMicroStop, s.MappedReasonCode)));
        Assert.Null(all[0].EndedAt);
        Assert.Equal((600, "Infeed jam"), (all[2].DurationSeconds, all[2].StopCodeText));

        var uncoded = await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, null, now.AddHours(-1), now.AddMinutes(1), true, false)));
        Assert.Empty(uncoded);    // both have mapped reasons

        var reasons = await QueryAsync<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>>(new ListCodingReasonsQuery(filler.Id));
        Assert.Equal(["SJAM-" + _s, "SSTV-" + _s, "SOTH-" + _s], reasons.Select(r => r.Code));

        var jamStop = all[2];
        await db.SendAsync(new CodeDowntimeCommand(jamStop.Id, jamStop.StartedAt, other.Id, "Wrong carton"));
        var coded = (await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, filler.Id, now.AddHours(-1), now.AddMinutes(1), false, false))))
            .Single(s => s.Id == jamStop.Id);
        Assert.Equal(("SOTH-" + _s, "SJAM-" + _s, "Wrong carton", "downtime"), (coded.OperatorReasonCode, coded.MappedReasonCode, coded.Comment, coded.UtilizationClass));

        var board = (await QueryAsync<GetMachineStatusQuery, IReadOnlyList<MachineStatusDto>>(new GetMachineStatusQuery(site.Id))).ToDictionary(m => m.Code);
        Assert.Equal((OeeCategories.WaitingExternal, true), (board["SL-" + _s].Category, board["SL-" + _s].IsDerived));
        Assert.Equal(line.Id, board["SF-" + _s].ParentId);
        var fillerTile = board["SF-" + _s];
        Assert.Equal(("SSTV-" + _s, t0.AddMinutes(30)), (fillerTile.OpenStop!.ReasonCode, fillerTile.OpenStop.StartedAt));
        Assert.Equal(OeeCategories.Producing, board["SC-" + _s].Category);
    }

    [Fact]
    public async Task Stops_entered_split_and_merged_replace_the_ones_they_adjust_in_the_list_and_in_utilization()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "AENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "AS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "AL-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(line.Id, "AF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse jam = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "AJAM-" + _s, "Jam", false, 10, UtilizationClasses.Downtime));
        CreatedResponse setup = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "ASET-" + _s, "Setup", false, 20, UtilizationClasses.Planned));
        var fillerNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(filler.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(filler.Id, fillerNow.Version, null, StateDerivations.OwnTags, null, 120));

        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "AG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "B", "Broker", EdgeProtocols.SparkplugB, new Dictionary<string, string> { ["host"] = "b" }));
        CreatedResponse state = await db.SendAsync(new CreateEquipmentTagCommand(filler.Id, TagRoles.State, connection.Id, "P/N/AF:State", 1, 0, null));

        var now = DateTimeOffset.UtcNow;
        var t0 = now.AddMinutes(-40);
        EdgeSignalDto S(long seq, int minutes, decimal value) => new() { Sequence = seq, TagId = state.Id, OccurredAt = t0.AddMinutes(minutes), Value = value };
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, Guid.CreateVersion7(), null,
        [
            S(1, 0, 6),
            S(2, 5, 2), S(3, 15, 6),     // a 10-minute stop
            S(4, 20, 5), S(5, 21, 6),    // a 1-minute micro-stop
            S(6, 30, 2),                 // stopped, still open
        ]));

        async Task<IReadOnlyList<DowntimeDto>> ListAsync(bool superseded = false) =>
            await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, filler.Id, t0, now.AddMinutes(1), false, true, superseded)));
        async Task<IReadOnlyList<UtilizationRowDto>> UtilizationAsync()
        {
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var report = await QueryAsync<GetOeeReportQuery, OeeReportDto>(new GetOeeReportQuery(site.Id, today.AddDays(-1), today, filler.Id));
            return [.. report.Utilization.Where(u => u.EquipmentId == filler.Id)];
        }

        var stopped = (await ListAsync()).Single(s => s.StartedAt == t0.AddMinutes(5));
        await db.SendAsync(new CodeDowntimeCommand(stopped.Id, stopped.StartedAt, jam.Id, null));

        // Split at 10 minutes: the stop is kept, superseded, and its two parts keep its coding.
        var split = await db.SendAsync(new SplitStopCommand(stopped.Id, stopped.StartedAt, t0.AddMinutes(10), "Jam, then no cartons"));
        var current = await ListAsync();
        Assert.Equal([(30, "machine"), (20, "machine"), (10, "split"), (5, "split")], current.Select(s => ((int)Math.Round((s.StartedAt - t0).TotalMinutes), s.Source)));
        Assert.All(current.Where(s => s.Source == "split"), s => Assert.Equal(("AJAM-" + _s, stopped.Id, "Jam, then no cartons"), (s.OperatorReasonCode, s.ReplacesIds.Single(), s.AdjustmentComment)));
        var history = await ListAsync(superseded: true);
        Assert.NotNull(Assert.Single(history, s => s.Id == stopped.Id).SupersededAt);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new CodeDowntimeCommand(stopped.Id, stopped.StartedAt, jam.Id, null)));
        var jammed = Assert.Single(await UtilizationAsync(), u => u.Use == "down" && u.ReasonCodeId == jam.Id);
        Assert.Equal((0.17m, 2), (jammed.Hours, jammed.Stops));

        // Merge the second part with the micro-stop: one stop from 10 to 21 minutes, coded as the part was; no micro-stop left.
        var micro = current.Single(s => s.IsMicroStop);
        await db.SendAsync(new MergeStopsCommand([split.Stops[1], new StopKeyDto(micro.Id, micro.StartedAt)], "One jam; the machine flickered"));
        var open = current.Single(s => s.EndedAt is null);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new MergeStopsCommand(
            [split.Stops[0], new StopKeyDto(open.Id, open.StartedAt)], "x")));
        var utilization = await UtilizationAsync();
        jammed = Assert.Single(utilization, u => u.Use == "down" && u.ReasonCodeId == jam.Id);
        Assert.Equal((0.27m, 2), (jammed.Hours, jammed.Stops));
        Assert.DoesNotContain(utilization, u => u.Use == "micro_stop");

        // A setup the machine didn't report, entered by hand between the merged stop and the open one; it may not overlap either.
        await db.SendAsync(new EnterStopCommand(filler.Id, t0.AddMinutes(22), t0.AddMinutes(25), setup.Id, "Changed the carton size"));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new EnterStopCommand(filler.Id, t0.AddMinutes(20), t0.AddMinutes(23), setup.Id, "x")));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new EnterStopCommand(filler.Id, t0.AddMinutes(28), t0.AddMinutes(31), setup.Id, "x")));
        var manual = Assert.Single(await ListAsync(), s => s.Source == "manual");
        Assert.Equal(("ASET-" + _s, "planned", false), (manual.OperatorReasonCode, manual.UtilizationClass, manual.AdjustedBy is null));
        var planned = Assert.Single(await UtilizationAsync(), u => u.Use == "planned");
        Assert.Equal((setup.Id, 0.05m, 1), (planned.ReasonCodeId, planned.Hours, planned.Stops));
    }

    [Fact]
    public async Task Stops_are_retimed_removed_and_undone_and_late_machine_data_is_trimmed_around_stops_entered_by_hand()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "RENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "RS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(site.Id, "RF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse jam = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "RJAM-" + _s, "Jam", false, 10, UtilizationClasses.Downtime));
        var fillerNow = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(filler.Id));
        await db.SendAsync(new SetEquipmentMachineDataCommand(filler.Id, fillerNow.Version, null, StateDerivations.OwnTags, null, 60));
        CreatedResponse node = await db.SendAsync(new CreateEdgeNodeCommand(site.Id, "RG-" + _s, "Gateway", null));
        CreatedResponse connection = await db.SendAsync(new CreateEdgeConnectionCommand(node.Id, "B", "Broker", EdgeProtocols.SparkplugB, new Dictionary<string, string> { ["host"] = "b" }));
        CreatedResponse state = await db.SendAsync(new CreateEquipmentTagCommand(filler.Id, TagRoles.State, connection.Id, "P/N/RF:State", 1, 0, null));

        var now = DateTimeOffset.UtcNow;
        var t0 = now.AddMinutes(-90);
        var stream = Guid.CreateVersion7();
        EdgeSignalDto S(long seq, int minutes, decimal value) => new() { Sequence = seq, TagId = state.Id, OccurredAt = t0.AddMinutes(minutes), Value = value };
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, stream, null, [S(1, 0, 6), S(2, 5, 2), S(3, 15, 6)]));   // a 10-minute stop

        async Task<List<DowntimeDto>> CurrentAsync() => [.. (await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, filler.Id, t0, now.AddMinutes(1), false, true))))
            .OrderBy(s => s.StartedAt)];
        async Task<List<DowntimeDto>> AllAsync() => [.. await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, filler.Id, t0, now.AddMinutes(1), false, true, true)))];
        var machine = Assert.Single(await CurrentAsync());

        // Change its times, then undo that: the machine's stop is back, as a copy, with its times.
        var retimed = Assert.Single((await db.SendAsync(new RetimeStopCommand(machine.Id, machine.StartedAt, t0.AddMinutes(6), t0.AddMinutes(14), "Clock offset"))).Stops);
        Assert.Equal(("retime", t0.AddMinutes(6), (DateTimeOffset?)t0.AddMinutes(14)), Single(await CurrentAsync()));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new RetimeStopCommand(retimed.Id, retimed.StartedAt, t0.AddMinutes(6), t0.AddMinutes(14), "Same")));
        var restored = Assert.Single((await db.SendAsync(new UndoStopAdjustmentCommand(retimed.Id, retimed.StartedAt, "Offset was right after all"))).Stops);
        Assert.Equal(("restore", t0.AddMinutes(5), (DateTimeOffset?)t0.AddMinutes(15)), Single(await CurrentAsync()));
        Assert.Equal("Undone: Offset was right after all", (await AllAsync()).Single(s => s.Id == retimed.Id).SupersededNote);

        // Split it, split one part again: undoing the first split is refused, since a part of it was adjusted since.
        var parts = (await db.SendAsync(new SplitStopCommand(restored.Id, restored.StartedAt, t0.AddMinutes(10), "Jam, then waiting"))).Stops;
        await db.SendAsync(new SplitStopCommand(parts[1].Id, parts[1].StartedAt, t0.AddMinutes(12), "Waiting had two causes"));
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new UndoStopAdjustmentCommand(parts[0].Id, parts[0].StartedAt, "Back to one")));

        // A stop entered by hand in error is removed; a machine's stop can't be.
        var entered = Assert.Single((await db.SendAsync(new EnterStopCommand(filler.Id, t0.AddMinutes(30), t0.AddMinutes(40), jam.Id, "Power cut"))).Stops);
        await db.SendAsync(new VoidStopCommand(entered.Id, entered.StartedAt, "Wrong machine"));
        Assert.DoesNotContain(await CurrentAsync(), s => s.Id == entered.Id);
        Assert.Equal("Removed: Wrong machine", (await AllAsync()).Single(s => s.Id == entered.Id).SupersededNote);
        await Assert.ThrowsAsync<DomainRuleException>(() => db.SendAsync(new VoidStopCommand(parts[0].Id, parts[0].StartedAt, "Not mine")));

        // An outage entered by hand for 50–60; the edge's buffer then delivers the machine's own stop, 48–62: only the edges of it are kept.
        await db.SendAsync(new EnterStopCommand(filler.Id, t0.AddMinutes(50), t0.AddMinutes(60), jam.Id, "Line down, edge offline"));
        await db.SendAsync(new IngestEdgeSignalsCommand(node.Id, stream, null, [S(4, 48, 2), S(5, 62, 6)]));
        var late = (await CurrentAsync()).Where(s => s.StartedAt >= t0.AddMinutes(45)).ToList();
        Assert.Equal(
            [("trim", t0.AddMinutes(48), (DateTimeOffset?)t0.AddMinutes(50)), ("manual", t0.AddMinutes(50), t0.AddMinutes(60)), ("trim", t0.AddMinutes(60), t0.AddMinutes(62))],
            late.Select(s => (s.Source, s.StartedAt, s.EndedAt)));
        Assert.Equal("Covered by a stop entered or adjusted by hand", (await AllAsync()).Single(s => s.Source == "machine" && s.StartedAt == t0.AddMinutes(48)).SupersededNote);

        // Current stops never overlap.
        var current = await CurrentAsync();
        Assert.True(current.Zip(current.Skip(1)).All(p => p.First.EndedAt <= p.Second.StartedAt));

        static (string Source, DateTimeOffset Start, DateTimeOffset? End) Single(List<DowntimeDto> stops) =>
            Assert.Single(stops) is var s ? (s.Source, s.StartedAt, s.EndedAt) : default;
    }

    [Fact]
    public async Task Stops_carry_their_reason_groups_and_only_reasons_without_history_become_groups()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "GENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "GS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(site.Id, "GL-" + _s, "Pack line", null, "work_center", "production_line", true, true, false, null));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(line.Id, "GF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));
        CreatedResponse mechanical = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "GMECH-" + _s, "Mechanical", false, 10, IsGroup: true));
        CreatedResponse jam = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "GJAM-" + _s, "Jam", false, 10, UtilizationClasses.Downtime, ParentId: mechanical.Id));
        CreatedResponse spare = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "GSPR-" + _s, "Spare", false, 20, UtilizationClasses.Downtime));
        CreatedResponse empty = await db.SendAsync(new CreateReasonCodeCommand(site.Id, "utilization", "GEMP-" + _s, "Electrical", false, 30, IsGroup: true));

        var now = DateTimeOffset.UtcNow;
        await db.SendAsync(new EnterStopCommand(filler.Id, now.AddMinutes(-30), now.AddMinutes(-20), jam.Id, "Jammed"));

        // The stops list and the OEE report name the group a reason is filed under.
        var stop = Assert.Single(await StopsAsync(new ListStopsQuery(new StopFilter(site.Id, filler.Id, now.AddHours(-1), now.AddMinutes(1), false, true))));
        Assert.Equal("Mechanical", stop.ReasonGroupPath);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var report = await QueryAsync<GetOeeReportQuery, OeeReportDto>(new GetOeeReportQuery(site.Id, today.AddDays(-1), today, filler.Id));
        var jammed = Assert.Single(report.Utilization, u => u.EquipmentId == filler.Id && u.ReasonCodeId == jam.Id);
        Assert.Equal(["GMECH-" + _s], jammed.ReasonGroups.Select(g => g.Code));

        async Task<long> VersionAsync(Guid id) =>
            (await QueryAsync<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>(new ListAllReasonCodesQuery(site.Id))).Single(r => r.Id == id).Version;

        // A reason that recorded a stop stays a reason; one never used becomes a group, and is no longer offered.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new ConvertReasonCodeCommand(jam.Id, await VersionAsync(jam.Id), true)));
        Assert.Contains("ts.downtime", refused.Message, StringComparison.Ordinal);
        await db.SendAsync(new ConvertReasonCodeCommand(spare.Id, await VersionAsync(spare.Id), true));
        var offered = await QueryAsync<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>>(new ListReasonCodesQuery("utilization", site.Id));
        Assert.DoesNotContain(offered, r => r.Id == spare.Id);

        // A group with reasons in it stays a group; an empty one becomes a reason, a utilization one only with its class.
        await Assert.ThrowsAsync<DomainRuleException>(async () => await db.SendAsync(new ConvertReasonCodeCommand(mechanical.Id, await VersionAsync(mechanical.Id), false, UtilizationClasses.Downtime)));
        await Assert.ThrowsAsync<ValidationException>(async () => await db.SendAsync(new ConvertReasonCodeCommand(empty.Id, await VersionAsync(empty.Id), false)));
        await db.SendAsync(new ConvertReasonCodeCommand(empty.Id, await VersionAsync(empty.Id), false, UtilizationClasses.Downtime));
        offered = await QueryAsync<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>>(new ListReasonCodesQuery("utilization", site.Id));
        Assert.Equal("downtime", Assert.Single(offered, r => r.Id == empty.Id).UtilizationClass);
    }

    [Fact]
    public async Task More_than_a_thousand_stops_come_in_pages_that_neither_skip_nor_repeat()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "PENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "PS-" + _s, "Site", null, "site", "site", false, false, false, "UTC"));
        CreatedResponse filler = await db.SendAsync(new CreateEquipmentCommand(site.Id, "PF-" + _s, "Filler", null, "work_unit", "machine", true, false, false, null));

        // 1,203 closed stops, three to each start time; one of each three replaced by a split (D-069), so it shows only when asked for.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-50);
        await using (var admin = await db.AdminDataSource.OpenConnectionAsync())
        {
            await admin.ExecuteAsync(
                """
                INSERT INTO ts.downtime (started_at, ended_at, equipment_id, packml_state, superseded_at, superseded_by)
                SELECT @t0 + (g / 3) * interval '1 second', @t0 + (g / 3) * interval '1 second' + interval '1 second', @filler, 2,
                       CASE WHEN g % 3 = 0 THEN now() END, CASE WHEN g % 3 = 0 THEN 'test' END
                FROM generate_series(0, 1202) AS g
                """,
                new { t0, filler = filler.Id });
        }

        async Task<List<DowntimeDto>> AllAsync(bool superseded)
        {
            var all = new List<DowntimeDto>();
            string? after = null;
            var pages = 0;
            do
            {
                var page = await QueryAsync<ListStopsQuery, StopPageDto>(new ListStopsQuery(
                    new StopFilter(site.Id, filler.Id, t0.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), false, true, superseded), after, 250));
                all.AddRange(page.Stops);
                after = page.Next;
                pages++;
            }
            while (after is not null && pages < 20);

            return all;
        }

        var current = await AllAsync(superseded: false);
        Assert.Equal(802, current.Count);
        Assert.Equal(802, current.Select(s => s.Id).Distinct().Count());

        var everything = await AllAsync(superseded: true);
        Assert.Equal(1203, everything.Count);
        Assert.Equal(1203, everything.Select(s => s.Id).Distinct().Count());

        // Newest first; at one start time, the current stops before the replaced one.
        Assert.True(everything.Zip(everything.Skip(1)).All(p => p.First.StartedAt >= p.Second.StartedAt));
        Assert.True(everything.GroupBy(s => s.StartedAt).All(g => g.SkipWhile(s => s.SupersededAt is null).All(s => s.SupersededAt is not null)));

        // The default page is 500; a cursor that isn't one is refused.
        var first = await QueryAsync<ListStopsQuery, StopPageDto>(new ListStopsQuery(new StopFilter(site.Id, filler.Id, t0.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), false, true)));
        Assert.Equal((500, true), (first.Stops.Count, first.Next is not null));
        await Assert.ThrowsAsync<ValidationException>(() => QueryAsync<ListStopsQuery, StopPageDto>(new ListStopsQuery(
            new StopFilter(site.Id, filler.Id, t0, DateTimeOffset.UtcNow, false, true), "not-a-cursor")));
    }
}
