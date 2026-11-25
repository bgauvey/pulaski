using System.Globalization;
using System.Xml.Linq;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Contracts.Execution;
using Pulaski.Domain;

namespace Pulaski.Application.WorkOrders;

/// <summary>What a job's production performance needs beyond its execution (D-102): whose order it is, and its lots.</summary>
public sealed record JobPerformanceFacts
{
    public required string SiteCode { get; init; }

    /// <summary>The ERP's key for the work order (D-066), sent back as the request it answers; null for a work order made in Pulaski.</summary>
    public string? ExternalKey { get; init; }
    public string? ExternalSystem { get; init; }

    /// <summary>The process operation the job is for, the segment it answers; null when the order has no process.</summary>
    public int? OperationNumber { get; init; }

    /// <summary>Every lot the job produced or consumed, with what its standing declarations add up to.</summary>
    public IReadOnlyList<JobLotQuantity> Lots { get; init; } = [];
}

/// <summary>
/// One lot's standing declarations for a job, in the material's base unit: <see cref="Produced"/> into it or consumed from it.
/// A balance (D-031) has no lot number; it is named by <see cref="LocationCode"/>.
/// </summary>
public sealed record JobLotQuantity
{
    public bool Produced { get; init; }
    public required string MaterialCode { get; init; }
    public string? LotNumber { get; init; }
    public string? LocationCode { get; init; }
    public decimal Quantity { get; init; }
    public required string UomCode { get; init; }
}

public interface IProductionPerformanceReadStore
{
    /// <summary>Null when the job doesn't exist.</summary>
    Task<JobPerformanceFacts?> GetJobFactsAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <summary>
/// A job's totals as a B2MML production performance document (D-102); with <paramref name="SyncId"/>, wrapped as the
/// <c>SyncProductionPerformance</c> a B2MML feed sends, that id its <c>BODID</c>.
/// </summary>
public sealed record GetJobProductionPerformanceQuery(Guid JobId, Guid? SyncId = null) : IQuery<ProductionPerformanceDocument>;

public sealed record ProductionPerformanceDocument(string JobNumber, string Xml);

internal sealed class GetJobProductionPerformanceHandler(
    IExecutionReadStore execution, IProductionPerformanceReadStore store, TimeProvider timeProvider)
    : IQueryHandler<GetJobProductionPerformanceQuery, ProductionPerformanceDocument>
{
    public async Task<ProductionPerformanceDocument> HandleAsync(GetJobProductionPerformanceQuery query, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var job = await execution.GetJobAsync(query.JobId, now, cancellationToken) ?? throw new NotFoundException($"Job {query.JobId} not found.");
        if (job.JobType != "production")
        {
            throw ValidationException.Field("jobId", $"Job {job.JobNumber} is a {UserMessage.Code(job.JobType)} job; only production jobs have production performance.");
        }

        var facts = await store.GetJobFactsAsync(query.JobId, cancellationToken) ?? throw new NotFoundException($"Job {query.JobId} not found.");
        return new ProductionPerformanceDocument(
            job.JobNumber,
            query.SyncId is { } id ? ProductionPerformanceB2mml.WriteSync(job, facts, now, id) : ProductionPerformanceB2mml.Write(job, facts, now));
    }
}

/// <summary>
/// Writes a job's totals as an ISA-95 production performance in B2MML (D-102): one <c>ProductionResponse</c> answering the work order's
/// production request, re-made whole after every change so a receiver replaces what it had. Pure.
/// <list type="bullet">
/// <item><c>ProductionPerformance/ID</c>, <c>ProductionResponse/ID</c> and <c>SegmentResponse/ID</c> are the job number;
/// <c>ProductionRequestID</c> the ERP's key for the order (the order number for an order made in Pulaski).</item>
/// <item>The site is the <c>HierarchyScope</c>; the operation number, if any, the <c>ProcessSegmentID</c>; each line it ran on an
/// <c>EquipmentActual</c>.</item>
/// <item>Times: the first run's start; the end once the job is completed (else its last run's end, while it isn't running). The state is
/// the job's: <c>Ready</c> before it starts, <c>Running</c>, <c>Held</c> (paused), <c>Completed</c> (completed or closed), <c>Aborted</c>
/// (cancelled).</item>
/// <item>Good and scrap totals in the job's unit are <c>SegmentData</c> <c>GoodQuantity</c> and <c>ScrapQuantity</c>; reversed reports are
/// out of them.</item>
/// <item>Each lot produced or consumed is a <c>MaterialActual</c> (<c>Produced</c> / <c>Consumed</c>) with its standing quantity in the
/// material's base unit; a balance has no <c>MaterialLotID</c>, only its <c>StorageLocation</c>.</item>
/// </list>
/// </summary>
public static class ProductionPerformanceB2mml
{
    public static readonly XNamespace Ns = "http://www.mesa.org/xml/B2MML";

    public static string Write(JobExecutionDto job, JobPerformanceFacts facts, DateTimeOffset publishedAt) =>
        Text(Document(job, facts, publishedAt));

    /// <summary>
    /// The document as a B2MML verb, <c>SyncProductionPerformance</c>, which a B2MML feed sends: <c>ApplicationArea</c> with the sender
    /// (<c>Pulaski</c>), the time and <paramref name="bodId"/> (the event's id, also the delivery's <c>Idempotency-Key</c>), and a
    /// <c>Sync</c> whose action is <c>Replace</c>: each document replaces the one before for the job.
    /// </summary>
    public static string WriteSync(JobExecutionDto job, JobPerformanceFacts facts, DateTimeOffset publishedAt, Guid bodId) =>
        Text(new XElement(Ns + "SyncProductionPerformance",
            new XElement(Ns + "ApplicationArea",
                new XElement(Ns + "Sender", new XElement(Ns + "LogicalID", "Pulaski")),
                new XElement(Ns + "CreationDateTime", Time(publishedAt)),
                new XElement(Ns + "BODID", bodId.ToString())),
            new XElement(Ns + "DataArea",
                new XElement(Ns + "Sync",
                    new XElement(Ns + "ActionCriteria", new XElement(Ns + "ActionExpression", new XAttribute("actionCode", "Replace")))),
                Document(job, facts, publishedAt))));

    private static string Text(XElement root) =>
        new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + Environment.NewLine + root + Environment.NewLine;

    /// <summary>The <c>ProductionPerformance</c> element, for a verb to wrap.</summary>
    public static XElement Document(JobExecutionDto job, JobPerformanceFacts facts, DateTimeOffset publishedAt)
    {
        var start = job.Runs.Count == 0 ? (DateTimeOffset?)null : job.Runs.Min(r => r.StartedAt);
        var end = job.CompletedAt
            ?? (job.Status != "running" && job.Runs.Count > 0 && job.Runs.All(r => r.EndedAt is not null) ? job.Runs.Max(r => r.EndedAt) : null);
        var state = State(job.Status);

        return new XElement(Ns + "ProductionPerformance",
            new XElement(Ns + "ID", job.JobNumber),
            new XElement(Ns + "HierarchyScope",
                new XElement(Ns + "EquipmentID", facts.SiteCode),
                new XElement(Ns + "EquipmentElementLevel", "Site")),
            new XElement(Ns + "PublishedDate", Time(publishedAt)),
            new XElement(Ns + "ProductionResponse",
                new XElement(Ns + "ID", job.JobNumber),
                new XElement(Ns + "ProductionRequestID", facts.ExternalKey ?? job.WorkOrderNumber),
                start is { } s ? new XElement(Ns + "StartTime", Time(s)) : null,
                end is { } e ? new XElement(Ns + "EndTime", Time(e)) : null,
                new XElement(Ns + "ResponseState", state),
                new XElement(Ns + "SegmentResponse",
                    new XElement(Ns + "ID", job.JobNumber),
                    facts.OperationNumber is { } op ? new XElement(Ns + "ProcessSegmentID", op.ToString(CultureInfo.InvariantCulture)) : null,
                    start is { } rs ? new XElement(Ns + "ActualStartTime", Time(rs)) : null,
                    end is { } re ? new XElement(Ns + "ActualEndTime", Time(re)) : null,
                    new XElement(Ns + "SegmentState", state),
                    Data("GoodQuantity", job.GoodQty, job.UomCode),
                    Data("ScrapQuantity", job.ScrapQty, job.UomCode),
                    Lines(job).Select(line => new XElement(Ns + "EquipmentActual", new XElement(Ns + "EquipmentID", line))),
                    facts.Lots.OrderBy(l => !l.Produced).Select(Material))));
    }

    /// <summary>ISA-95's response state for a job status.</summary>
    public static string State(string jobStatus) => jobStatus switch
    {
        "running" => "Running",
        "paused" => "Held",
        "completed" or "closed" => "Completed",
        "cancelled" => "Aborted",
        _ => "Ready",
    };

    // The lines its runs were on, in the order it first ran on them; the job's line before it has run.
    private static IEnumerable<string> Lines(JobExecutionDto job) =>
        job.Runs.Count == 0 ? [job.LineCode] : job.Runs.OrderBy(r => r.RunNo).Select(r => r.LineCode).Distinct(StringComparer.Ordinal);

    private static XElement Data(string id, decimal value, string? uom) =>
        new(Ns + "SegmentData",
            new XElement(Ns + "ID", id),
            new XElement(Ns + "Value",
                new XElement(Ns + "ValueString", Number(value)),
                new XElement(Ns + "DataType", "decimal"),
                uom is null ? null : new XElement(Ns + "UnitOfMeasure", uom)));

    private static XElement Material(JobLotQuantity lot) =>
        new(Ns + "MaterialActual",
            new XElement(Ns + "MaterialDefinitionID", lot.MaterialCode),
            lot.LotNumber is { } number ? new XElement(Ns + "MaterialLotID", number) : null,
            lot.LotNumber is null && lot.LocationCode is { } location ? new XElement(Ns + "StorageLocation", location) : null,
            new XElement(Ns + "MaterialUse", lot.Produced ? "Produced" : "Consumed"),
            new XElement(Ns + "Quantity",
                new XElement(Ns + "QuantityString", Number(lot.Quantity)),
                new XElement(Ns + "DataType", "decimal"),
                new XElement(Ns + "UnitOfMeasure", lot.UomCode)));

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
