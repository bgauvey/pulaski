namespace Pulaski.Domain;

/// <summary>A business rule was violated. The message is safe to show to a user.</summary>
public sealed class DomainRuleException(string message) : Exception(message);
