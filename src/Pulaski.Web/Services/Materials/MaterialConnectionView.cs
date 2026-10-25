using Pulaski.Contracts.Materials;

namespace Pulaski.Web.Services.Materials;

/// <summary>How the connections panel words a check or an import (D-068).</summary>
public static class MaterialConnectionView
{
    public static string Summary(string code, ImportMaterialsResponse r) =>
        r.FileProblems.Count > 0 ? $"{code} can't be imported: {string.Join(" ", r.FileProblems)}"
        : r.Applied ? $"Imported from {code}: {r.Created} created, {r.Updated} updated, {r.Unchanged} unchanged."
        : r.Errors > 0 ? $"{code}: {r.Errors} {(r.Errors == 1 ? "item has" : "items have")} errors, so nothing can be imported yet. Fix them at the source or in the field map, then check again."
        : r.Created + r.Updated == 0 ? $"{code} is up to date: {r.Unchanged} unchanged."
        : $"{code} is ready: {r.Created} to create, {r.Updated} to update, {r.Unchanged} unchanged. Click Import.";
}
