using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Pulaski.Domain;

/// <summary>
/// A message for a person (#128, D-114): its English template with placeholders (<c>"{0} isn't a line"</c>), the values that fill them, and
/// the English text. The template is the key the screens translate by, as their own text is. Numbers stay numbers, so a screen writes them in
/// its reader's region; a value that is itself a <see cref="UserMessage"/> is translated on its own, and a <see cref="MessageCode"/> is a status
/// or kind code the screen words (#188); anything else is its text.
/// </summary>
public sealed record UserMessage(string Template, IReadOnlyList<object?> Values, string Text)
{
    /// <summary>A message without values: its text, braces doubled so it reads as a template.</summary>
    public static UserMessage Of(string text) =>
        new(text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal), [], text);

    /// <summary>A message from an interpolated string, for code that words one to return (#189).</summary>
    public static UserMessage From(ref MessageBuilder message) => message.Build();

    /// <summary>
    /// Several messages as one, each translated on its own (#188): the template <c>"{0} {1}"</c> with the messages as its values. None is an
    /// empty message.
    /// </summary>
    public static UserMessage Join(IEnumerable<UserMessage> messages, string separator = " ")
    {
        var parts = messages.ToList();
        var escaped = separator.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
        return parts.Count == 1
            ? parts[0]
            : new(
                string.Join(escaped, parts.Select((_, i) => "{" + i.ToString(CultureInfo.InvariantCulture) + "}")),
                [.. parts],
                string.Join(separator, parts.Select(p => p.Text)));
    }

    /// <summary>A status or kind code put into a message (<c>$"Job {JobNumber} is {UserMessage.Code(Status)}."</c>), which a screen words.</summary>
    public static MessageCode Code(string code) => new(code);

    public override string ToString() => Text;
}

/// <summary>
/// A status or kind code inside a message (#188): its English text is the code as words ('on_hold' → 'on hold'); a screen shows its label in
/// its reader's language.
/// </summary>
public sealed record MessageCode(string Code)
{
    public override string ToString() => Code.Replace('_', ' ');
}

/// <summary>An exception whose message is for a person, with its template and values (#128).</summary>
public interface IHasUserMessage
{
    UserMessage UserMessage { get; }
}

/// <summary>
/// Builds a <see cref="UserMessage"/> from an interpolated string, as the compiler hands its parts over: <c>$"{line.Code} isn't a line"</c>
/// becomes the template <c>"{0} isn't a line"</c> with the value <c>"EXT-01"</c>. A message written as a plain string is its own template.
/// </summary>
[InterpolatedStringHandler]
public ref struct MessageBuilder
{
    private readonly StringBuilder _template;
    private readonly StringBuilder _text;
    private readonly List<object?> _values;

    public MessageBuilder(int literalLength, int formattedCount)
    {
        _template = new StringBuilder(literalLength + formattedCount * 4);
        _text = new StringBuilder(literalLength + formattedCount * 8);
        _values = new List<object?>(formattedCount);
    }

    public readonly void AppendLiteral(string value)
    {
        _template.Append(value.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
        _text.Append(value);
    }

    public readonly void AppendFormatted<T>(T value) => Append(value, alignment: null, format: null);

    public readonly void AppendFormatted<T>(T value, string? format) => Append(value, alignment: null, format);

    public readonly void AppendFormatted<T>(T value, int alignment) => Append(value, alignment, format: null);

    public readonly void AppendFormatted<T>(T value, int alignment, string? format) => Append(value, alignment, format);

    public readonly UserMessage Build() => new(_template.ToString(), [.. _values], _text.ToString());

    private readonly void Append<T>(T value, int? alignment, string? format)
    {
        var index = _values.Count;
        _template.Append('{').Append(index.ToString(CultureInfo.InvariantCulture));
        if (alignment is { } a)
        {
            _template.Append(',').Append(a.ToString(CultureInfo.InvariantCulture));
        }

        if (format is not null)
        {
            _template.Append(':').Append(format);
        }

        _template.Append('}');

        // A message inside a message, or a code, stays one, for the screen to word (#188).
        if (value is UserMessage or MessageCode)
        {
            _text.Append(value.ToString());
            _values.Add(value);
            return;
        }

        var text = value is IFormattable formattable ? formattable.ToString(format, CultureInfo.InvariantCulture) : value?.ToString() ?? "";
        _text.Append(alignment is { } width ? (width < 0 ? text.PadRight(-width) : text.PadLeft(width)) : text);

        // Numbers stay numbers, for the reader's region; the rest is its text.
        _values.Add(value is byte or short or int or long or decimal or double or float ? value : text);
    }
}
