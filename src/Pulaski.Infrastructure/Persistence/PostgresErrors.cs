using Npgsql;
using Pulaski.Application.Errors;
using Pulaski.Domain;

namespace Pulaski.Infrastructure.Persistence;

/// <summary>Maps constraint violations to application exceptions with messages fit for a user.</summary>
internal static class PostgresErrors
{
    private static readonly Dictionary<string, UserMessage> UniqueMessages = new(StringComparer.Ordinal)
    {
        ["equipment_code_uq"] = UserMessage.Of("That equipment code is already used in this site."),
        ["wo_number_uq"] = UserMessage.Of("That work order number is already used in this site."),
        ["job_number_uq"] = UserMessage.Of("That job number is already used in this site."),
        ["process_code_uq"] = UserMessage.Of("That process code is already used in this site."),
        ["process_version_draft_uq"] = UserMessage.Of("This process already has a draft."),
        ["process_version_released_uq"] = UserMessage.Of("Another version of this process was released at the same time. Reload and try again."),
        ["lot_uq"] = UserMessage.Of("That lot number is already used for this material in this site."),
        ["lot_bulk_uq"] = UserMessage.Of("Someone else started this material's balance at that location at the same moment. Try again."),
        ["stock_count_number_uq"] = UserMessage.Of("Another count was started at the same moment. Try again."),
        ["reason_code_uq"] = UserMessage.Of("That code is already used for this kind of reason."),
        ["machine_profile_code_uq"] = UserMessage.Of("That machine profile code is already used in this site."),
        ["edge_node_code_uq"] = UserMessage.Of("That edge node code is already used in this site."),
        ["edge_connection_code_uq"] = UserMessage.Of("The edge node already has a connection with that code."),
        ["equipment_tag_role_uq"] = UserMessage.Of("That equipment already has an active tag for this role; switch the other one off first."),
        ["equipment_tag_source_uq"] = UserMessage.Of("That address is already bound to an active tag on this connection."),
        ["material_code_key"] = UserMessage.Of("That material code is already used."),
        ["material_external_uq"] = UserMessage.Of("Another material already has that external key."),
        ["material_class_code_key"] = UserMessage.Of("That class code is already used."),
    };

    public static bool TryTranslate(PostgresException ex, out Exception translated)
    {
        translated = ex.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => new ConflictException(
                ex.ConstraintName is { } name && UniqueMessages.TryGetValue(name, out var message)
                    ? message
                    : UserMessage.Of("A record with the same key already exists.")),
            PostgresErrorCodes.ForeignKeyViolation => new DomainRuleException(
                "A referenced record does not exist."),
            PostgresErrorCodes.CheckViolation => new DomainRuleException(
                $"The change breaks the rule '{ex.ConstraintName}'."),
            // Two periods of one capability overlap (D-034): another change got in between the check and the write.
            PostgresErrorCodes.ExclusionViolation when ex.ConstraintName == "capability_no_overlap" => new ConflictException(
                "Another capability for this material on this line was changed at the same moment, and the periods now overlap. Reload and try again."),
            // Two commands took the same rows in opposite order (e.g. opposite bulk transfers, D-027); one was rolled back.
            PostgresErrorCodes.DeadlockDetected => new ConflictException(
                "Someone else changed the same records at the same moment. Try again."),
            _ => ex,
        };

        return !ReferenceEquals(translated, ex);
    }
}
