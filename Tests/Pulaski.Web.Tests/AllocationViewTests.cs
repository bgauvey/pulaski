using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Inventory;

namespace Pulaski.Web.Tests;

/// <summary>How allocation changes are summed up (D-064).</summary>
public class AllocationViewTests
{
    [Fact]
    public void A_change_lists_what_it_did_and_across_how_many_jobs()
    {
        Assert.Equal("2 allocated, 1 trimmed.", AllocationView.Summary(new AllocationChangeResponse { Allocated = 2, Trimmed = 1, Jobs = 1 }));
        Assert.Equal("1 released, across 3 jobs.", AllocationView.Summary(new AllocationChangeResponse { Released = 1, Jobs = 3 }));
        Assert.Equal("Nothing to change.", AllocationView.Summary(new AllocationChangeResponse { Jobs = 1 }));
        Assert.Equal("Nothing to change across 4 jobs.", AllocationView.Summary(new AllocationChangeResponse { Jobs = 4 }));
    }
}
