using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

public class SequenceRankTests
{
    [Fact]
    public void Empty_line_starts_at_one() => Assert.Equal(1m, SequenceRank.Between(null, null));

    [Fact]
    public void End_of_line_is_the_next_whole_number() => Assert.Equal(4m, SequenceRank.Between(3.25m, null));

    [Fact]
    public void Start_of_line_is_the_previous_whole_number() => Assert.Equal(0m, SequenceRank.Between(null, 0.5m));

    [Fact]
    public void Between_two_jobs_is_the_midpoint() => Assert.Equal(1.5m, SequenceRank.Between(1m, 2m));

    [Fact]
    public void No_room_at_ten_decimal_places_returns_null() =>
        Assert.Null(SequenceRank.Between(1.0000000001m, 1.0000000002m));

    [Fact]
    public void Equal_neighbours_leave_no_room() => Assert.Null(SequenceRank.Between(2m, 2m));

    [Fact]
    public void Repeated_halving_runs_out_after_about_thirty_moves()
    {
        decimal before = 1m, after = 2m;
        var moves = 0;
        while (SequenceRank.Between(before, after) is { } mid)
        {
            after = mid;
            moves++;
        }

        Assert.InRange(moves, 30, 34);
    }
}
