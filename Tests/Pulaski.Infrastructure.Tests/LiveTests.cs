using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Live;
using Pulaski.Contracts.Common;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Every event appended, and every machine ingest touched, is heard on the live channel once its transaction commits (#143).</summary>
[Collection(DatabaseTests.Name)]
public sealed class LiveTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private sealed record Notice(string Type, string Kind, Guid Id);

    private static async Task<List<Notice>> HearAsync(NpgsqlConnection listening, ConcurrentQueue<string> heard, Func<Notice, bool> until, int tries = 50)
    {
        var notices = new List<Notice>();
        for (var i = 0; i < tries && !notices.Any(until); i++)
        {
            await listening.WaitAsync(TimeSpan.FromMilliseconds(100));
            while (heard.TryDequeue(out var payload))
            {
                notices.Add(JsonSerializer.Deserialize<Notice>(payload, JsonSerializerOptions.Web)!);
            }
        }

        return notices;
    }

    [Fact]
    public async Task An_event_and_a_machine_change_are_heard_once_committed_and_not_before()
    {
        await using var listening = await db.AdminDataSource.OpenConnectionAsync();
        var heard = new ConcurrentQueue<string>();
        listening.Notification += (_, e) => heard.Enqueue(e.Payload);
        await using (var listen = new NpgsqlCommand($"LISTEN {LiveChannel.Channel}", listening))
        {
            await listen.ExecuteNonQueryAsync();
        }

        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "LV-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        var created = await HearAsync(listening, heard, n => n.Id == enterprise.Id);
        Assert.Contains(created, n => n.Id == enterprise.Id && n.Kind == "equipment" && n.Type.StartsWith("pulaski.equipment.", StringComparison.Ordinal));

        // A machine's change is notified in its transaction: nothing until it commits, then once (repeats in one transaction are one).
        var machine = Guid.CreateVersion7();
        await using var scope = db.Services.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var live = scope.ServiceProvider.GetRequiredService<ILiveSignals>();
        await using (var transaction = await unitOfWork.BeginAsync(CancellationToken.None))
        {
            await live.MachinesChangedAsync([machine], CancellationToken.None);
            await live.MachinesChangedAsync([machine], CancellationToken.None);
            Assert.DoesNotContain(await HearAsync(listening, heard, _ => false, tries: 3), n => n.Id == machine);
            await transaction.CommitAsync(CancellationToken.None);
        }

        var notices = await HearAsync(listening, heard, n => n.Id == machine);
        Assert.Equal(("machine", "machine"), (Assert.Single(notices, n => n.Id == machine).Type, notices.Single(n => n.Id == machine).Kind));
    }
}
