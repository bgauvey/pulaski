namespace Pulaski.Contracts.Common;

/// <summary>
/// A note or warning for a person (#189): its English <see cref="Text"/>, and the <see cref="Template"/> with {0}, {1}… and the
/// <see cref="Values"/> that fill it, so a screen shows it in its reader's language (D-114). A screen without the template shows the text.
/// Numbers among the values stay numbers, for the reader's region.
/// </summary>
public sealed record MessageDto(string Text)
{
    public string? Template { get; init; }

    public IReadOnlyList<object?> Values { get; init; } = [];

    public override string ToString() => Text;
}
