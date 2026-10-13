using Npgsql;
using Pulaski.Application.Errors;
using Pulaski.Domain;

namespace Pulaski.Infrastructure.Persistence;

/// <summary>Maps constraint violations to application exceptions with messages fit for a user.</summary>
internal static class PostgresErrors
{
    private static readonly Dictionary<string, string> UniqueMessages = new(StringComparer.Ordinal)
    {
        ["equipment_code_uq"] = "That equipment code is already used in this site.",
        ["wo_number_uq"] = "That work order number is already used in this site.",
    };

    public static bool TryTranslate(PostgresException ex, out Exception translated)
    {
        translated = ex.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new ConflictException(
                ex.ConstraintName is { } name && UniqueMessages.TryGetValue(name, out var message)
                    ? message
                    : "A record with the same key already exists."),
            PostgresErrorCodes.ForeignKeyViolation => new DomainRuleException(
                "A referenced record does not exist."),
            PostgresErrorCodes.CheckViolation => new DomainRuleException(
                $"The change breaks the rule '{ex.ConstraintName}'."),
            _ => ex,
        };

        return !ReferenceEquals(translated, ex);
    }
}
