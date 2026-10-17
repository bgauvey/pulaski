namespace Pulaski.Application.Abstractions;

/// <summary>Builds the plain-language notes some commands return.</summary>
internal static class NoteListExtensions
{
    public static void AddIfSet(this List<string> notes, string? note)
    {
        if (note is not null)
        {
            notes.Add(note);
        }
    }
}
