using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Localization;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Quality;
using Pulaski.Domain;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Quality;

/// <summary>Draws a certificate as a PDF (#137); the API's renderer.</summary>
public interface ICertificateRenderer
{
    byte[] Render(CertificateDto certificate);
}

/// <summary>
/// A lot's certificate of analysis, worked out now (#137, D-123): one line per item of its material's released specification, from the lot's
/// results, else (for an item the lot has none of) the results recorded against the job that made it with no lot named. Excluded results are left
/// out. Never refused for what it finds: failures, missing results and a held lot are warnings, and say so on the certificate.
/// </summary>
public sealed record GetCertificateQuery(Guid LotId, string? Reference = null) : IQuery<CertificateDto>;

/// <summary>Issues a lot's certificate (#137): drawn as a PDF and attached as a lot document of kind <c>coa</c>, sent like any certificate.</summary>
public sealed record IssueCertificateCommand(Guid LotId, string? Reference) : ICommand<IssuedCertificateResponse>;

internal sealed class GetCertificateHandler(
    IQueryHandler<GetLotQuery, LotDto> lots,
    IQueryHandler<GetEquipmentQuery, EquipmentDto> equipment,
    IQueryHandler<ListQualityResultsQuery, IReadOnlyList<QualityResultDto>> results,
    IQualityReadStore quality,
    TimeProvider time) : IQueryHandler<GetCertificateQuery, CertificateDto>
{
    public const int MaxReference = 100;

    public async Task<CertificateDto> HandleAsync(GetCertificateQuery query, CancellationToken cancellationToken)
    {
        var reference = string.IsNullOrWhiteSpace(query.Reference) ? null : query.Reference.Trim();
        new Validator().Require(reference is null || reference.Length <= MaxReference, "Reference", $"At most {MaxReference} characters.").ThrowIfInvalid();

        var found = await lots.HandleAsync(new GetLotQuery(query.LotId), cancellationToken);
        var (lot, timeZone) = (found.Lot, found.SiteTimeZone);
        var spec = await quality.GetReleasedSpecAsync(lot.MaterialId, cancellationToken)
            ?? throw new DomainRuleException($"{lot.MaterialCode} has no released specification, so there is nothing to certify against.");
        var site = await equipment.HandleAsync(new GetEquipmentQuery(lot.SiteId), cancellationToken);
        var decimals = (await quality.GetCharacteristicsAsync([.. spec.Items.Select(i => i.CharacteristicId)], cancellationToken)).ToDictionary(c => c.Id, c => c.Decimals);

        var ofLot = (await results.HandleAsync(new ListQualityResultsQuery(new QualityResultFilter(null, LotId: lot.Id, Limit: 5000)), cancellationToken))
            .Where(r => !r.IsExcluded).ToLookup(r => r.CharacteristicId);
        var ofJob = lot.ProducedByJobId is { } jobId
            ? (await results.HandleAsync(new ListQualityResultsQuery(new QualityResultFilter(null, JobId: jobId, Limit: 5000)), cancellationToken))
                .Where(r => !r.IsExcluded && r.LotId is null).ToLookup(r => r.CharacteristicId)
            : Enumerable.Empty<QualityResultDto>().ToLookup(r => r.CharacteristicId);

        var lines = spec.Items.Select(item =>
        {
            var measured = item.DataKind == CharacteristicKinds.Variable;
            var (source, found) = ofLot[item.CharacteristicId].Any() ? ("lot", ofLot[item.CharacteristicId]) : ("job", ofJob[item.CharacteristicId]);
            var summary = Certificates.Summarize(measured, [.. found.Select(r => new CertificateResult(r.Values, r.Inspected, r.Defects, r.Conforms))]);
            return new CertificateLineDto
            {
                CharacteristicCode = item.Code,
                CharacteristicName = item.Name,
                UomCode = item.UomCode,
                Requirement = item.Limits,
                Measured = measured,
                Results = summary.Results,
                Mean = summary.Mean,
                Min = summary.Min,
                Max = summary.Max,
                Inspected = summary.Inspected,
                Defects = summary.Defects,
                Decimals = decimals.GetValueOrDefault(item.CharacteristicId, 3),
                Outcome = summary.Outcome,
                Source = summary.Results == 0 ? "lot" : source,
            };
        }).ToList();

        var warnings = new List<MessageDto>();
        if (lot.Status is Domain.Inventory.LotStatus.OnHold or Domain.Inventory.LotStatus.Quarantine)
        {
            warnings.Add(Notes.Of($"The lot is {UserMessage.Code(lot.Status)}."));
        }

        warnings.AddRange(lines.Where(l => l.Outcome == CertificateOutcome.Fail).Select(l => Notes.Of($"{l.CharacteristicCode} is out of specification.")));
        warnings.AddRange(lines.Where(l => l.Outcome == CertificateOutcome.NoResult).Select(l => Notes.Of($"{l.CharacteristicCode} has no result.")));
        warnings.AddRange(lines.Where(l => l.Outcome == CertificateOutcome.NotJudged).Select(l => Notes.Of($"{l.CharacteristicCode}'s results weren't judged against limits.")));
        return new CertificateDto
        {
            LotId = lot.Id,
            LotNumber = lot.LotNumber,
            MaterialCode = lot.MaterialCode,
            MaterialName = lot.MaterialName,
            Quantity = lot.Quantity,
            UomCode = lot.UomCode,
            LotStatus = lot.Status,
            SupplierLot = lot.SupplierLot,
            ExpiresAt = lot.ExpiresAt,
            ProducedByJobNumber = lot.ProducedByJobNumber,
            SiteCode = site.Code,
            SiteName = site.Name,
            SiteTimeZone = timeZone,
            SpecVersion = spec.VersionNumber,
            SpecReleasedAt = spec.ReleasedAt,
            IssuedAt = time.GetUtcNow(),
            Reference = reference,
            Lines = lines,
            Conforms = warnings.Count == 0,
            Warnings = warnings,
        };
    }
}

internal sealed class IssueCertificateHandler(
    IQueryHandler<GetCertificateQuery, CertificateDto> certificates,
    ICommandHandler<AttachLotDocumentCommand, CreatedResponse> attach,
    ICertificateRenderer renderer) : ICommandHandler<IssueCertificateCommand, IssuedCertificateResponse>
{
    public async Task<IssuedCertificateResponse> HandleAsync(IssueCertificateCommand command, CancellationToken cancellationToken)
    {
        var certificate = await certificates.HandleAsync(new GetCertificateQuery(command.LotId, command.Reference), cancellationToken);
        var label = certificate.LotNumber ?? certificate.MaterialCode;
        var stamp = certificate.IssuedAt.UtcDateTime.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var issuedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(certificate.IssuedAt, TimeZoneInfo.FindSystemTimeZoneById(certificate.SiteTimeZone ?? "UTC")).DateTime);
        var attached = await attach.HandleAsync(new AttachLotDocumentCommand(
            command.LotId, "coa", $"Certificate of analysis {label}", $"COA-{label}-{stamp}", certificate.SiteName, issuedOn, null,
            $"COA-{label}-{stamp}.pdf", renderer.Render(certificate)), cancellationToken);
        return new IssuedCertificateResponse(attached.Id, certificate.Warnings);
    }
}
