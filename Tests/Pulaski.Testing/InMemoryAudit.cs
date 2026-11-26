using Pulaski.Application.Audit;

namespace Pulaski.Testing;

/// <summary>The audit trail in memory (#129): the test adds rows; searched by subject only.</summary>
public sealed class InMemoryAudit : IAuditReadStore
{
    public List<AuditRow> Rows { get; } = [];

    public Task<IReadOnlyList<AuditRow>> SearchAsync(AuditFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditRow>>([.. Rows.Where(r => filter.SubjectId is null || r.Subject == filter.SubjectId)
            .OrderByDescending(r => r.RecordedAt).Take(filter.Limit + 1)]);
}
