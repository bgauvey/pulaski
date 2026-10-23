using Pulaski.Domain.Labor;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Breaks come out of worked time without touching sessions (D-047); qualifications warn what is missing (D-048).</summary>
public class BreakAndQualificationRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Person = Guid.CreateVersion7();
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();

    private static LaborSpan Session(double from, double? to, decimal pct = 100, Guid? line = null) =>
        new(Guid.CreateVersion7(), Person, line ?? Line, null, LaborTypes.Direct, T0.AddHours(from), to is { } t ? T0.AddHours(t) : null, pct);

    private static BreakSpan Break(double from, double? to, bool paid = false) => new(Person, T0.AddHours(from), to is { } t ? T0.AddHours(t) : null, paid);

    [Fact]
    public void A_break_comes_out_of_the_session_and_its_run_shares()
    {
        var session = Session(0, 8);
        RunSpan[] runs = [new(Guid.CreateVersion7(), Guid.CreateVersion7(), Line, T0.AddHours(2), T0.AddHours(6))];

        var shares = LaborRules.Attribute([session], runs, T0.AddHours(9), [Break(3, 3.5), Break(7, 7.25, paid: true)]);

        Assert.Equal(3.5m, shares.Single(s => s.RunId is not null).Hours);
        Assert.Equal(3.75m, shares.Single(s => s.RunId is null).Hours);
        Assert.Equal(7.25m, LaborRules.WorkedHours(session.Start, session.End, 100, [Break(3, 3.5), Break(7, 7.25)], T0.AddHours(9)));
    }

    [Fact]
    public void A_break_counts_once_however_many_sessions_it_overlaps_and_each_loses_its_share()
    {
        // Half on each of two lines; a 30-minute break takes 15 minutes off each, and is 30 minutes of break.
        var a = Session(0, 4, 50);
        var b = Session(0, 4, 50, Other);
        var breaks = new[] { Break(1, 1.5) };

        Assert.Equal(1.75m, LaborRules.WorkedHours(a.Start, a.End, a.AllocationPct, breaks, T0.AddHours(5)));
        Assert.Equal((0m, 0.5m), LaborRules.BreakHours(breaks, [a, b], T0.AddHours(5)));
    }

    [Fact]
    public void Break_time_counts_only_while_the_person_was_on_and_open_breaks_run_until_now()
    {
        var on = Session(0, 2);

        Assert.Equal((0.5m, 0m), LaborRules.BreakHours([Break(1.5, 3, paid: true)], [on], T0.AddHours(5)));
        Assert.Equal(1.5m, LaborRules.WorkedHours(T0, null, 100, [Break(1.5, null)], T0.AddHours(2)));
        Assert.NotNull(LaborRules.BreakProblem(T0, T0.AddHours(5), T0.AddHours(6)));
        Assert.NotNull(LaborRules.BreakProblem(T0, T0, T0.AddHours(1)));
        Assert.Null(LaborRules.BreakProblem(T0, null, T0.AddHours(1)));
    }

    [Fact]
    public void Missing_qualifications_say_why_and_a_valid_renewal_covers_an_expired_grant()
    {
        var fork = new RequiredQualification(Guid.CreateVersion7(), "FORK", "Forklift");
        var crane = new RequiredQualification(Guid.CreateVersion7(), "CRANE", "Crane");
        var lift = new RequiredQualification(Guid.CreateVersion7(), "LIFT", "Lifting");
        var day = new DateOnly(2026, 10, 19);
        QualificationGrant[] grants =
        [
            new(fork.QualificationId, new DateOnly(2025, 1, 1), new DateOnly(2026, 10, 1), null),
            new(crane.QualificationId, new DateOnly(2026, 1, 1), null, T0.AddDays(-1)),
            new(lift.QualificationId, new DateOnly(2026, 11, 1), null, null),
        ];

        var missing = QualificationRules.Missing([fork, crane, lift, fork], grants, day, T0);
        Assert.Equal(["expired 2026-10-01", "revoked", "valid only from 2026-11-01"], missing.Select(m => m.Why));
        Assert.Contains("FORK Forklift (expired 2026-10-01)", QualificationRules.Describe("Lee", "EXT-01", missing), StringComparison.Ordinal);

        var renewed = QualificationRules.Missing([fork], [.. grants, new(fork.QualificationId, new DateOnly(2026, 10, 2), new DateOnly(2027, 10, 1), null)], day, T0);
        Assert.Empty(renewed);
        Assert.Equal(new DateOnly(2027, 10, 18), QualificationRules.DefaultExpiry(day, 12));
        Assert.Null(QualificationRules.DefaultExpiry(day, null));
        Assert.Equal(GrantStatus.Valid, QualificationRules.Status(new(fork.QualificationId, day, day, null), day, T0));
    }
}
