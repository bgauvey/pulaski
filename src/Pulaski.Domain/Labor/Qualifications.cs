namespace Pulaski.Domain.Labor;

/// <summary>
/// A grant of a qualification to a person (D-048): valid from <see cref="ValidFrom"/> to <see cref="ExpiresOn"/>, both whole
/// site-local days and inclusive (no expiry when null), until it is revoked.
/// </summary>
public sealed record QualificationGrant(Guid QualificationId, DateOnly ValidFrom, DateOnly? ExpiresOn, DateTimeOffset? RevokedAt);

/// <summary>A qualification a line, or the equipment above it, requires of whoever works on it (D-048).</summary>
public sealed record RequiredQualification(Guid QualificationId, string Code, string Name);

/// <summary>What a person is missing for a line, and why: never held, expired, revoked or not valid yet.</summary>
public sealed record MissingQualification(RequiredQualification Qualification, string Why);

/// <summary>Grant states as the screens show them (D-048).</summary>
public static class GrantStatus
{
    public const string Valid = "valid";
    public const string NotYet = "not_yet";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

/// <summary>
/// Qualifications (D-048). Pulaski warns, never refuses: a person checked onto a line they aren't qualified for is still
/// checked on, and the warning says what they are missing.
/// </summary>
public static class QualificationRules
{
    public const int MaxValidityMonths = 120;

    /// <summary>A grant's state on a site-local day, at the instant <paramref name="at"/> for revocation.</summary>
    public static string Status(QualificationGrant grant, DateOnly day, DateTimeOffset at) =>
        grant.RevokedAt is { } revoked && revoked <= at ? GrantStatus.Revoked
        : day < grant.ValidFrom ? GrantStatus.NotYet
        : grant.ExpiresOn is { } expires && day > expires ? GrantStatus.Expired
        : GrantStatus.Valid;

    /// <summary>The last valid day of a grant from <paramref name="from"/> lasting <paramref name="months"/> months; null when it doesn't expire.</summary>
    public static DateOnly? DefaultExpiry(DateOnly from, int? months) => months is > 0 ? from.AddMonths(months.Value).AddDays(-1) : null;

    /// <summary>
    /// What an import's listed qualification grants (#71: add and renew, never revoke): null when an unrevoked grant already covers it, else the
    /// new grant's days. With no dates, a grant valid on <paramref name="today"/> covers it and a new one runs from today for its usual
    /// validity. With a valid-from day, one valid that day covers it. With an expiry, one from no later and expiring no sooner (or never) covers
    /// it, so a source that sends a later expiry renews. Running the same import again therefore grants nothing.
    /// </summary>
    /// <param name="held">The person's grants of this qualification.</param>
    /// <param name="validFrom">The day the source says the grant starts; null for today.</param>
    /// <param name="expiresOn">The last day the source says it is valid; null for its usual validity.</param>
    /// <param name="today">The site-local day of the import.</param>
    /// <param name="validityMonths">The qualification's usual validity.</param>
    public static (DateOnly ValidFrom, DateOnly? ExpiresOn)? ImportGrant(
        IEnumerable<QualificationGrant> held, DateOnly? validFrom, DateOnly? expiresOn, DateOnly today, int? validityMonths)
    {
        var from = validFrom ?? today;
        var standing = held.Where(g => g.RevokedAt is null).ToList();
        var covered = expiresOn is { } until
            ? standing.Any(g => g.ValidFrom <= from && (g.ExpiresOn is null || g.ExpiresOn >= until))
            : standing.Any(g => g.ValidFrom <= from && (g.ExpiresOn is null || g.ExpiresOn >= from));
        return covered ? null : (from, expiresOn ?? DefaultExpiry(from, validityMonths));
    }

    /// <returns>What is wrong with a grant's days; null when it can be saved.</returns>
    public static string? GrantProblem(DateOnly validFrom, DateOnly? expiresOn) =>
        validFrom == default ? "Choose the day it is valid from."
        : expiresOn is { } e && e < validFrom ? "It expires on or after the day it is valid from."
        : null;

    /// <summary>The required qualifications the person holds no valid grant of on that day, each with why.</summary>
    public static IReadOnlyList<MissingQualification> Missing(
        IEnumerable<RequiredQualification> required, IReadOnlyList<QualificationGrant> grants, DateOnly day, DateTimeOffset at)
    {
        var missing = new List<MissingQualification>();
        foreach (var q in required.DistinctBy(r => r.QualificationId))
        {
            var held = grants.Where(g => g.QualificationId == q.QualificationId).ToList();
            if (held.Any(g => Status(g, day, at) == GrantStatus.Valid))
            {
                continue;
            }

            var why = held.Count == 0
                ? "not held"
                : held.Where(g => Status(g, day, at) == GrantStatus.Expired).Max(g => g.ExpiresOn) is { } expired
                    ? $"expired {expired:yyyy-MM-dd}"
                    : held.Any(g => Status(g, day, at) == GrantStatus.NotYet)
                        ? $"valid only from {held.Min(g => g.ValidFrom):yyyy-MM-dd}"
                        : "revoked";
            missing.Add(new MissingQualification(q, why));
        }

        return missing;
    }

    /// <summary>"Lee Park isn't qualified for EXT-01: FORK Forklift (expired 2026-10-01)."</summary>
    public static string Describe(string personName, string lineCode, IReadOnlyList<MissingQualification> missing) =>
        $"{personName} isn't qualified for {lineCode}: {string.Join("; ", missing.Select(m => $"{m.Qualification.Code} {m.Qualification.Name} ({m.Why})"))}.";
}
