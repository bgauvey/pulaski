using System.Xml.Linq;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Execution;

namespace Pulaski.Application.Tests.WorkOrders;

/// <summary>Writing a job's totals as a B2MML production performance (D-102).</summary>
public class ProductionPerformanceB2mmlTests
{
    private static readonly XNamespace Ns = ProductionPerformanceB2mml.Ns;
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Started = new(2026, 11, 19, 6, 0, 0, 123, TimeSpan.Zero);

    private static JobExecutionDto Job(string status = "completed", DateTimeOffset? completedAt = null, params JobRunDto[] runs) => new()
    {
        JobId = Guid.NewGuid(),
        JobNumber = "WO-1-10",
        JobType = "production",
        Status = status,
        SiteId = Guid.NewGuid(),
        LineId = Guid.NewGuid(),
        LineCode = "EXT-01",
        WorkOrderId = Guid.NewGuid(),
        WorkOrderNumber = "WO-1",
        MaterialCode = "PRF-4410-WH",
        UomCode = "m",
        GoodQty = 11850.5m,
        ScrapQty = 120m,
        CompletedAt = completedAt,
        Runs = runs,
    };

    private static JobRunDto Run(int no, string line, DateTimeOffset start, DateTimeOffset? end) =>
        new() { Id = Guid.NewGuid(), RunNo = no, LineCode = line, StartedAt = start, EndedAt = end };

    private static JobPerformanceFacts Facts(string? externalKey = "4500012345") => new()
    {
        SiteCode = "PLT1",
        ExternalKey = externalKey,
        ExternalSystem = externalKey is null ? null : "SAP",
        OperationNumber = 10,
        Lots =
        [
            new() { Produced = false, MaterialCode = "PVC-RESIN", LocationCode = "SILO-1", Quantity = 2400.250m, UomCode = "kg" },
            new() { Produced = true, MaterialCode = "PRF-4410-WH", LotNumber = "L-000101", LocationCode = "FG", Quantity = 6000m, UomCode = "m" },
            new() { Produced = true, MaterialCode = "PRF-4410-WH", LotNumber = "L-000102", LocationCode = "FG", Quantity = 5850.5m, UomCode = "m" },
        ],
    };

    private static XElement Response(string xml) =>
        XDocument.Parse(xml).Root!.Element(Ns + "ProductionResponse")!;

    [Fact]
    public void A_completed_job_answers_the_erp_request_with_its_totals_and_lots()
    {
        var ended = Started.AddHours(7);
        var xml = ProductionPerformanceB2mml.Write(Job("completed", ended, Run(1, "EXT-01", Started, ended)), Facts(), Now);

        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml, StringComparison.Ordinal);
        var root = XDocument.Parse(xml).Root!;
        Assert.Equal(Ns + "ProductionPerformance", root.Name);
        Assert.Equal(("WO-1-10", "PLT1", "2026-11-19T14:00:00Z"),
            ((string)root.Element(Ns + "ID")!, (string)root.Element(Ns + "HierarchyScope")!.Element(Ns + "EquipmentID")!, (string)root.Element(Ns + "PublishedDate")!));

        var response = root.Element(Ns + "ProductionResponse")!;
        Assert.Equal(("WO-1-10", "4500012345", "Completed"),
            ((string)response.Element(Ns + "ID")!, (string)response.Element(Ns + "ProductionRequestID")!, (string)response.Element(Ns + "ResponseState")!));
        Assert.Equal(("2026-11-19T06:00:00.123Z", "2026-11-19T13:00:00.123Z"),
            ((string)response.Element(Ns + "StartTime")!, (string)response.Element(Ns + "EndTime")!));

        var segment = response.Element(Ns + "SegmentResponse")!;
        Assert.Equal(("10", "Completed"), ((string)segment.Element(Ns + "ProcessSegmentID")!, (string)segment.Element(Ns + "SegmentState")!));
        var data = segment.Elements(Ns + "SegmentData")
            .ToDictionary(d => (string)d.Element(Ns + "ID")!, d => ((string)d.Element(Ns + "Value")!.Element(Ns + "ValueString")!, (string)d.Element(Ns + "Value")!.Element(Ns + "UnitOfMeasure")!));
        Assert.Equal(("11850.5", "m"), data["GoodQuantity"]);
        Assert.Equal(("120", "m"), data["ScrapQuantity"]);
        Assert.Equal(["EXT-01"], segment.Elements(Ns + "EquipmentActual").Select(e => (string)e.Element(Ns + "EquipmentID")!));

        // Produced lots first, each with its own quantity; the silo's balance has no lot, only its location.
        var materials = segment.Elements(Ns + "MaterialActual").Select(m => (
            (string)m.Element(Ns + "MaterialUse")!,
            (string)m.Element(Ns + "MaterialDefinitionID")!,
            (string?)m.Element(Ns + "MaterialLotID"),
            (string?)m.Element(Ns + "StorageLocation"),
            (string)m.Element(Ns + "Quantity")!.Element(Ns + "QuantityString")!,
            (string)m.Element(Ns + "Quantity")!.Element(Ns + "UnitOfMeasure")!)).ToList();
        Assert.Equal(
            [
                ("Produced", "PRF-4410-WH", "L-000101", null, "6000", "m"),
                ("Produced", "PRF-4410-WH", "L-000102", null, "5850.5", "m"),
                ("Consumed", "PVC-RESIN", null, "SILO-1", "2400.25", "kg"),
            ],
            materials);
    }

    [Fact]
    public void A_running_job_has_a_start_but_no_end()
    {
        var response = Response(ProductionPerformanceB2mml.Write(Job("running", null, Run(1, "EXT-01", Started, null)), Facts(), Now));

        Assert.Equal("Running", (string)response.Element(Ns + "ResponseState")!);
        Assert.NotNull(response.Element(Ns + "StartTime"));
        Assert.Null(response.Element(Ns + "EndTime"));
        Assert.Null(response.Element(Ns + "SegmentResponse")!.Element(Ns + "ActualEndTime"));
    }

    [Fact]
    public void A_paused_job_is_held_until_its_last_run_ended_and_names_each_line_it_ran_on()
    {
        var job = Job("paused", null,
            Run(1, "EXT-01", Started, Started.AddHours(2)),
            Run(2, "EXT-02", Started.AddHours(3), Started.AddHours(4)),
            Run(3, "EXT-01", Started.AddHours(5), Started.AddHours(6)));
        var response = Response(ProductionPerformanceB2mml.Write(job, Facts(), Now));

        Assert.Equal("Held", (string)response.Element(Ns + "ResponseState")!);
        Assert.Equal("2026-11-19T12:00:00.123Z", (string)response.Element(Ns + "EndTime")!);
        Assert.Equal(["EXT-01", "EXT-02"],
            response.Element(Ns + "SegmentResponse")!.Elements(Ns + "EquipmentActual").Select(e => (string)e.Element(Ns + "EquipmentID")!));
    }

    [Fact]
    public void A_job_not_yet_started_is_ready_on_its_own_line_with_nothing_made()
    {
        var job = Job("dispatched") with { GoodQty = 0, ScrapQty = 0 };
        var response = Response(ProductionPerformanceB2mml.Write(job, Facts() with { Lots = [] }, Now));

        Assert.Equal("Ready", (string)response.Element(Ns + "ResponseState")!);
        Assert.Null(response.Element(Ns + "StartTime"));
        var segment = response.Element(Ns + "SegmentResponse")!;
        Assert.Equal("EXT-01", (string)segment.Element(Ns + "EquipmentActual")!.Element(Ns + "EquipmentID")!);
        Assert.Empty(segment.Elements(Ns + "MaterialActual"));
    }

    [Fact]
    public void An_order_made_in_pulaski_is_answered_by_its_order_number()
    {
        var response = Response(ProductionPerformanceB2mml.Write(Job(), Facts(externalKey: null), Now));

        Assert.Equal("WO-1", (string)response.Element(Ns + "ProductionRequestID")!);
    }

    [Fact]
    public void A_feed_sends_it_as_a_sync_that_replaces_the_one_before()
    {
        var bodId = Guid.CreateVersion7();
        var root = XDocument.Parse(ProductionPerformanceB2mml.WriteSync(Job(), Facts(), Now, bodId)).Root!;

        Assert.Equal(Ns + "SyncProductionPerformance", root.Name);
        var area = root.Element(Ns + "ApplicationArea")!;
        Assert.Equal(("Pulaski", "2026-11-19T14:00:00Z", bodId.ToString()),
            ((string)area.Element(Ns + "Sender")!.Element(Ns + "LogicalID")!, (string)area.Element(Ns + "CreationDateTime")!, (string)area.Element(Ns + "BODID")!));
        var data = root.Element(Ns + "DataArea")!;
        Assert.Equal("Replace", (string)data.Element(Ns + "Sync")!.Element(Ns + "ActionCriteria")!.Element(Ns + "ActionExpression")!.Attribute("actionCode")!);
        Assert.Equal("WO-1-10", (string)data.Element(Ns + "ProductionPerformance")!.Element(Ns + "ID")!);
    }

    [Theory]
    [InlineData("planned", "Ready")]
    [InlineData("firmed", "Ready")]
    [InlineData("running", "Running")]
    [InlineData("paused", "Held")]
    [InlineData("completed", "Completed")]
    [InlineData("closed", "Completed")]
    [InlineData("cancelled", "Aborted")]
    public void Job_statuses_map_to_isa95_response_states(string status, string state) =>
        Assert.Equal(state, ProductionPerformanceB2mml.State(status));
}
