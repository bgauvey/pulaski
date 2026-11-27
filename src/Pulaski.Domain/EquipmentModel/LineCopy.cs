namespace Pulaski.Domain.EquipmentModel;

/// <summary>What a line copy can bring (#140, D-132), each switched off in the review if not wanted.</summary>
public sealed record LineCopyParts(bool Equipment = true, bool Capabilities = true, bool MachineSettings = true, bool Requirements = true);

/// <summary>One piece of equipment to make: the original, and the code and name the copy gets.</summary>
public sealed record LineCopyItem(Guid SourceId, Guid? SourceParentId, int Depth, string Code, string Name);

/// <summary>Codes and checks for copying a line (#140). Pure.</summary>
public static class LineCopyRules
{
    public const int MaxCode = 50;

    /// <summary>
    /// A copied child's code: one that starts with the line's code gets the new line's instead (EXT-01-SAW → EXT-03-SAW); any other gets the new
    /// line's code in front (SAW → EXT-03-SAW).
    /// </summary>
    public static string ChildCode(string sourceLineCode, string newLineCode, string childCode) =>
        childCode.StartsWith(sourceLineCode, StringComparison.OrdinalIgnoreCase) ? newLineCode + childCode[sourceLineCode.Length..] : $"{newLineCode}-{childCode}";

    /// <summary>
    /// The copy's codes are given, at most <see cref="MaxCode"/> characters, different from each other (ignoring case) and from every code in use at
    /// the site (<paramref name="taken"/>).
    /// </summary>
    public static IReadOnlyList<UserMessage> Problems(IReadOnlyList<LineCopyItem> items, IReadOnlySet<string> taken)
    {
        var problems = new List<UserMessage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var code = item.Code.Trim();
            if (code.Length is 0 or > MaxCode)
            {
                problems.Add(UserMessage.Of($"Give every piece of equipment a code of at most {MaxCode} characters."));
            }
            else if (!seen.Add(code))
            {
                problems.Add(UserMessage.Of($"{code} is given twice."));
            }
            else if (taken.Contains(code))
            {
                problems.Add(UserMessage.Of($"{code} is already used at the site."));
            }

            if (item.Name.Trim().Length is 0 or > 200)
            {
                problems.Add(UserMessage.Of($"Give {code} a name of at most 200 characters."));
            }
        }

        return problems;
    }
}
