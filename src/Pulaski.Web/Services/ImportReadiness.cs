namespace Pulaski.Web.Services;

/// <summary>Why a file import's Import button can't be used yet (#47); null when it can. Every import checks the file first, then imports it.</summary>
public static class ImportReadiness
{
    public static string? Why(bool isChecked, bool applied, int fileProblems, int errors, int changes) =>
        !isChecked ? "Pick a file first: Pulaski checks it, then you import it."
        : applied ? "Imported. Pick another file to import more."
        : fileProblems > 0 || errors > 0 ? "Fix what the check found, then pick the file again."
        : changes == 0 ? "The file changes nothing."
        : null;
}
