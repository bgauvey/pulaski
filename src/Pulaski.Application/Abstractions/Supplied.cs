namespace Pulaski.Application.Abstractions;

/// <summary>
/// A value an import source supplied (D-034, D-054); a field left out of the source is null instead, and keeps what the record has.
/// </summary>
public sealed record Supplied<T>(T Value);
