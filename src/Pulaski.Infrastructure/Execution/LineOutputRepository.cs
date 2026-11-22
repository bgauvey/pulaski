using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Execution;

/// <summary>A line's report defaults (#122, D-111), in the command's transaction.</summary>
internal sealed class LineOutputRepository(DbSession session) : ILineOutputRepository
{
    public async Task<Versioned<LineOutput>?> GetForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT id, code, site_id, can_run_job, output_location_id, label_printer_id, xmin::text::bigint AS version
            FROM core.equipment WHERE id = @Id FOR NO KEY UPDATE
            """,
            new { Id = equipmentId }, cancellationToken) is { SiteId: { } site } r
            ? new Versioned<LineOutput>(new LineOutput(r.Id, r.Code, site, r.CanRunJob, r.OutputLocationId, r.LabelPrinterId), r.Version)
            : null;

    public Task<bool> IsLocationAsync(Guid siteId, Guid locationId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND site_id = @SiteId AND holds_inventory AND is_active)",
            new { Id = locationId, SiteId = siteId }, cancellationToken);

    public Task<bool> IsPrinterAsync(Guid siteId, Guid printerId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM core.label_printer WHERE id = @Id AND site_id = @SiteId AND is_active)",
            new { Id = printerId, SiteId = siteId }, cancellationToken);

    public Task<long> UpdateAsync(LineOutput output, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            UPDATE core.equipment SET output_location_id = @OutputLocationId, label_printer_id = @LabelPrinterId, updated_at = @At
            WHERE id = @EquipmentId
            RETURNING xmin::text::bigint
            """,
            new { output.EquipmentId, output.OutputLocationId, output.LabelPrinterId, At = at }, cancellationToken);

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid? SiteId { get; init; }
        public bool CanRunJob { get; init; }
        public Guid? OutputLocationId { get; init; }
        public Guid? LabelPrinterId { get; init; }
        public long Version { get; init; }
    }
}
