using System.Text.Json;
using Pulaski.Application.Live;
using Pulaski.Infrastructure.Events;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Live;

/// <summary>Notifies the machines ingest touched, in its transaction: delivered once it commits (#143).</summary>
internal sealed class LiveSignals(DbSession session) : ILiveSignals
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task MachinesChangedAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "SELECT pg_notify('pulaski_live', n) FROM unnest(@Notices) AS n",
            new { Notices = equipmentIds.Select(id => JsonSerializer.Serialize(new LiveNotice(LiveChannel.Machine, LiveChannel.Machine, id), Json)).ToArray() },
            cancellationToken);
}
