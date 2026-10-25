using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
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

        var all = await QueryAsync<ListStopsQuery, IReadOnlyList<DowntimeDto>>(new ListStopsQuery(new StopFilter(site.Id, line.Id, now.AddHours(-1), now.AddMinutes(1), false, true)));
        Assert.Equal([(30, false, "SSTV-" + _s), (20, true, "SSTV-" + _s), (5, false, "SJAM-" + _s)],
            all.Select(s => ((int)Math.Round((s.StartedAt - t0).TotalMinutes), s.IsMicroStop, s.MappedReasonCode)));
        Assert.Null(all[0].EndedAt);
        Assert.Equal((600, "Infeed jam"), (all[2].DurationSeconds, all[2].StopCodeText));

        var uncoded = await QueryAsync<ListStopsQuery, IReadOnlyList<DowntimeDto>>(new ListStopsQuery(new StopFilter(site.Id, null, now.AddHours(-1), now.AddMinutes(1), true, false)));
        Assert.Empty(uncoded);    // both have mapped reasons

        var reasons = await QueryAsync<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>>(new ListCodingReasonsQuery(filler.Id));
        Assert.Equal(["SJAM-" + _s, "SSTV-" + _s, "SOTH-" + _s], reasons.Select(r => r.Code));

        var jamStop = all[2];
        await db.SendAsync(new CodeDowntimeCommand(jamStop.Id, jamStop.StartedAt, other.Id, "Wrong carton"));
        var coded = (await QueryAsync<ListStopsQuery, IReadOnlyList<DowntimeDto>>(new ListStopsQuery(new StopFilter(site.Id, filler.Id, now.AddHours(-1), now.AddMinutes(1), false, false))))
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
            await QueryAsync<ListStopsQuery, IReadOnlyList<DowntimeDto>>(new ListStopsQuery(new StopFilter(site.Id, filler.Id, t0, now.AddMinutes(1), false, true, superseded)));
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
}
