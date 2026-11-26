using Pulaski.Domain.Security;

namespace Pulaski.Domain.Setup;

/// <summary>
/// A step in setting a site up (#139, D-126), in the order the user guide gives: what it is, why, the screen it is done on and the view that
/// opens it. Required steps take a site to its first job; the others can be marked not needed there.
/// </summary>
public sealed record SetupStep(string Key, string Title, string Why, string Href, string Permission, bool Required);

/// <summary>The steps, in order (#139). Titles and reasons are terms.</summary>
public static class SetupSteps
{
    public const string Site = "site";
    public const string Lines = "lines";
    public const string Materials = "materials";
    public const string Units = "units";
    public const string Capabilities = "capabilities";
    public const string Shifts = "shifts";
    public const string People = "people";
    public const string Processes = "processes";
    public const string Boms = "boms";
    public const string Locations = "locations";
    public const string Reasons = "reasons";
    public const string MachineProfiles = "machine_profiles";
    public const string EdgeNodes = "edge_nodes";
    public const string Users = "users";
    public const string WorkOrders = "work_orders";
    public const string FirstJob = "first_job";

    public static IReadOnlyList<SetupStep> All { get; } =
    [
        new(Site, Term.Mark("A site, with its time zone"), Term.Mark("Everything else belongs to a site."), "equipment", Permissions.EquipmentView, true),
        new(Lines, Term.Mark("Lines that run jobs"), Term.Mark("Jobs are planned on lines and departments."), "equipment", Permissions.EquipmentView, true),
        new(Materials, Term.Mark("Materials"), Term.Mark("Orders, processes and BOMs need materials."), "materials", Permissions.MaterialsView, true),
        new(Units, Term.Mark("Unit conversions for materials"), Term.Mark("So orders, BOMs and rates can use any unit that converts."), "units", Permissions.UnitsView, false),
        new(Capabilities, Term.Mark("Capabilities: which material runs on which line, and how fast"), Term.Mark("Without one, a line can't take a material's job."), "materials", Permissions.MaterialsView, true),
        new(Shifts, Term.Mark("Shift schedules"), Term.Mark("Without one, lines are taken to run around the clock."), "shifts", Permissions.ShiftsView, false),
        new(People, Term.Mark("People and crews"), Term.Mark("So labor can be recorded and crews checked onto lines."), "people", Permissions.People, false),
        new(Processes, Term.Mark("A released process"), Term.Mark("Every work order is made from a process."), "processes", Permissions.ProcessesView, true),
        new(Boms, Term.Mark("BOMs for what you make"), Term.Mark("Without a BOM, an order plans no components."), "boms", Permissions.ProcessesView, false),
        new(Locations, Term.Mark("Inventory locations"), Term.Mark("Lots have to be somewhere: stores, staging areas, feed points."), "equipment", Permissions.EquipmentView, true),
        new(Reasons, Term.Mark("Reason codes"), Term.Mark("Holds, adjustments, scrap and stops need reasons."), "reasons", Permissions.ReasonsView, false),
        new(MachineProfiles, Term.Mark("Machine profiles"), Term.Mark("So machines' state values and stop codes mean something."), "machine-profiles", Permissions.EquipmentView, false),
        new(EdgeNodes, Term.Mark("Edge nodes collecting machine data"), Term.Mark("So Pulaski hears machine state and counts."), "edge-nodes", Permissions.EquipmentView, false),
        new(Users, Term.Mark("Users with roles at the site"), Term.Mark("So the people who plan, run and check the work can sign in."), "users", Permissions.Security, false),
        new(WorkOrders, Term.Mark("A work order"), Term.Mark("Now you can plan."), "work-orders", Permissions.WorkOrdersView, true),
        new(FirstJob, Term.Mark("The first job started"), Term.Mark("The site is running."), "floor", Permissions.FloorView, true),
    ];

    public static SetupStep? Find(string? key) => All.FirstOrDefault(s => s.Key == key);
}

/// <summary>What is there at a site, counted, for the checklist (#139).</summary>
public sealed record SetupFacts(IReadOnlyDictionary<string, int> Counts)
{
    public int Count(string step) => Counts.GetValueOrDefault(step);
}

public static class SetupStatus
{
    public const string Done = "done";
    public const string ToDo = "to_do";
    public const string NotNeeded = "not_needed";
}

/// <summary>A step as the checklist shows it: done (with how many), to do, or not needed here.</summary>
public sealed record SetupStepState(SetupStep Step, string Status, int Count);

/// <summary>Works the checklist out from what is there and what was marked not needed (#139). Pure.</summary>
public static class SetupChecklist
{
    public static IReadOnlyList<SetupStepState> Evaluate(SetupFacts facts, IReadOnlySet<string> notNeeded) =>
        [.. SetupSteps.All.Select(step => facts.Count(step.Key) is var count and > 0
            ? new SetupStepState(step, SetupStatus.Done, count)
            : new SetupStepState(step, !step.Required && notNeeded.Contains(step.Key) ? SetupStatus.NotNeeded : SetupStatus.ToDo, 0))];

    /// <summary>Ready for the first job and beyond: every required step done.</summary>
    public static bool Ready(IReadOnlyList<SetupStepState> steps) => steps.Where(s => s.Step.Required).All(s => s.Status == SetupStatus.Done);

    /// <summary>Whether <paramref name="key"/> may be marked not needed: an optional step.</summary>
    /// <exception cref="DomainRuleException">No such step, or a required one.</exception>
    public static SetupStep Skippable(string? key) =>
        SetupSteps.Find(key) is not { } step ? throw new DomainRuleException($"'{key}' isn't a setup step.")
        : step.Required ? throw new DomainRuleException($"{UserMessage.Of(step.Title)} is needed for the first job, so it can't be skipped.")
        : step;
}
