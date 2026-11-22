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

    private NotFoundException(UserMessage message)
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

    private ForbiddenException(UserMessage message)
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

    private ConflictException(UserMessage message)
        : base(message.Text) => UserMessage = message;

    public UserMessage UserMessage { get; }
}

/// <summary>The request is malformed. <see cref="Errors"/> is keyed by field name.</summary>
public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("One or more fields are invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Collects field errors and throws them together.</summary>
internal sealed class Validator
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public Validator Require(bool condition, string field, string message)
    {
        if (!condition)
        {
            if (!_errors.TryGetValue(field, out var list))
            {
                list = [];
                _errors[field] = list;
            }

            list.Add(message);
        }

        return this;
    }

    public Validator RequireText(string? value, string field, int maxLength = 200) =>
        Require(!string.IsNullOrWhiteSpace(value), field, "Required.")
            .Require(value is null || value.Length <= maxLength, field, $"At most {maxLength} characters.");

    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            throw new ValidationException(_errors.ToDictionary(e => e.Key, e => e.Value.ToArray()));
        }
    }
}
