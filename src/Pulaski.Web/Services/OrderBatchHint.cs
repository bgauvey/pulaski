using System.Globalization;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services;

/// <summary>
/// The new order form's batch hint (#85): the batch size each line of the chosen process plans in (its own, else the material's), so a
/// planner can see what an order is shared into before submitting.
/// </summary>
public static class OrderBatchHint
{
    /// <summary>
    /// One phrase per operation, such as "op 10: MIX-1 200 kg (60%), MIX-2 50 kg (40%)"; empty when every line plans in the material's
    /// size, which the plain hint already says.
    /// </summary>
    public static IReadOnlyList<string> Operations(ProcessOrderCheckDto? check, decimal materialBatchSize, string unitCode)
    {
        if (check is null || check.Equipment.All(e => e.LineBatchSize is null || e.LineBatchSize == materialBatchSize))
        {
            return [];
        }

        return [.. check.Equipment.GroupBy(e => e.OperationNumber).OrderBy(g => g.Key).Select(op =>
            $"op {op.Key}: " + string.Join(", ", op.Select(e =>
                $"{e.EquipmentCode} {Qty(e.LineBatchSize ?? materialBatchSize)} {unitCode}{(e.IsDepartment ? " until a line is chosen" : "")} ({Qty(e.SharePercent)}%)")))];
    }

    private static string Qty(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
