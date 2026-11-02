namespace Pulaski.Domain.Boms;

/// <summary>How a component is taken from inventory (D-025). Values match <c>core.bom_item.consumption_method</c>.</summary>
public static class ConsumptionMethod
{
    /// <summary>Lots loaded at a feed point are consumed over the time they were loaded (resin in a hopper).</summary>
    public const string TimeWindow = "time_window";

    /// <summary>An operator declares what was used.</summary>
    public const string Declared = "declared";

    /// <summary>Consumed in proportion to the good quantity reported.</summary>
    public const string Backflush = "backflush";

    /// <summary>Each lot is scanned as it is used.</summary>
    public const string Scan = "scan";

    public static IReadOnlyList<string> All { get; } = [TimeWindow, Declared, Backflush, Scan];
}

/// <summary>BOM version lifecycle (D-025). Values match <c>core.bom_version.status</c>.</summary>
public static class BomVersionStatus
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Obsolete = "obsolete";
}

/// <summary>
/// One component line: <see cref="QtyPer"/> of the component per the BOM's base quantity, plus
/// <see cref="ScrapFactor"/> more (0.02 plans 2% extra). <see cref="OperationNumber"/> names the
/// operation that consumes it on a BOM for a process; null means the first operation. <see cref="Alternates"/> are the materials
/// approved in its place (#82).
/// </summary>
public sealed record BomItem(
    Guid Id,
    int LineNo,
    Guid ComponentMaterialId,
    decimal QtyPer,
    Guid UomId,
    decimal ScrapFactor,
    string ConsumptionMethod,
    int? OperationNumber,
    IReadOnlyList<BomItemAlternate>? Alternates = null)
{
    /// <summary>The alternates, none when there are none.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<BomItemAlternate> AlternateList => Alternates ?? [];
}

/// <summary>
/// A material approved in place of a BOM line's component (#82): <see cref="Ratio"/> of its base unit stands for one of the component's
/// base unit (1.05 = 5% more of it is used). Among a line's alternates, the lowest <see cref="Priority"/> is offered first.
/// </summary>
public sealed record BomItemAlternate(Guid MaterialId, decimal Ratio, int Priority);

/// <summary>Something wrong with a BOM, pinned to the item it concerns when there is one.</summary>
public sealed record BomProblem(string Code, string Message, Guid? ItemId = null);

/// <summary>
/// What <see cref="BaseQty"/> of a material (in <see cref="BaseUomId"/>) is made from, one level
/// deep (D-025). A draft may be incomplete but never malformed (<see cref="StructuralProblems"/>);
/// a release must also be complete (<see cref="ReleaseProblems"/>).
/// </summary>
public sealed record BomDefinition(decimal BaseQty, Guid BaseUomId, IReadOnlyList<BomItem> Items)
{
    /// <summary>Problems that make the definition unusable even as a draft.</summary>
    public IReadOnlyList<BomProblem> StructuralProblems()
    {
        var problems = new List<BomProblem>();
        if (BaseQty <= 0)
        {
            problems.Add(new("base_qty", "The base quantity must be greater than zero."));
        }

        var ids = new HashSet<Guid>();
        foreach (var item in Items)
        {
            if (!ids.Add(item.Id))
            {
                problems.Add(new("duplicate_item", $"Line {item.LineNo} appears twice.", item.Id));
            }

            if (item.LineNo <= 0)
            {
                problems.Add(new("line_no", $"Line numbers must be positive; {item.LineNo} is not.", item.Id));
            }

            if (item.QtyPer <= 0)
            {
                problems.Add(new("qty_per", $"Line {item.LineNo}: the quantity must be greater than zero.", item.Id));
            }

            if (item.ScrapFactor is < 0 or >= 1)
            {
                problems.Add(new("scrap_factor", $"Line {item.LineNo}: the scrap factor must be at least 0 and less than 1.", item.Id));
            }

            if (!ConsumptionMethod.All.Contains(item.ConsumptionMethod))
            {
                problems.Add(new("consumption_method", $"Line {item.LineNo}: '{item.ConsumptionMethod}' is not a consumption method.", item.Id));
            }

            if (item.OperationNumber is <= 0)
            {
                problems.Add(new("operation_number", $"Line {item.LineNo}: operation numbers are positive.", item.Id));
            }

            problems.AddRange(AlternateProblems(item));
        }

        foreach (var line in Items.GroupBy(i => i.LineNo).Where(g => g.Count() > 1))
        {
            problems.Add(new("duplicate_line", $"Line number {line.Key} is used {line.Count()} times.", line.First().Id));
        }

        return problems;
    }

    private static IEnumerable<BomProblem> AlternateProblems(BomItem item)
    {
        foreach (var alternate in item.AlternateList)
        {
            if (alternate.MaterialId == item.ComponentMaterialId)
            {
                yield return new("alternate_self", $"Line {item.LineNo}: a component can't be its own alternate.", item.Id);
            }

            if (alternate.Ratio <= 0)
            {
                yield return new("alternate_ratio", $"Line {item.LineNo}: an alternate's ratio must be greater than zero.", item.Id);
            }

            if (alternate.Priority <= 0)
            {
                yield return new("alternate_priority", $"Line {item.LineNo}: an alternate's priority must be 1 or more.", item.Id);
            }
        }

        if (item.AlternateList.GroupBy(a => a.MaterialId).Any(g => g.Count() > 1))
        {
            yield return new("alternate_duplicate", $"Line {item.LineNo}: the same alternate is listed twice.", item.Id);
        }
    }

    /// <summary>The operation numbers the items name that aren't in <paramref name="operationNumbers"/>, in order.</summary>
    public IReadOnlyList<int> MissingOperations(IEnumerable<int> operationNumbers)
    {
        var known = operationNumbers.ToHashSet();
        return [.. Items.Select(i => i.OperationNumber).OfType<int>().Where(n => !known.Contains(n)).Distinct().Order()];
    }

    /// <summary>Everything that stops a release on the definition alone; the caller checks the components and operations.</summary>
    public IReadOnlyList<BomProblem> ReleaseProblems()
    {
        var problems = StructuralProblems().ToList();
        if (Items.Count == 0)
        {
            problems.Add(new("no_items", "Add at least one component."));
        }

        return problems;
    }
}
