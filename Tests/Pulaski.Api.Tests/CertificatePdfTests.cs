using Pulaski.Api.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Quality;

namespace Pulaski.Api.Tests;

/// <summary>A certificate of analysis draws as a PDF, warnings and all (#137).</summary>
public class CertificatePdfTests
{
    [Fact]
    public void A_certificate_renders_as_a_pdf_whatever_it_found()
    {
        var certificate = new CertificateDto
        {
            LotId = Guid.CreateVersion7(),
            LotNumber = "L-1001",
            MaterialCode = "PRF-1",
            MaterialName = "Profile",
            Quantity = 100,
            UomCode = "m",
            LotStatus = "on_hold",
            SiteCode = "PLANT",
            SiteName = "Plant",
            SiteTimeZone = "America/Chicago",
            SpecVersion = 2,
            IssuedAt = DateTimeOffset.UnixEpoch,
            Reference = "PO-77",
            Lines =
            [
                new() { CharacteristicCode = "WALL", CharacteristicName = "Wall", Requirement = "3.2 – 3.4", Measured = true, Results = 2, Mean = 3.3m, Min = 3.28m, Max = 3.34m, Outcome = "pass", Decimals = 2 },
                new() { CharacteristicCode = "SURF", CharacteristicName = "Surface", Requirement = "at most 1 defect", Results = 0, Outcome = "no_result" },
            ],
            Warnings = [new MessageDto("SURF has no result.")],
        };
        var pdf = new CertificatePdfRenderer().Render(certificate);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 1000);
    }
}
