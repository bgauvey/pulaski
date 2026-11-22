using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>What a production report starts with (#122).</summary>
public class ReportDefaultsTests
{
    private static readonly InventoryLocationDto Staging = new() { Id = Guid.NewGuid(), SiteId = Guid.Empty, Code = "STAGING", Name = "Staging", EquipmentRole = "storage", IsActive = true };
    private static readonly InventoryLocationDto Dock = new() { Id = Guid.NewGuid(), SiteId = Guid.Empty, Code = "DOCK", Name = "Dock", EquipmentRole = "storage", IsActive = true };

    private static JobExecutionDto Job(Guid? lineDefault, string? filling = null, string? printer = null) => new()
    {
        JobId = Guid.NewGuid(),
        JobNumber = "J-1",
        JobType = "production",
        Status = "running",
        SiteId = Guid.Empty,
        LineId = Guid.Empty,
        LineCode = "EXT-01",
        DefaultLocationId = lineDefault,
        LabelPrinterCode = printer,
        FillingLots = filling is null ? [] : [new FillingLotDto { LotId = Guid.NewGuid(), LotNumber = "J-1-01", LocationCode = filling }],
    };

    [Fact]
    public void Good_output_goes_where_the_lot_is_being_filled_else_to_the_lines_default_else_the_form_asks()
    {
        Assert.Equal(Dock.Id, ReportDefaults.Location(Job(Staging.Id, filling: "DOCK"), [Staging, Dock]));
        Assert.Equal(Staging.Id, ReportDefaults.Location(Job(Staging.Id), [Staging, Dock]));
        Assert.Equal(Guid.Empty, ReportDefaults.Location(Job(Guid.NewGuid()), [Staging, Dock]));
        Assert.Equal(Guid.Empty, ReportDefaults.Location(Job(null), [Staging, Dock]));

        Assert.Equal("Lot labels print on BT-1 once a lot is closed.", ReportDefaults.LabelHint(Job(null, printer: "BT-1")));
        Assert.Null(ReportDefaults.LabelHint(Job(null)));
    }
}
