using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Scheduling;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Scheduling;

/// <summary>SQL for refreshing planning rates (#70, D-098): the site settings, when it last ran, and proposals.</summary>
internal static class RateRefreshSql
{
    private const string SettingsColumns = """
        s.site_id, s.take_out_stops, s.is_scheduled, s.every_days::int AS every_days, s.window_days::int AS window_days, s.threshold_pct,
        s.auto_accept, s.auto_accept_limit_pct, r.refreshed_at, s.xmin::text::bigint AS version
        """;

    public const string Settings = $"""
        SELECT {SettingsColumns}
        FROM core.rate_refresh_settings s
        LEFT JOIN core.rate_refresh r ON r.site_id = s.site_id
        WHERE s.site_id = @SiteId
        """;

    public const string EnsureSettings = "INSERT INTO core.rate_refresh_settings (site_id) VALUES (@SiteId) ON CONFLICT (site_id) DO NOTHING RETURNING true";

    public const string SettingsForUpdate = Settings + "\nFOR UPDATE OF s";

    public const string UpdateSettings = """
        UPDATE core.rate_refresh_settings
        SET take_out_stops = @TakeOutStops, is_scheduled = @IsScheduled, every_days = @EveryDays, window_days = @WindowDays,
            threshold_pct = @ThresholdPct, auto_accept = @AutoAccept, auto_accept_limit_pct = @AutoAcceptLimitPct, updated_at = @At
        WHERE site_id = @SiteId
        RETURNING xmin::text::bigint
        """;

    public const string Scheduled = $"""
        SELECT {SettingsColumns}
        FROM core.rate_refresh_settings s
        LEFT JOIN core.rate_refresh r ON r.site_id = s.site_id
        WHERE s.is_scheduled
        ORDER BY s.site_id
        """;

    public const string MarkRefreshed = """
        INSERT INTO core.rate_refresh (site_id, refreshed_at) VALUES (@SiteId, @At)
        ON CONFLICT (site_id) DO UPDATE SET refreshed_at = excluded.refreshed_at
        """;

    public const string StandardsOfSite = """
        SELECT rs.id
        FROM core.run_standard rs
        JOIN core.equipment e ON e.id = rs.equipment_id
        WHERE e.site_id = @SiteId AND e.is_active
        ORDER BY rs.id
        """;

    private const string ProposalColumns = """
        id, run_standard_id, proposed_rate_per_hour, uom_id, calculation_basis, window_days::int AS window_days, window_from, window_to, used_runs,
        used_hours, used_good_qty, stopped_hours, calculated_rate_per_hour, effective_rate_per_hour, review_reason, proposed_at, status
        """;

    public const string PendingForUpdate = $"""
        SELECT {ProposalColumns} FROM core.run_standard_proposal WHERE run_standard_id = @Id AND status = 'pending' FOR UPDATE
        """;

    public const string InsertProposal = """
        INSERT INTO core.run_standard_proposal (id, run_standard_id, proposed_rate_per_hour, uom_id, calculation_basis, window_days, window_from, window_to,
                                                used_runs, used_hours, used_good_qty, stopped_hours, calculated_rate_per_hour, effective_rate_per_hour,
                                                review_reason, status, proposed_at)
        VALUES (@Id, @RunStandardId, @ProposedRatePerHour, @UomId, @CalculationBasis, @WindowDays, @WindowFrom, @WindowTo,
                @UsedRuns, @UsedHours, @UsedGoodQty, @StoppedHours, @CalculatedRatePerHour, @EffectiveRatePerHour,
                @ReviewReason, @Status, @ProposedAt)
        """;

    public const string Decide = """
        UPDATE core.run_standard_proposal
        SET status = @Status, decided_at = @At, decided_by = @Actor, decision_note = @Note, updated_at = @At
        WHERE id = @Id AND status = 'pending'
        """;

    private const string ProposalView = """
        SELECT p.id, p.run_standard_id, rs.xmin::text::bigint AS run_standard_version, rs.equipment_id AS line_id, e.code AS line_code,
               rs.material_id, m.code AS material_code, m.name AS material_name, u.code AS uom_code, p.proposed_rate_per_hour, p.calculation_basis,
               p.window_days::int AS window_days, p.window_from, p.window_to, p.used_runs, p.used_hours, p.used_good_qty, p.stopped_hours,
               p.calculated_rate_per_hour, rs.calculated_rate_per_hour AS current_calculated_rate_per_hour, rs.calculation_basis AS current_basis,
               rs.modified_rate_per_hour, p.review_reason, p.status, p.proposed_at, p.decided_at, p.decided_by, p.decision_note
        FROM core.run_standard_proposal p
        JOIN core.run_standard rs ON rs.id = p.run_standard_id
        JOIN core.equipment e ON e.id = rs.equipment_id
        JOIN core.material m ON m.id = rs.material_id
        JOIN core.uom u ON u.id = p.uom_id
        """;

    public const string ListProposals = ProposalView + """

        WHERE e.site_id = @SiteId AND (p.status = 'pending' OR p.decided_at >= @Since) AND p.status <> 'superseded'
        ORDER BY (p.status = 'pending') DESC, p.proposed_at DESC, e.code, m.code
        """;

    public const string Pending = ProposalView + "\nWHERE p.run_standard_id = @Id AND p.status = 'pending'";

    internal sealed class SettingsRow
    {
        public Guid SiteId { get; init; }
        public bool TakeOutStops { get; init; }
        public bool IsScheduled { get; init; }
        public int EveryDays { get; init; }
        public int WindowDays { get; init; }
        public decimal ThresholdPct { get; init; }
        public bool AutoAccept { get; init; }
        public decimal AutoAcceptLimitPct { get; init; }
        public DateTimeOffset? RefreshedAt { get; init; }
        public long Version { get; init; }

        public RateRefreshSettings ToSettings() =>
            new(SiteId, TakeOutStops, IsScheduled, EveryDays, WindowDays, ThresholdPct, AutoAccept, AutoAcceptLimitPct);
    }

    internal sealed class ProposalRow
    {
        public Guid Id { get; init; }
        public Guid RunStandardId { get; init; }
        public decimal ProposedRatePerHour { get; init; }
        public Guid UomId { get; init; }
        public string CalculationBasis { get; init; } = "";
        public int WindowDays { get; init; }
        public DateTimeOffset WindowFrom { get; init; }
        public DateTimeOffset WindowTo { get; init; }
        public int UsedRuns { get; init; }
        public decimal UsedHours { get; init; }
        public decimal UsedGoodQty { get; init; }
        public decimal StoppedHours { get; init; }
        public decimal CalculatedRatePerHour { get; init; }
        public decimal EffectiveRatePerHour { get; init; }
        public string? ReviewReason { get; init; }
        public DateTimeOffset ProposedAt { get; init; }
        public string Status { get; init; } = "";

        public RateProposal ToProposal() => new(
            Id, RunStandardId, ProposedRatePerHour, UomId, CalculationBasis, WindowDays, WindowFrom, WindowTo, UsedRuns, UsedHours, UsedGoodQty, StoppedHours,
            CalculatedRatePerHour, EffectiveRatePerHour, ReviewReason, ProposedAt, Status);
    }
}

internal sealed class RateRefreshRepository(DbSession session) : IRateRefreshRepository
{
    public async Task<RateRefreshSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<RateRefreshSql.SettingsRow>(RateRefreshSql.Settings, new { SiteId = siteId }, cancellationToken))?.ToSettings()
        ?? RateRefreshSettings.Default(siteId);

    public async Task<Versioned<RateRefreshSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var created = await session.QuerySingleOrDefaultAsync<bool?>(RateRefreshSql.EnsureSettings, new { SiteId = siteId }, cancellationToken) == true;
        var row = (await session.QuerySingleOrDefaultAsync<RateRefreshSql.SettingsRow>(RateRefreshSql.SettingsForUpdate, new { SiteId = siteId }, cancellationToken))!;
        return new Versioned<RateRefreshSettings>(row.ToSettings(), created ? 0 : row.Version);
    }

    public Task<long> UpdateSettingsAsync(RateRefreshSettings s, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(RateRefreshSql.UpdateSettings, new
        {
            s.SiteId,
            s.TakeOutStops,
            s.IsScheduled,
            EveryDays = (short)s.EveryDays,
            WindowDays = (short)s.WindowDays,
            s.ThresholdPct,
            s.AutoAccept,
            s.AutoAcceptLimitPct,
            At = at,
        }, cancellationToken);

    public async Task<IReadOnlyList<(RateRefreshSettings Settings, DateTimeOffset? RefreshedAt)>> ListScheduledAsync(CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<RateRefreshSql.SettingsRow>(RateRefreshSql.Scheduled, null, cancellationToken)).Select(r => (r.ToSettings(), r.RefreshedAt))];

    public Task MarkRefreshedAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(RateRefreshSql.MarkRefreshed, new { SiteId = siteId, At = at }, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListRunStandardsOfSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(RateRefreshSql.StandardsOfSite, new { SiteId = siteId }, cancellationToken);

    public async Task<RateProposal?> GetPendingForUpdateAsync(Guid runStandardId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<RateRefreshSql.ProposalRow>(RateRefreshSql.PendingForUpdate, new { Id = runStandardId }, cancellationToken))?.ToProposal();

    public Task InsertProposalAsync(RateProposal p, CancellationToken cancellationToken) =>
        session.ExecuteAsync(RateRefreshSql.InsertProposal, new
        {
            p.Id,
            p.RunStandardId,
            p.ProposedRatePerHour,
            p.UomId,
            p.CalculationBasis,
            WindowDays = (short)p.WindowDays,
            p.WindowFrom,
            p.WindowTo,
            p.UsedRuns,
            p.UsedHours,
            p.UsedGoodQty,
            p.StoppedHours,
            p.CalculatedRatePerHour,
            p.EffectiveRatePerHour,
            p.ReviewReason,
            p.Status,
            p.ProposedAt,
        }, cancellationToken);

    public Task DecideAsync(Guid proposalId, string status, DateTimeOffset at, string actorId, string? note, CancellationToken cancellationToken) =>
        session.ExecuteAsync(RateRefreshSql.Decide, new { Id = proposalId, Status = status, At = at, Actor = actorId, Note = note }, cancellationToken);
}

internal sealed class RateRefreshQueries(NpgsqlDataSource dataSource) : IRateRefreshReadStore
{
    public async Task<RateRefreshSettingsDto> GetSettingsDtoAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<RateRefreshSql.SettingsRow>(
            new CommandDefinition(RateRefreshSql.Settings, new { SiteId = siteId }, cancellationToken: cancellationToken));
        var s = row?.ToSettings() ?? RateRefreshSettings.Default(siteId);
        return new RateRefreshSettingsDto
        {
            SiteId = siteId,
            TakeOutStops = s.TakeOutStops,
            IsScheduled = s.IsScheduled,
            EveryDays = s.EveryDays,
            WindowDays = s.WindowDays,
            ThresholdPct = s.ThresholdPct,
            AutoAccept = s.AutoAccept,
            AutoAcceptLimitPct = s.AutoAcceptLimitPct,
            RefreshedAt = row?.RefreshedAt,
            Version = row?.Version ?? 0,
        };
    }

    public async Task<RateRefreshSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<RateRefreshSql.SettingsRow>(
            new CommandDefinition(RateRefreshSql.Settings, new { SiteId = siteId }, cancellationToken: cancellationToken)))?.ToSettings()
            ?? RateRefreshSettings.Default(siteId);
    }

    public async Task<IReadOnlyList<RateProposalDto>> ListProposalsAsync(Guid siteId, DateTimeOffset decidedSince, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<RateProposalDto>(
            new CommandDefinition(RateRefreshSql.ListProposals, new { SiteId = siteId, Since = decidedSince }, cancellationToken: cancellationToken))).AsList();
    }

    public async Task<RateProposalDto?> GetPendingAsync(Guid runStandardId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<RateProposalDto>(
            new CommandDefinition(RateRefreshSql.Pending, new { Id = runStandardId }, cancellationToken: cancellationToken));
    }
}
