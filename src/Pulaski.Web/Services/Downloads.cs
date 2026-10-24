using Microsoft.JSInterop;

namespace Pulaski.Web.Services;

/// <summary>Hands the browser a file to save (exports).</summary>
public sealed class Downloads(IJSRuntime js)
{
    public ValueTask CsvAsync(string fileName, string csv) =>
        js.InvokeVoidAsync("pulaski.ui.download", fileName, "﻿" + csv, "text/csv;charset=utf-8");

    public ValueTask FileAsync(string fileName, byte[] content, string contentType) =>
        js.InvokeVoidAsync("pulaski.ui.downloadBytes", fileName, content, contentType);

    public ValueTask PdfAsync(string fileName, byte[] pdf) =>
        js.InvokeVoidAsync("pulaski.ui.downloadBytes", fileName, pdf, "application/pdf");
}
