using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Inventory;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Stops from machine data, coding them and the status board end to end (D-041): real SQL, as the application login.</summary>
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
}
