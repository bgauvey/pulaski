using System.Globalization;
using System.Xml.Linq;
using System.Xml;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain;

namespace Pulaski.Application.WorkOrders;

/// <summary>One ISA-95 production request read from B2MML: the order it asks for, or why it can't be read (<paramref name="Problems"/>).</summary>
public sealed record WorkOrderB2mmlRow(int Line, string RequestId, ErpWorkOrderRequest? Order, IReadOnlyList<UserMessage> Problems);

public sealed record WorkOrderB2mmlSource(IReadOnlyList<WorkOrderB2mmlRow> Rows, IReadOnlyList<UserMessage> Problems);

/// <summary>
/// Reads ISA-95 production schedules in B2MML XML (D-102) into ERP work orders for <see cref="ImportWorkOrdersCommand"/>: every
/// <c>ProductionRequest</c>, whatever wraps it (a <c>ProductionSchedule</c>, a verb such as <c>SyncProductionSchedule</c>, or none), matched
/// by local name in any namespace. Pure; what happens to an order is the import's (D-066).
/// <list type="bullet">
/// <item><c>ID</c> is the order number and the ERP's key; <c>Description</c> the order's note; <c>Priority</c> its priority.</item>
/// <item>The site is the request's <c>HierarchyScope/EquipmentID</c>, else the schedule's, else the one the import names.</item>
/// <item>The produced material is the <c>MaterialProducedRequirement</c> of a <c>SegmentRequirement</c> (B2MML before V0600), or a
/// <c>MaterialRequirement</c> whose <c>MaterialUse</c> is <c>Produced</c>: its <c>MaterialDefinitionID</c> and <c>Quantity</c>
/// (<c>QuantityString</c> + <c>UnitOfMeasure</c>). One material per order.</item>
/// <item>The planned start is the request's <c>StartTime</c>, else the segment's <c>EarliestStartTime</c>; the due time its <c>EndTime</c>,
/// else <c>LatestEndTime</c>.</item>
/// <item>The process is <c>ProductProductionRuleID</c>, else the segment's <c>ProductSegmentID</c> or <c>ProcessSegmentID</c>, as a Pulaski
/// process code; none uses the material's default.</item>
/// <item><c>RequestState</c> <c>Cancelled</c> (or <c>Canceled</c>, <c>Aborted</c>) cancels the order.</item>
/// </list>
/// </summary>
public static class WorkOrderB2mml
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    private static readonly string[] CancelledStates = ["cancelled", "canceled", "aborted"];

    /// <summary>A document with one order and every part it reads, to start from.</summary>
    public const string Template = """
        <?xml version="1.0" encoding="UTF-8"?>
        <ProductionSchedule xmlns="http://www.mesa.org/xml/B2MML">
          <ID>WEEK-47</ID>
          <HierarchyScope><EquipmentID>PLT1</EquipmentID><EquipmentElementLevel>Site</EquipmentElementLevel></HierarchyScope>
          <ProductionRequest>
            <ID>4500012345</ID>
            <Description>Customer order 778, white profile</Description>
            <ProductProductionRuleID>EXTRUDE-PACK</ProductProductionRuleID>
            <StartTime>2026-11-23T06:00:00Z</StartTime>
            <EndTime>2026-11-27T18:00:00Z</EndTime>
            <Priority>2</Priority>
            <RequestState>Released</RequestState>
            <SegmentRequirement>
              <ID>10</ID>
              <MaterialProducedRequirement>
                <MaterialDefinitionID>PRF-4410-WH</MaterialDefinitionID>
                <Quantity><QuantityString>12000</QuantityString><UnitOfMeasure>m</UnitOfMeasure></Quantity>
              </MaterialProducedRequirement>
            </SegmentRequirement>
          </ProductionRequest>
        </ProductionSchedule>

        """;

    /// <param name="text">The B2MML document.</param>
    /// <param name="externalSystem">The ERP the orders belong to; their <c>ID</c>s are its keys.</param>
    /// <param name="siteCode">The site for requests whose schedule names none.</param>
    public static WorkOrderB2mmlSource Parse(string text, string externalSystem, string? siteCode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new WorkOrderB2mmlSource([], [UserMessage.Of("The file is empty.")]);
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new WorkOrderB2mmlSource([], [UserMessage.From($"The file isn't well-formed XML: {ex.Message}")]);
        }

        var requests = document.Descendants().Where(e => e.Name.LocalName == "ProductionRequest").ToList();
        return requests.Count switch
        {
            0 => new WorkOrderB2mmlSource([], [UserMessage.Of("The file has no ProductionRequest elements; a B2MML production schedule has one per order.")]),
            > ImportWorkOrdersHandler.MaxOrders => new WorkOrderB2mmlSource([], [UserMessage.From($"The file has {requests.Count} orders; import at most {ImportWorkOrdersHandler.MaxOrders} at a time.")]),
            _ => new WorkOrderB2mmlSource(Distinct([.. requests.Select(r => ToRow(r, externalSystem, siteCode))]), []),
        };
    }

    // The ID is the ERP's key, so the same one twice in a file is one order sent twice: the first is imported, the rest refused.
    private static List<WorkOrderB2mmlRow> Distinct(List<WorkOrderB2mmlRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. rows.Select(r => r.Order is null || seen.Add(r.RequestId)
            ? r
            : r with { Order = null, Problems = [UserMessage.From($"ID {r.RequestId} appears more than once in the file; only the first is imported.")] })];
    }

    private static WorkOrderB2mmlRow ToRow(XElement request, string externalSystem, string? siteCode)
    {
        var problems = new List<UserMessage>();
        var line = ((IXmlLineInfo)request).HasLineInfo() ? ((IXmlLineInfo)request).LineNumber : 0;
        var id = First(request, "ID") ?? "";
        if (id.Length == 0)
        {
            problems.Add(UserMessage.Of("ID is missing; it is the order number."));
        }

        var segments = Children(request, "SegmentRequirement").ToList();
        var produced = segments.SelectMany(s => Children(s, "MaterialProducedRequirement"))
            .Concat(segments.SelectMany(s => Children(s, "MaterialRequirement")).Where(IsProduced))
            .Concat(Children(request, "MaterialRequirement").Where(IsProduced))
            .ToList();
        string material = "", unit = "";
        decimal quantity = 0;
        switch (produced.Count)
        {
            case 0:
                problems.Add(UserMessage.Of("No produced material: give a SegmentRequirement a MaterialProducedRequirement (or a MaterialRequirement with MaterialUse Produced)."));
                break;
            case > 1:
                problems.Add(UserMessage.From($"It produces {produced.Count} materials; a work order makes one."));
                break;
            default:
                material = First(produced[0], "MaterialDefinitionID") ?? "";
                if (material.Length == 0)
                {
                    problems.Add(UserMessage.Of("The produced material has no MaterialDefinitionID."));
                }

                (quantity, unit) = Quantity(produced[0], problems);
                break;
        }

        var start = Time(First(request, "StartTime") ?? segments.Select(s => First(s, "EarliestStartTime")).FirstOrDefault(t => t is not null), "StartTime", problems);
        if (start is null && problems.All(p => !p.Text.StartsWith("StartTime", StringComparison.Ordinal)))
        {
            problems.Add(UserMessage.Of("StartTime is missing (or a SegmentRequirement's EarliestStartTime); it is the order's planned start."));
        }

        var due = Time(First(request, "EndTime") ?? segments.Select(s => First(s, "LatestEndTime")).FirstOrDefault(t => t is not null), "EndTime", problems);
        int? priority = null;
        if (First(request, "Priority") is { } p)
        {
            if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                priority = number;
            }
            else
            {
                problems.Add(UserMessage.From($"Priority '{p}' isn't a whole number."));
            }
        }

        var site = Scope(request) ?? request.Ancestors().Select(Scope).FirstOrDefault(s => s is not null) ?? siteCode?.Trim();
        if (string.IsNullOrEmpty(site))
        {
            problems.Add(UserMessage.Of("No site: give the request or its schedule a HierarchyScope with the site's EquipmentID, or name the site in the import."));
        }

        if (problems.Count > 0)
        {
            return new WorkOrderB2mmlRow(line, id, null, problems);
        }

        var process = First(request, "ProductProductionRuleID")
            ?? segments.Select(s => First(s, "ProductSegmentID") ?? First(s, "ProcessSegmentID")).FirstOrDefault(c => c is not null);
        return new WorkOrderB2mmlRow(line, id, new ErpWorkOrderRequest
        {
            ExternalSystem = externalSystem,
            ExternalKey = id,
            SiteCode = site!,
            OrderNumber = id,
            MaterialCode = material,
            Quantity = quantity,
            UomCode = unit,
            PlannedStart = start!.Value,
            DueAt = due,
            Priority = priority,
            ProcessCode = process,
            Notes = First(request, "Description"),
            Cancelled = First(request, "RequestState") is { } state && CancelledStates.Contains(state.ToLowerInvariant()),
        }, []);
    }

    private static bool IsProduced(XElement requirement) =>
        string.Equals(First(requirement, "MaterialUse"), "Produced", StringComparison.OrdinalIgnoreCase);

    private static string? Scope(XElement element) =>
        Children(element, "HierarchyScope").FirstOrDefault() is { } scope ? First(scope, "EquipmentID") : null;

    private static (decimal Quantity, string Unit) Quantity(XElement requirement, List<UserMessage> problems)
    {
        if (Children(requirement, "Quantity").FirstOrDefault() is not { } q)
        {
            problems.Add(UserMessage.Of("The produced material has no Quantity."));
            return (0, "");
        }

        var amount = First(q, "QuantityString") ?? (q.HasElements ? null : q.Value.Trim());
        var unit = First(q, "UnitOfMeasure") ?? "";
        if (unit.Length == 0)
        {
            problems.Add(UserMessage.Of("The quantity has no UnitOfMeasure."));
        }

        if (amount is null || !decimal.TryParse(amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity))
        {
            problems.Add(UserMessage.From($"Quantity '{amount}' isn't a number."));
            return (0, unit);
        }

        return (quantity, unit);
    }

    // An ISO 8601 time; without an offset it is UTC, as B2MML's dateTime is read elsewhere (LotB2mml).
    private static DateTimeOffset? Time(string? text, string name, List<UserMessage> problems)
    {
        if (text is null)
        {
            return null;
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return at;
        }

        problems.Add(UserMessage.From($"{name} '{text}' isn't an ISO 8601 time."));
        return null;
    }

    private static string? First(XElement parent, string name) => Children(parent, name).FirstOrDefault()?.Value.Trim() is { Length: > 0 } v ? v : null;

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);
}
