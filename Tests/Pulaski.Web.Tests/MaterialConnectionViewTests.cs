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
}
