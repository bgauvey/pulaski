using Pulaski.Contracts.Materials;
using Pulaski.Web.Services.Materials;

namespace Pulaski.Web.Tests;

/// <summary>How a connection's check or import is worded (D-068).</summary>
public class MaterialConnectionViewTests
{
    [Fact]
    public void A_check_says_what_an_import_would_do_and_why_it_cant()
    {
        Assert.Equal("ERP is ready: 2 to create, 1 to update, 5 unchanged. Click Import.",
            MaterialConnectionView.Summary("ERP", new ImportMaterialsResponse { Created = 2, Updated = 1, Unchanged = 5 }));
        Assert.Equal("ERP is up to date: 5 unchanged.", MaterialConnectionView.Summary("ERP", new ImportMaterialsResponse { Unchanged = 5 }));
        Assert.StartsWith("ERP: 1 item has errors", MaterialConnectionView.Summary("ERP", new ImportMaterialsResponse { Errors = 1 }), StringComparison.Ordinal);
        Assert.Equal("ERP can't be imported: down.", MaterialConnectionView.Summary("ERP", new ImportMaterialsResponse { FileProblems = ["down."] }));
        Assert.StartsWith("Imported from ERP: 2 created", MaterialConnectionView.Summary("ERP", new ImportMaterialsResponse { Applied = true, Created = 2 }), StringComparison.Ordinal);
    }

    [Fact]
    public void Daily_times_are_read_as_typed_and_a_schedule_reads_as_words()
    {
        Assert.Equal([new TimeOnly(2, 0), new TimeOnly(14, 30)], MaterialConnectionView.ParseTimes("2:00, 14:30")!);
        Assert.Null(MaterialConnectionView.ParseTimes("2pm"));
        Assert.Empty(MaterialConnectionView.ParseTimes(" ")!);

        var c = new MaterialConnectionDto { Id = Guid.CreateVersion7(), Code = "HR", Name = "HR", Kind = "scim", EntitySet = "", ExternalSystem = "HR1" };
        Assert.Equal("On demand", MaterialConnectionView.Schedule(c));
        Assert.Equal("Every 6 hours", MaterialConnectionView.Schedule(c with { ScheduleEveryHours = 6 }));
        Assert.Equal("Daily at 02:00, 14:30 UTC", MaterialConnectionView.Schedule(c with { ScheduleDailyAt = [new(2, 0), new(14, 30)] }));
        Assert.Equal("Not imported (errors)", MaterialConnectionView.Outcome("not_applied"));
    }
}
