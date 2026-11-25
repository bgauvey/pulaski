using Pulaski.Contracts.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>Importing ERP work orders from a file, in words (D-102).</summary>
public class ErpOrderImportViewTests
{
    private static ErpOrderResultDto Result(string action) => new() { ExternalKey = action, OrderNumber = action, Action = action };

    [Fact]
    public void Import_waits_for_the_erp_a_file_and_a_check_that_changes_something()
    {
        var ready = new ImportWorkOrdersResponse { DryRun = true, Results = [Result("created"), Result("refused")] };

        Assert.Equal("Name the ERP the orders come from first.", ErpOrderImportView.Why(Text.English, ready, " "));
        Assert.StartsWith("Pick a file first", ErpOrderImportView.Why(Text.English, null, "SAP"), StringComparison.Ordinal);
        Assert.Null(ErpOrderImportView.Why(Text.English, ready, "SAP"));
        Assert.Equal("The file changes nothing.", ErpOrderImportView.Why(Text.English, new ImportWorkOrdersResponse { DryRun = true, Results = [Result("unchanged"), Result("refused")] }, "SAP"));
        Assert.StartsWith("Fix what the check found", ErpOrderImportView.Why(Text.English, new ImportWorkOrdersResponse { DryRun = true, FileProblems = [new MessageDto("No orders.")] }, "SAP"), StringComparison.Ordinal);
        Assert.StartsWith("Imported.", ErpOrderImportView.Why(Text.English, ready with { DryRun = false }, "SAP"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_counts_each_result_as_a_check_or_as_done()
    {
        var results = new[] { Result("created"), Result("created"), Result("pending"), Result("refused") };

        Assert.Equal("Ready: 2 to create, 1 to wait for a planner, 1 refused (not imported).",
            ErpOrderImportView.Summary(Text.English, new ImportWorkOrdersResponse { DryRun = true, Results = results }));
        Assert.Equal("Imported: 2 created, 1 waiting for a planner, 1 refused.",
            ErpOrderImportView.Summary(Text.English, new ImportWorkOrdersResponse { DryRun = false, Results = results }));
        Assert.Equal("The file is empty.", ErpOrderImportView.Summary(Text.English, new ImportWorkOrdersResponse { DryRun = true, FileProblems = [new MessageDto("The file is empty.")] }));
    }
}
