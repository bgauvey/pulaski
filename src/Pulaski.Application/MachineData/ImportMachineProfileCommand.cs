using Pulaski.Application.Abstractions;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.MachineData;

/// <summary>
/// Loads a machine's definitions into its profile (D-050), all or nothing. Each kind of entry in the rows replaces that list:
/// the file's entries are created or updated, and active entries of that kind it leaves out are switched off. A kind the
/// rows don't mention is left as it is, so a file of stop reasons keeps the states.
/// </summary>
public sealed record ImportMachineProfileCommand(Guid ProfileId, long ExpectedVersion, ProfileImportSource Rows, bool DryRun) : ICommand<ProfileImportResponse>;

internal sealed class ImportMachineProfileHandler(IUnitOfWork unitOfWork, IMachineDataRepository machines, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<ImportMachineProfileCommand, ProfileImportResponse>
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Unchanged = "unchanged";
    public const string SwitchOff = "switch_off";
    public const string Error = "error";

    public async Task<ProfileImportResponse> HandleAsync(ImportMachineProfileCommand command, CancellationToken cancellationToken)
    {
        if (command.Rows.Problems.Count > 0)
        {
            return new ProfileImportResponse { FileProblems = command.Rows.Problems, Errors = command.Rows.Problems.Count, Version = command.ExpectedVersion };
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var profile = await ProfileRules.LockAsync(machines, command.ProfileId, command.ExpectedVersion, cancellationToken);
        var states = (await machines.ListStatesAsync(profile.Id, cancellationToken)).ToDictionary(s => s.RawValue);
        var codes = (await machines.ListCodesAsync(profile.Id, cancellationToken)).ToDictionary(c => (c.Kind, c.RawValue));
        var reasons = (await machines.ListUtilizationReasonsAsync(profile.SiteId, cancellationToken)).ToDictionary(r => r.Code, StringComparer.Ordinal);

        var results = new List<ProfileImportRowDto>();
        var seen = new HashSet<(string, int)>();
        var stateWrites = new List<ProfileStateRecord>();
        var codeWrites = new List<ProfileCodeRecord>();
        foreach (var row in command.Rows.Rows)
        {
            var problem = Problem(row, seen, reasons);
            if (problem is not null)
            {
                results.Add(Result(row.Line, row.Kind, row.RawValue, Error, problem));
                continue;
            }

            var reasonId = row.Reason is { } code ? reasons[code].Id : (Guid?)null;
            if (row.Kind == MachineProfileCsv.State)
            {
                var after = new ProfileStateRecord(profile.Id, row.RawValue, row.Name, row.PackMlState!.Value, reasonId, IsActive: true);
                var action = Compare(states.GetValueOrDefault(row.RawValue), after);
                results.Add(Result(row.Line, row.Kind, row.RawValue, action, null));
                if (action != Unchanged)
                {
                    stateWrites.Add(after);
                }
            }
            else
            {
                var after = new ProfileCodeRecord(profile.Id, row.Kind, row.RawValue, row.Name, row.Category, reasonId, IsActive: true);
                var action = Compare(codes.GetValueOrDefault((row.Kind, row.RawValue)), after);
                results.Add(Result(row.Line, row.Kind, row.RawValue, action, null));
                if (action != Unchanged)
                {
                    codeWrites.Add(after);
                }
            }
        }

        // What the file replaces: each kind it mentions. Entries of that kind it leaves out are switched off.
        var kinds = command.Rows.Rows.Select(r => r.Kind).Where(MachineProfileCsv.Kinds.Contains).ToHashSet();
        if (kinds.Contains(MachineProfileCsv.State))
        {
            foreach (var left in states.Values.Where(s => s.IsActive && !seen.Contains((MachineProfileCsv.State, s.RawValue))).OrderBy(s => s.RawValue))
            {
                stateWrites.Add(left with { IsActive = false });
                results.Add(Result(0, MachineProfileCsv.State, left.RawValue, SwitchOff, $"{left.Name} isn't in the file."));
            }
        }

        foreach (var left in codes.Values.Where(c => kinds.Contains(c.Kind) && c.IsActive && !seen.Contains((c.Kind, c.RawValue))).OrderBy(c => c.Kind).ThenBy(c => c.RawValue))
        {
            codeWrites.Add(left with { IsActive = false });
            results.Add(Result(0, left.Kind, left.RawValue, SwitchOff, $"{left.Message} isn't in the file."));
        }

        var errors = results.Count(r => r.Action == Error);
        var version = command.ExpectedVersion;
        var changed = stateWrites.Count + codeWrites.Count > 0;
        if (errors == 0 && !command.DryRun && changed)
        {
            foreach (var state in stateWrites)
            {
                await machines.UpsertStateAsync(state, cancellationToken);
            }

            foreach (var code in codeWrites)
            {
                await machines.UpsertCodeAsync(code, cancellationToken);
            }

            var counts = new
            {
                Created = results.Count(r => r.Action == Create),
                Updated = results.Count(r => r.Action == Update),
                SwitchedOff = results.Count(r => r.Action == SwitchOff),
            };
            var envelope = events.Create(EventTypes.MachineProfileImported, "machine_profile", profile.Id,
                new { profile.Code, counts.Created, counts.Updated, counts.SwitchedOff, States = stateWrites, Codes = codeWrites }, "CSV import");
            version = await machines.UpdateProfileAsync(profile, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return new ProfileImportResponse
        {
            Applied = errors == 0 && !command.DryRun,
            Created = results.Count(r => r.Action == Create),
            Updated = results.Count(r => r.Action == Update),
            Unchanged = results.Count(r => r.Action == Unchanged),
            SwitchedOff = results.Count(r => r.Action == SwitchOff),
            Errors = errors,
            Rows = results,
            Version = version,
        };
    }

    private static string? Problem(ProfileImportRow row, HashSet<(string, int)> seen, Dictionary<string, UtilizationReasonRecord> reasons)
    {
        if (row.Problems.Count > 0)
        {
            return string.Join(" ", row.Problems);
        }

        // Counted before the other checks, so a later row with the same value is caught even when this one fails.
        if (!seen.Add((row.Kind, row.RawValue)))
        {
            return $"{Label(row.Kind)} {row.RawValue} appears earlier in the file.";
        }

        var limit = row.Kind == MachineProfileCsv.State ? ProfileRules.NameLength : ProfileRules.MessageLength;
        if (row.Name.Length == 0 || row.Name.Length > limit)
        {
            return $"name: required, at most {limit} characters.";
        }

        if (row.Category is { Length: > ProfileRules.CategoryLength })
        {
            return $"category: at most {ProfileRules.CategoryLength} characters.";
        }

        return row.Reason is { } code && !reasons.ContainsKey(code)
            ? $"reason: {code} isn't an active utilization reason of the site."
            : null;
    }

    private static string Compare<T>(T? before, T after)
        where T : class => before is null ? Create : before.Equals(after) ? Unchanged : Update;

    private static ProfileImportRowDto Result(int line, string kind, int rawValue, string action, string? message) =>
        new() { Line = line, Kind = kind, RawValue = rawValue, Action = action, Message = message };

    private static string Label(string kind) => kind switch
    {
        MachineProfileCsv.State => "State",
        StopCodeKinds.StopReason => "Stop reason",
        StopCodeKinds.Alarm => "Alarm",
        _ => "Warning",
    };
}
