using Pulaski.Domain;

namespace Pulaski.Application.Errors;

/// <summary>The requested resource does not exist. Its message carries its template and values, for the reader's language (#128).</summary>
public sealed class NotFoundException : Exception, IHasUserMessage
{
    public NotFoundException(string message)
        : this(UserMessage.Of(message))
    {
    }

    public NotFoundException(ref MessageBuilder message)
        : this(message.Build())
    {
    }

    /// <summary>A message worked out elsewhere (a list of problems joined, say).</summary>
    public NotFoundException(UserMessage message)
        : base(message.Text) => UserMessage = message;

    public UserMessage UserMessage { get; }
}

/// <summary>Who is calling isn't known or can't sign in (D-037).</summary>
public sealed class UnauthorizedException(string message) : Exception(message);

/// <summary>The caller is known but may not do this (D-037). Its message carries its template and values, for the reader's language (#128).</summary>
public sealed class ForbiddenException : Exception, IHasUserMessage
{
    public ForbiddenException(string message)
        : this(UserMessage.Of(message))
    {
    }

    public ForbiddenException(ref MessageBuilder message)
        : this(message.Build())
    {
    }

    /// <summary>A message worked out elsewhere (a list of problems joined, say).</summary>
    public ForbiddenException(UserMessage message)
        : base(message.Text) => UserMessage = message;

    public UserMessage UserMessage { get; }
}

/// <summary>The resource changed since it was read, or a unique key is taken. Its message carries its template and values, for the reader's language (#128).</summary>
public sealed class ConflictException : Exception, IHasUserMessage
{
    public ConflictException(string message)
        : this(UserMessage.Of(message))
    {
    }

    public ConflictException(ref MessageBuilder message)
        : this(message.Build())
    {
    }

    /// <summary>A message worked out elsewhere (a list of problems joined, say).</summary>
    public ConflictException(UserMessage message)
        : base(message.Text) => UserMessage = message;

    public UserMessage UserMessage { get; }
}

/// <summary>
/// The request is malformed. <see cref="Errors"/> is keyed by field name; <see cref="Messages"/> holds the same messages with their templates and
/// values, so a screen can show them in its reader's language (#128, #189).
/// </summary>
public sealed class ValidationException : Exception
{
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : this(errors.ToDictionary(e => e.Key, e => e.Value.Select(UserMessage.Of).ToArray(), StringComparer.Ordinal))
    {
    }

    public ValidationException(IReadOnlyDictionary<string, UserMessage[]> messages)
        : base("One or more fields are invalid.")
    {
        Messages = messages;
        Errors = messages.ToDictionary(m => m.Key, m => m.Value.Select(v => v.Text).ToArray(), StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public IReadOnlyDictionary<string, UserMessage[]> Messages { get; }

    /// <summary>One field's message: <c>throw ValidationException.Field("Body", $"At most {max} characters.")</c>.</summary>
    public static ValidationException Field(string field, string message) => Field(field, UserMessage.Of(message));

    /// <inheritdoc cref="Field(string, string)"/>
    public static ValidationException Field(string field, ref MessageBuilder message) => Field(field, message.Build());

    /// <summary>One field's messages, worked out elsewhere (a list of problems).</summary>
    public static ValidationException Field(string field, IEnumerable<string> messages) => Field(field, messages.Select(UserMessage.Of));

    /// <inheritdoc cref="Field(string, IEnumerable{string})"/>
    public static ValidationException Field(string field, IEnumerable<UserMessage> messages) =>
        new(new Dictionary<string, UserMessage[]>(StringComparer.Ordinal) { [field] = [.. messages] });

    /// <summary>One field's message, worked out elsewhere.</summary>
    public static ValidationException Field(string field, UserMessage message) =>
        new(new Dictionary<string, UserMessage[]>(StringComparer.Ordinal) { [field] = [message] });
}

/// <summary>Collects field errors and throws them together.</summary>
internal sealed class Validator
{
    private readonly Dictionary<string, List<UserMessage>> _errors = new(StringComparer.Ordinal);

    /// <summary>Adds <paramref name="message"/> for <paramref name="field"/> unless <paramref name="condition"/> holds.</summary>
    public Validator Require(bool condition, string field, string message) => condition ? this : Add(field, UserMessage.Of(message));

    /// <summary>
    /// The same with values (#189): <c>$"At most {max} characters."</c> keeps its template and values, so a screen shows it in its reader's
    /// language.
    /// </summary>
    public Validator Require(bool condition, string field, ref MessageBuilder message) => condition ? this : Add(field, message.Build());

    /// <summary>The same with a message worked out elsewhere (#188); null is no problem.</summary>
    public Validator Require(string field, UserMessage? problem) => problem is null ? this : Add(field, problem);

    private Validator Add(string field, UserMessage message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            list = [];
            _errors[field] = list;
        }

        list.Add(message);
        return this;
    }

    public Validator RequireText(string? value, string field, int maxLength = 200) =>
        Require(!string.IsNullOrWhiteSpace(value), field, "Required.")
            .Require(value is null || value.Length <= maxLength, field, $"At most {maxLength} characters.");

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw new ValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal));
        }
    }
}
