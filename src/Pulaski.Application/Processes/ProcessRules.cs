using Pulaski.Contracts.Processes;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Processes;

/// <summary>Maps definitions between the API and the domain, and checks the equipment they name.</summary>
internal static class ProcessRules
{
    public static ProcessDefinition ToDomain(ProcessDefinitionDto dto) => new(
        [.. dto.Operations.Select(o => new ProcessOperation(
            o.Id, o.Number, o.Name?.Trim() ?? "", string.IsNullOrWhiteSpace(o.Description) ? null : o.Description.Trim(), o.X, o.Y,
            [.. o.Equipment.Select(e => new OperationEquipment(e.EquipmentId, e.SharePercent))]))],
        [.. dto.Dependencies.Select(d => new ProcessDependency(d.Id, d.FromOperationId, d.ToOperationId, d.Type, d.Lag))]);

    public static ProcessDefinitionDto ToDto(ProcessDefinition definition) => new()
    {
        Operations = [.. definition.Operations.Select(o => new ProcessOperationDto
        {
            Id = o.Id,
            Number = o.Number,
            Name = o.Name,
            Description = o.Description,
            X = o.X,
            Y = o.Y,
            Equipment = [.. o.Equipment.Select(e => new OperationEquipmentDto(e.EquipmentId, e.SharePercent))],
        })],
        Dependencies = [.. definition.Dependencies.Select(d => new ProcessDependencyDto(d.Id, d.FromOperationId, d.ToOperationId, d.Type, d.Lag))],
    };

    public static IReadOnlyList<Guid> EquipmentIds(ProcessDefinition definition) =>
        [.. definition.Operations.SelectMany(o => o.Equipment).Select(e => e.EquipmentId).Distinct()];

    /// <summary>
    /// Equipment an operation names must be in the process's site and hold jobs: a line, or a
    /// department that plans for lines (D-022). <paramref name="malformedOnly"/> leaves out what a
    /// draft may still have: equipment made inactive since it was chosen.
    /// </summary>
    public static IReadOnlyList<ProcessProblem> EquipmentProblems(
        ProcessDefinition definition, Guid siteId, IReadOnlyList<ProcessEquipmentRow> equipment, bool malformedOnly)
    {
        var byId = equipment.ToDictionary(e => e.Id);
        var problems = new List<ProcessProblem>();
        foreach (var op in definition.Operations)
        {
            foreach (var use in op.Equipment)
            {
                var row = byId.GetValueOrDefault(use.EquipmentId);
                if (row is null || row.SiteId != siteId)
                {
                    problems.Add(new("unknown_equipment", $"Operation {op.Number} names equipment that isn't in this site.", op.Id));
                }
                else if (!row.IsSchedulable && !row.CanRunJob)
                {
                    problems.Add(new("not_a_line", $"Operation {op.Number}: {row.Code} doesn't run or plan jobs; pick a line or a department.", op.Id));
                }
                else if (!row.IsActive && !malformedOnly)
                {
                    problems.Add(new("inactive_equipment", $"Operation {op.Number}: {row.Code} is inactive.", op.Id));
                }
            }
        }

        return problems;
    }

    public static ProcessProblemDto ToDto(ProcessProblem problem) => new(problem.Code, problem.Message, problem.OperationId, problem.DependencyId);
}
