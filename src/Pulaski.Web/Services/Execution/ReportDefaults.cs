using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services.Execution;

/// <summary>What a production report starts with (#122). Pure.</summary>
public static class ReportDefaults
{
    /// <summary>
    /// Where good output goes unless the operator changes it: the location of the lot being filled (D-104), else the line's default output
    /// location; only an active location offered. <see cref="Guid.Empty"/> when there is neither, so the form asks.
    /// </summary>
    public static Guid Location(JobExecutionDto job, IReadOnlyList<InventoryLocationDto> locations)
    {
        var filling = job.FillingLots.Count > 0 ? locations.FirstOrDefault(l => l.Code == job.FillingLots[0].LocationCode) : null;
        var lineDefault = locations.FirstOrDefault(l => l.Id == job.DefaultLocationId);
        return (filling ?? lineDefault)?.Id ?? Guid.Empty;
    }

    /// <summary>"Lot labels print on BT-1 once a lot is closed."; null when the line has no label printer.</summary>
    public static string? LabelHint(JobExecutionDto job) =>
        job.LabelPrinterCode is { } printer ? $"Lot labels print on {printer} once a lot is closed." : null;
}
