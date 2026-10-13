namespace Pulaski.Web.Components;

/// <summary>How a <see cref="PropertyItem"/> is edited in the <see cref="Shared.PropertyGrid"/>.</summary>
public enum PropertyEditor
{
    /// <summary>Shown, not editable.</summary>
    ReadOnly,

    /// <summary>Free text, committed on Enter or when focus leaves.</summary>
    Text,

    /// <summary>One of <see cref="PropertyItem.Choices"/>, committed on selection.</summary>
    Choice,
}

/// <summary>An option of a <see cref="PropertyEditor.Choice"/> property.</summary>
public sealed record PropertyChoice(string Value, string Label)
{
    /// <summary>True/False, as a property grid shows a boolean.</summary>
    public static IReadOnlyList<PropertyChoice> Boolean { get; } =
        [new("true", "True"), new("false", "False")];
}

/// <summary>One row of a <see cref="Shared.PropertyGrid"/>. Values travel as text; booleans as "true"/"false".</summary>
public sealed record PropertyItem
{
    /// <summary>Stable key the owner uses to apply a change.</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    public required string Category { get; init; }

    /// <summary>Shown in the description pane when the row is selected.</summary>
    public string? Description { get; init; }

    public string? Value { get; init; }

    public PropertyEditor Editor { get; init; } = PropertyEditor.ReadOnly;

    public IReadOnlyList<PropertyChoice> Choices { get; init; } = [];

    public string? Placeholder { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>The text shown for the value: a choice's label, or the value itself.</summary>
    public string DisplayValue =>
        Choices.FirstOrDefault(c => c.Value == Value)?.Label ?? Value ?? "";

    public static string FromBool(bool value) => value ? "true" : "false";
}

/// <summary>A committed edit: the property's key and its new value.</summary>
public sealed record PropertyChange(string Key, string? Value);
