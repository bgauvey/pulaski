using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.WorkOrders;

/// <summary>
/// One line or department a released operation names, with what deciding whether it can take the
/// order's job needs: whether it takes jobs, and whether the material can run there (D-020, D-022).
/// </summary>
public sealed record OrderEquipmentRow
{
    public required Guid OperationId { get; init; }
    public int OperationNumber { get; init; }
    public required string OperationName { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public Guid? SiteId { get; init; }
    public bool IsSchedulable { get; init; }
    public bool CanRunJob { get; init; }
    public bool IsActive { get; init; }
    public required string MaterialCode { get; init; }

    /// <summary>A capability in effect on the line, or on a line under the department that can run jobs.</summary>
    public bool CanRunMaterial { get; init; }

    /// <summary>The operation's share of the order on this equipment, in percent.</summary>
    public decimal SharePercent { get; init; }

    /// <summary>The line's own batch size for the material (D-065); null for the material's, and for a department.</summary>
    public decimal? LineBatchSize { get; init; }

    public bool IsDepartment => IsSchedulable && !CanRunJob;
}

/// <summary>
/// Whether a released process can make an order for a material where it says (D-024). Creating
/// the order and the form's check use these same rules, so the form never offers what creation refuses.
/// </summary>
public static class ProcessOrderRules
{
    public static IReadOnlyList<ProcessOrderProblemDto> Problems(IEnumerable<OrderEquipmentRow> rows, Guid siteId)
    {
        var problems = new List<ProcessOrderProblemDto>();
        foreach (var row in rows.OrderBy(r => r.OperationNumber).ThenBy(r => r.EquipmentCode, StringComparer.Ordinal))
        {
            var op = $"Operation {row.OperationNumber} ({row.OperationName}):";
            if (!row.IsSchedulable || !row.IsActive || row.SiteId != siteId)
            {
                problems.Add(new(
                    "cannot_take_jobs", $"{op} {row.EquipmentCode} can't take jobs; change the process.", row.OperationNumber, row.EquipmentCode));
            }
            else if (!row.CanRunMaterial)
            {
                var where = row.IsDepartment ? $"no line in {row.EquipmentCode} is" : $"{row.EquipmentCode} is not";
                problems.Add(new(
                    "no_capability",
                    $"{op} {where} set up to run {row.MaterialCode}; add a capability for it first.",
                    row.OperationNumber,
                    row.EquipmentCode));
            }
        }

        return problems;
    }

    public static IReadOnlyList<int> DepartmentOperations(IEnumerable<OrderEquipmentRow> rows) =>
        [.. rows.Where(r => r.IsDepartment).Select(r => r.OperationNumber).Distinct().Order()];
}

/// <summary>Can an order for <see cref="MaterialId"/> be made from the process? Answers before the planner submits.</summary>
public sealed record CheckProcessForOrderQuery(Guid ProcessId, Guid MaterialId) : IQuery<ProcessOrderCheckDto>;

internal sealed class CheckProcessForOrderHandler(IProcessReadStore processes, IBomReadStore boms, TimeProvider timeProvider)
    : IQueryHandler<CheckProcessForOrderQuery, ProcessOrderCheckDto>
{
    public async Task<ProcessOrderCheckDto> HandleAsync(CheckProcessForOrderQuery query, CancellationToken cancellationToken)
    {
        var process = await processes.GetAsync(query.ProcessId, cancellationToken)
            ?? throw new NotFoundException($"Process {query.ProcessId} not found.");
        var result = new ProcessOrderCheckDto { ProcessId = process.Id, MaterialId = query.MaterialId };

        var released = process.Versions.FirstOrDefault(v => v.Status == ProcessVersionStatus.Released);
        if (released is null)
        {
            return result with { Problems = [new("no_released_version", $"Process {process.Code} has no released version.")] };
        }

        var rows = await processes.GetOrderEquipmentAsync(released.Id, query.MaterialId, timeProvider.GetUtcNow(), cancellationToken);
        if (rows.Count == 0)
        {
            throw new NotFoundException($"Material {query.MaterialId} not found.");
        }

        var problems = new List<ProcessOrderProblemDto>();
        if (!process.IsActive)
        {
            problems.Add(new("inactive", $"Process {process.Code} is inactive."));
        }

        var offered = await processes.ListAsync(process.SiteId, query.MaterialId, cancellationToken);
        if (!offered.Any(p => p.Id == process.Id))
        {
            problems.Add(new("not_covered", $"Process {process.Code} doesn't make {rows[0].MaterialCode}."));
        }

        problems.AddRange(ProcessOrderRules.Problems(rows, process.SiteId));

        // The BOM the order would use: the material's BOM for this process, or else its BOM for any process (D-025).
        var bom = (await boms.ListAsync(query.MaterialId, cancellationToken))
            .Where(b => b.IsActive && b.ReleasedVersion is not null && (b.ProcessId == process.Id || b.ProcessId is null))
            .OrderBy(b => b.ProcessId is null)
            .FirstOrDefault();
        if (bom is { MissingOperations.Length: > 0 })
        {
            problems.Add(new(
                "bom_operation",
                $"The BOM for {bom.MaterialCode} (version {bom.ReleasedVersion}) names operation {string.Join(", ", bom.MissingOperations)}, which {process.Code}'s released version doesn't have; release a new BOM version first."));
        }
        return result with
        {
            ProcessVersionId = released.Id,
            Problems = problems,
            DepartmentOperations = ProcessOrderRules.DepartmentOperations(rows),
            Equipment = [.. rows.Select(r => new OrderEquipmentBatchDto(r.OperationNumber, r.EquipmentCode, r.SharePercent, r.IsDepartment, r.LineBatchSize))],
        };
    }
}
