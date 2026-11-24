namespace Pulaski.Application.Abstractions;

/// <summary>Builds the plain-language notes some commands return.</summary>
internal static class NoteListExtensions
{
    public static void AddIfSet(this List<Pulaski.Contracts.Common.MessageDto> notes, Pulaski.Contracts.Common.MessageDto? note)
    {
        if (note is not null)
        {
            notes.Add(note);
        }
    }
}
