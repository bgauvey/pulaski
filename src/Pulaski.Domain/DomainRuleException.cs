namespace Pulaski.Domain;

/// <summary>
/// A business rule was violated. The message is safe to show to a user, and carries its template and values so a screen can show it in its
/// reader's language (#128, D-114).
/// </summary>
public sealed class DomainRuleException : Exception, IHasUserMessage
{
    public DomainRuleException(string message)
        : this(UserMessage.Of(message))
    {
    }

    public DomainRuleException(ref MessageBuilder message)
        : this(message.Build())
    {
    }

    /// <summary>A message worked out elsewhere (a list of problems joined, say).</summary>
    public DomainRuleException(UserMessage message)
        : base(message.Text) => UserMessage = message;

    public UserMessage UserMessage { get; }
}
