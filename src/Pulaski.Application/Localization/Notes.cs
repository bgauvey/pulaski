using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Localization;

/// <summary>
/// Notes and warnings for people (#189), built as refusals are (D-114): <c>Notes.Of($"{job.JobNumber} started.")</c> keeps the English text,
/// the template <c>"{0} started."</c> and its values, so a screen shows it in its reader's language. A plain string is its own template.
/// </summary>
public static class Notes
{
    public static MessageDto Of(string text) => Dto(UserMessage.Of(text));

    public static MessageDto Of(ref MessageBuilder message) => Dto(message.Build());

    /// <summary>An inventory warning as a note, keeping its template.</summary>
    public static MessageDto Of(InventoryWarningDto warning) => new(warning.Message) { Template = warning.Template, Values = warning.Values };

    /// <summary>A schedule warning as a note, keeping its template.</summary>
    public static MessageDto Of(ScheduleWarningDto warning) => new(warning.Message) { Template = warning.Template, Values = warning.Values };

    /// <summary>A note already made as a <see cref="UserMessage"/> (a domain rule's, say).</summary>
    public static MessageDto Of(UserMessage message) => Dto(message);

    /// <summary>An inventory warning with its code (the screens compare codes, never words).</summary>
    public static InventoryWarningDto Inventory(string code, string message) => Inventory(code, UserMessage.Of(message));

    /// <inheritdoc cref="Inventory(string, string)"/>
    public static InventoryWarningDto Inventory(string code, ref MessageBuilder message) => Inventory(code, message.Build());

    /// <summary>A schedule warning with its code.</summary>
    public static ScheduleWarningDto Schedule(string code, string message) => Schedule(code, UserMessage.Of(message));

    /// <inheritdoc cref="Schedule(string, string)"/>
    public static ScheduleWarningDto Schedule(string code, ref MessageBuilder message) => Schedule(code, message.Build());

    /// <summary>A schedule warning made by the domain's projector.</summary>
    public static ScheduleWarningDto Schedule(ScheduleWarning warning) => Schedule(warning.Code, warning.Text);

    /// <summary>A problem that stops a work order being made from a process, with its code (and the operation and equipment it is about).</summary>
    public static ProcessOrderProblemDto OrderProblem(string code, string message, int? operationNumber = null, string? equipmentCode = null) =>
        OrderProblem(code, UserMessage.Of(message), operationNumber, equipmentCode);

    /// <inheritdoc cref="OrderProblem(string, string, int?, string?)"/>
    public static ProcessOrderProblemDto OrderProblem(string code, ref MessageBuilder message, int? operationNumber = null, string? equipmentCode = null) =>
        OrderProblem(code, message.Build(), operationNumber, equipmentCode);

    private static ProcessOrderProblemDto OrderProblem(string code, UserMessage message, int? operationNumber, string? equipmentCode) =>
        new(code, message.Text, operationNumber, equipmentCode) { Template = message.Template, Values = message.Values };

    private static InventoryWarningDto Inventory(string code, UserMessage message) =>
        new(code, message.Text) { Template = message.Template, Values = message.Values };

    private static ScheduleWarningDto Schedule(string code, UserMessage message) =>
        new(code, message.Text) { Template = message.Template, Values = message.Values };

    private static MessageDto Dto(UserMessage message) => new(message.Text) { Template = message.Template, Values = message.Values };
}
