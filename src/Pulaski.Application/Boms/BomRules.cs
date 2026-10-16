using Pulaski.Contracts.Boms;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Boms;

/// <summary>Maps BOM definitions between the API and the domain, and checks what they name.</summary>
internal static class BomRules
{
    public static BomDefinition ToDomain(BomDefinitionDto dto) => new(
        dto.BaseQty,
        dto.BaseUomId,
        [.. dto.Items.Select(i => new BomItem(
            i.Id, i.LineNo, i.ComponentMaterialId, i.QtyPer, i.UomId, i.ScrapFactor, i.ConsumptionMethod?.Trim() ?? "", i.OperationNumber))]);

    public static BomDefinitionDto ToDto(BomDefinition definition) => new()
    {
        BaseQty = definition.BaseQty,
        BaseUomId = definition.BaseUomId,
        Items = [.. definition.Items.Select(i => new BomItemDto
        {
            Id = i.Id,
            LineNo = i.LineNo,
            ComponentMaterialId = i.ComponentMaterialId,
            QtyPer = i.QtyPer,
            UomId = i.UomId,
            ScrapFactor = i.ScrapFactor,
            ConsumptionMethod = i.ConsumptionMethod,
            OperationNumber = i.OperationNumber,
        })],
    };

    /// <summary>The components, and the BOM's own material when given (its base unit is checked too).</summary>
    public static IReadOnlyList<Guid> MaterialIds(BomDefinition definition, Guid? bomMaterialId = null) =>
        [.. definition.Items.Select(i => i.ComponentMaterialId).Append(bomMaterialId ?? Guid.Empty).Where(id => id != Guid.Empty).Distinct()];

    /// <summary>
    /// What the definition names must make sense for the BOM: real components other than the
    /// material itself, and operation numbers only on a BOM for a process, from its released
    /// version (<paramref name="operations"/>, null when it has none). <paramref name="malformedOnly"/>
    /// leaves out what a draft may still have: inactive components, operations not (yet) released,
    /// and units that don't convert yet. With <paramref name="converterFor"/>, the base unit must
    /// convert to the material's base unit and each item's unit to its component's (D-026).
    /// </summary>
    public static IReadOnlyList<BomProblem> ReferenceProblems(
        BomDefinition definition,
        Bom bom,
        IReadOnlyList<BomMaterialRow> materials,
        IReadOnlySet<int>? operations,
        bool malformedOnly,
        Func<Guid, UnitConverter>? converterFor = null)
    {
        var byId = materials.ToDictionary(m => m.Id);
        var problems = new List<BomProblem>();
        foreach (var item in definition.Items)
        {
            var component = byId.GetValueOrDefault(item.ComponentMaterialId);
            if (component is null)
            {
                problems.Add(new("unknown_component", $"Line {item.LineNo} names a material that doesn't exist.", item.Id));
            }
            else if (component.Id == bom.MaterialId)
            {
                problems.Add(new("self_component", $"Line {item.LineNo}: a material can't be its own component.", item.Id));
            }
            else if (!component.IsActive && !malformedOnly)
            {
                problems.Add(new("inactive_component", $"Line {item.LineNo}: {component.Code} is inactive.", item.Id));
            }
            else if (!malformedOnly && converterFor?.Invoke(component.Id) is { } converter && !converter.CanConvert(item.UomId, component.BaseUomId))
            {
                problems.Add(new(
                    "unit_conversion",
                    $"Line {item.LineNo}: {converter.Code(item.UomId)} doesn't convert to {converter.Code(component.BaseUomId)}, the base unit of {component.Code}; add a conversion for {component.Code} on the Units page.",
                    item.Id));
            }

            if (item.OperationNumber is { } number)
            {
                if (bom.ProcessId is null)
                {
                    problems.Add(new("operation_without_process", $"Line {item.LineNo}: only a BOM for a process names operations.", item.Id));
                }
                else if (!malformedOnly && operations is not null && !operations.Contains(number))
                {
                    problems.Add(new("unknown_operation", $"Line {item.LineNo}: the process's released version has no operation {number}.", item.Id));
                }
            }
        }

        if (!malformedOnly
            && converterFor?.Invoke(bom.MaterialId) is { } own
            && byId.GetValueOrDefault(bom.MaterialId) is { } output
            && !own.CanConvert(definition.BaseUomId, output.BaseUomId))
        {
            problems.Add(new(
                "base_unit_conversion",
                $"The BOM's unit, {own.Code(definition.BaseUomId)}, doesn't convert to {own.Code(output.BaseUomId)}, the base unit of {output.Code}; add a conversion for {output.Code} on the Units page."));
        }

        if (!malformedOnly && bom.ProcessId is not null && operations is null)
        {
            problems.Add(new("process_not_released", "The process has no released version yet; release it first."));
        }

        return problems;
    }

    public static BomProblemDto ToDto(BomProblem problem) => new(problem.Code, problem.Message, problem.ItemId);
}
