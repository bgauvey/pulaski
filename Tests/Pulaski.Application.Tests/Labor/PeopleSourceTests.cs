using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;

namespace Pulaski.Application.Tests.Labor;

/// <summary>Reading people from CSV and SCIM 2.0 files into import rows (D-054).</summary>
public class PeopleSourceTests
{
    [Fact]
    public void Csv_columns_are_named_in_any_order_case_and_spelling()
    {
        var source = PeopleCsv.Parse("Full Name,Employee No,Crew Code,active\r\n\"Ng, Ana\",1001,a,no\r\n");

        Assert.Empty(source.Problems);
        var row = Assert.Single(source.Rows);
        Assert.Equal((2, "1001", "Ng, Ana", "a", false), (row.Line, row.EmployeeNumber, row.DisplayName, row.CrewCode!.Value, row.IsActive!.Value));
        Assert.Null(row.Username);
        Assert.Null(row.ExternalSystem);
        Assert.Equal(PeopleCsvFormat.Header, PeopleCsv.Template);
    }

    [Fact]
    public void A_csv_external_system_given_on_import_is_the_system_of_rows_with_a_key()
    {
        var source = PeopleCsv.Parse("employee_number,name,external_system,external_key\n1,A,,W-1\n2,B,ERP1,7\n3,C,,\n", " HR1 ");

        Assert.Equal([("HR1", "W-1"), ("ERP1", "7"), (null, null)], source.Rows.Select(r => (r.ExternalSystem, r.ExternalKey)));
    }

    [Fact]
    public void Csv_problems_are_reported_not_thrown()
    {
        Assert.Contains("missing", Assert.Single(PeopleCsv.Parse("employee_number\n1").Problems), StringComparison.Ordinal);
        Assert.Contains("Unknown column 'shift'", PeopleCsv.Parse("employee_number,name,shift\n").Problems[0], StringComparison.Ordinal);
        Assert.Equal(["The file is empty."], PeopleCsv.Parse("").Problems);
        Assert.Contains("isn't yes or no", Assert.Single(Assert.Single(PeopleCsv.Parse("employee_number,name,active\n1,A,maybe").Rows).Problems), StringComparison.Ordinal);
    }

    private const string List = """
        {
          "schemas": ["urn:ietf:params:scim:api:messages:2.0:ListResponse"],
          "totalResults": 2,
          "Resources": [
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"],
              "id": "2819c223",
              "externalId": "W-1001",
              "userName": "ana.ng",
              "name": { "givenName": "Ana", "familyName": "Ng" },
              "active": true,
              "meta": { "version": "W/\"3694e05e\"" },
              "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "employeeNumber": "1001", "department": "Extrusion" }
            },
            {
              "SCHEMAS": ["urn:ietf:params:scim:schemas:core:2.0:User"],
              "ID": "77",
              "DisplayName": "Bo Lind",
              "Active": false,
              "urn:ietf:params:scim:schemas:extension:enterprise:2.0:user": { "EmployeeNumber": 1002 }
            }
          ]
        }
        """;

    [Fact]
    public void Scim_users_become_rows_keyed_by_external_id_else_id()
    {
        var source = PeopleScim.Parse(List, "HR1");

        Assert.Empty(source.Problems);
        Assert.Equal(["1001", "1002"], source.Rows.Select(r => r.EmployeeNumber));
        Assert.Equal(["Ana Ng", "Bo Lind"], source.Rows.Select(r => r.DisplayName));
        Assert.Equal([true, false], source.Rows.Select(r => r.IsActive!.Value));
        Assert.Equal([("HR1", "W-1001"), ("HR1", "77")], source.Rows.Select(r => (r.ExternalSystem, r.ExternalKey)));
        Assert.Equal(("W/\"3694e05e\"", "hr", 2), (source.Rows[0].ExternalVersion, source.Rows[0].Origin, source.Rows[1].Line));

        // SCIM says nothing about Pulaski's crew or sign-in, so they are kept.
        Assert.Null(source.Rows[0].CrewCode);
        Assert.Null(source.Rows[0].Username);
    }

    [Fact]
    public void Without_an_external_system_scim_users_are_pulaski_people()
    {
        var user = """{ "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"], "id": "1", "userName": "x", "displayName": "X", "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "employeeNumber": "9" } }""";

        var single = Assert.Single(PeopleScim.Parse(user, null).Rows);
        Assert.Equal(("9", null, null, null), (single.EmployeeNumber, single.ExternalSystem, single.Origin, single.IsActive));
        Assert.Single(PeopleScim.Parse($"[{user}]", null).Rows);
    }

    [Fact]
    public void Scim_problems_are_kept_on_the_user_or_the_file()
    {
        var rows = PeopleScim.Parse("""[{ "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"], "displayName": "Ops" }, 3]""", null).Rows;
        Assert.Equal(2, rows[0].Problems.Count);
        Assert.Contains("employeeNumber", rows[0].Problems[1], StringComparison.Ordinal);
        Assert.Contains("isn't a SCIM user", Assert.Single(rows[1].Problems), StringComparison.Ordinal);

        Assert.StartsWith("The file isn't valid JSON", Assert.Single(PeopleScim.Parse("{", null).Problems), StringComparison.Ordinal);
        Assert.Contains("no SCIM users", Assert.Single(PeopleScim.Parse("""{ "Resources": [] }""", null).Problems), StringComparison.Ordinal);
        Assert.Equal(["The file is empty."], PeopleScim.Parse(" ", null).Problems);
    }
}
