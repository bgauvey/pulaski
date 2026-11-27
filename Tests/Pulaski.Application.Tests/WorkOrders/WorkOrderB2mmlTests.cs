using Pulaski.Application.WorkOrders;

namespace Pulaski.Application.Tests.WorkOrders;

/// <summary>Reading ISA-95 production schedules in B2MML XML into ERP work orders (D-102).</summary>
public class WorkOrderB2mmlTests
{
    [Fact]
    public void The_template_is_one_order_with_every_part_read()
    {
        var source = WorkOrderB2mml.Parse(WorkOrderB2mml.Template, "SAP", null);

        Assert.Empty(source.Problems);
        var order = Assert.Single(source.Rows).Order!;
        Assert.Equal(("SAP", "4500012345", "4500012345", "PLT1"), (order.ExternalSystem, order.ExternalKey, order.OrderNumber, order.SiteCode));
        Assert.Equal(("PRF-4410-WH", 12000m, "m"), (order.MaterialCode, order.Quantity, order.UomCode));
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 6, 0, 0, TimeSpan.Zero), order.PlannedStart);
        Assert.Equal(new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero), order.DueAt);
        Assert.Equal((2, "EXTRUDE-PACK", "Customer order 778, white profile", false), (order.Priority, order.ProcessCode, order.Notes, order.Canceled));
        Assert.Null(order.ExternalVersion);
    }

    [Fact]
    public void A_newer_material_requirement_a_verb_wrapper_and_no_namespace_read_the_same()
    {
        const string Text = """
            <SyncProductionSchedule>
              <DataArea>
                <Sync />
                <ProductionSchedule>
                  <ProductionRequest>
                    <ID>R-2</ID>
                    <HierarchyScope><EquipmentID>PLT2</EquipmentID></HierarchyScope>
                    <SegmentRequirement>
                      <ProductSegmentID>MIX</ProductSegmentID>
                      <EarliestStartTime>2026-12-01T08:00:00+01:00</EarliestStartTime>
                      <LatestEndTime>2026-12-02T08:00:00+01:00</LatestEndTime>
                      <MaterialRequirement>
                        <MaterialDefinitionID>RESIN</MaterialDefinitionID>
                        <MaterialUse>Consumed</MaterialUse>
                        <Quantity><QuantityString>900</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity>
                      </MaterialRequirement>
                      <MaterialRequirement>
                        <MaterialDefinitionID>BLEND-7</MaterialDefinitionID>
                        <MaterialUse>Produced</MaterialUse>
                        <Quantity><QuantityString>1,000.5</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity>
                      </MaterialRequirement>
                    </SegmentRequirement>
                  </ProductionRequest>
                </ProductionSchedule>
              </DataArea>
            </SyncProductionSchedule>
            """;

        var order = Assert.Single(WorkOrderB2mml.Parse(Text, "ERP", "PLT1").Rows).Order!;

        Assert.Equal(("PLT2", "BLEND-7", 1000.5m, "kg", "MIX"), (order.SiteCode, order.MaterialCode, order.Quantity, order.UomCode, order.ProcessCode));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 7, 0, 0, TimeSpan.Zero), order.PlannedStart);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 7, 0, 0, TimeSpan.Zero), order.DueAt);
    }

    [Fact]
    public void The_site_comes_from_the_request_then_the_schedule_then_the_import()
    {
        static string Request(string id, string? scope) => $"""
            <ProductionRequest>
              <ID>{id}</ID>
              {(scope is null ? "" : $"<HierarchyScope><EquipmentID>{scope}</EquipmentID></HierarchyScope>")}
              <StartTime>2026-12-01T06:00:00Z</StartTime>
              <SegmentRequirement><MaterialProducedRequirement><MaterialDefinitionID>M</MaterialDefinitionID>
                <Quantity><QuantityString>5</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity></MaterialProducedRequirement></SegmentRequirement>
            </ProductionRequest>
            """;
        var withSchedule = $"<ProductionSchedule><HierarchyScope><EquipmentID>SCHED</EquipmentID></HierarchyScope>{Request("A", "OWN")}{Request("B", null)}</ProductionSchedule>";

        Assert.Equal(["OWN", "SCHED"], WorkOrderB2mml.Parse(withSchedule, "ERP", "IMPORT").Rows.Select(r => r.Order!.SiteCode));
        Assert.Equal("IMPORT", Assert.Single(WorkOrderB2mml.Parse($"<ProductionSchedule>{Request("C", null)}</ProductionSchedule>", "ERP", "IMPORT").Rows).Order!.SiteCode);
        var none = Assert.Single(WorkOrderB2mml.Parse($"<ProductionSchedule>{Request("D", null)}</ProductionSchedule>", "ERP", null).Rows);
        Assert.Null(none.Order);
        Assert.Contains(none.Problems, p => p.Text.StartsWith("No site", StringComparison.Ordinal));
    }

    [Fact]
    public void A_request_it_cant_read_says_each_reason_and_the_others_are_still_read()
    {
        const string Text = """
            <ProductionSchedule>
              <HierarchyScope><EquipmentID>PLT1</EquipmentID></HierarchyScope>
              <ProductionRequest>
                <Priority>high</Priority>
                <StartTime>next week</StartTime>
                <SegmentRequirement>
                  <MaterialProducedRequirement><MaterialDefinitionID>A</MaterialDefinitionID><Quantity><QuantityString>lots</QuantityString></Quantity></MaterialProducedRequirement>
                  <MaterialProducedRequirement><MaterialDefinitionID>B</MaterialDefinitionID></MaterialProducedRequirement>
                </SegmentRequirement>
              </ProductionRequest>
              <ProductionRequest>
                <ID>OK-1</ID>
                <StartTime>2026-12-01T06:00:00Z</StartTime>
                <SegmentRequirement><MaterialProducedRequirement><MaterialDefinitionID>M</MaterialDefinitionID>
                  <Quantity><QuantityString>5</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity></MaterialProducedRequirement></SegmentRequirement>
              </ProductionRequest>
            </ProductionSchedule>
            """;

        var rows = WorkOrderB2mml.Parse(Text, "ERP", null).Rows;

        var bad = rows[0];
        Assert.Null(bad.Order);
        Assert.Equal(3, bad.Line);
        Assert.Contains("ID is missing; it is the order number.", bad.Problems.Select(p => p.Text));
        Assert.Contains("It produces 2 materials; a work order makes one.", bad.Problems.Select(p => p.Text));
        Assert.Contains("StartTime 'next week' isn't an ISO 8601 time.", bad.Problems.Select(p => p.Text));
        Assert.Contains("Priority 'high' isn't a whole number.", bad.Problems.Select(p => p.Text));
        Assert.Equal("OK-1", rows[1].Order!.OrderNumber);
    }

    [Fact]
    public void Canceled_requests_cancel_and_an_id_twice_is_one_order()
    {
        static string Request(string state) => $"""
            <ProductionRequest><ID>X-1</ID><StartTime>2026-12-01T06:00:00Z</StartTime><RequestState>{state}</RequestState>
              <SegmentRequirement><MaterialProducedRequirement><MaterialDefinitionID>M</MaterialDefinitionID>
                <Quantity><QuantityString>5</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity></MaterialProducedRequirement></SegmentRequirement>
            </ProductionRequest>
            """;

        Assert.True(Assert.Single(WorkOrderB2mml.Parse($"<ProductionSchedule>{Request("Canceled")}</ProductionSchedule>", "ERP", "PLT1").Rows).Order!.Canceled);
        Assert.False(Assert.Single(WorkOrderB2mml.Parse($"<ProductionSchedule>{Request("Released")}</ProductionSchedule>", "ERP", "PLT1").Rows).Order!.Canceled);
        var twice = WorkOrderB2mml.Parse($"<ProductionSchedule>{Request("Released")}{Request("Released")}</ProductionSchedule>", "ERP", "PLT1").Rows;
        Assert.NotNull(twice[0].Order);
        Assert.Null(twice[1].Order);
        Assert.Contains("appears more than once", Assert.Single(twice[1].Problems)?.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "The file is empty.")]
    [InlineData("<ProductionSchedule>", "The file isn't well-formed XML")]
    [InlineData("<ProductionSchedule><ID>S</ID></ProductionSchedule>", "The file has no ProductionRequest elements")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"boom\">]><ProductionSchedule>&e;</ProductionSchedule>", "The file isn't well-formed XML")]
    public void A_file_it_cant_read_says_why(string text, string problem)
    {
        var source = WorkOrderB2mml.Parse(text, "ERP", "PLT1");

        Assert.Empty(source.Rows);
        Assert.StartsWith(problem, Assert.Single(source.Problems)?.Text, StringComparison.Ordinal);
    }
}
