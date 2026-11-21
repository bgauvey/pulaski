using System.Globalization;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Api.WorkOrders;

/// <summary>
/// Where label templates are and where label software picks up its jobs (#75, D-080), from <c>Labels</c> in the API's configuration. Paths
/// live only here: printers in the database name a template set (a folder under <see cref="TemplateDirectory"/>) and a drop folder by name.
/// </summary>
public sealed class LabelPrintingOptions
{
    /// <summary>The folder of template sets, relative to the API's content root unless absolute. Pulaski ships <c>zpl</c> and <c>csv</c>.</summary>
    public string TemplateDirectory { get; set; } = "Labels/Templates";

    /// <summary>Drop folders by name, for printers that hand labels to other software (BarTender and the like) as files.</summary>
    public Dictionary<string, string> DropFolders { get; set; } = [];

    public int ConnectTimeoutSeconds { get; set; } = 5;

    /// <summary>How long BarTender may take to answer a print script (#168).</summary>
    public int BarTenderTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Credentials for BarTender web services by printer code (#168), never in the database: <c>Username</c> and <c>Password</c> for basic
    /// authentication, and any <c>Headers</c> to send (an API key, say).
    /// </summary>
    public Dictionary<string, BarTenderCredentials> BarTender { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BarTenderCredentials
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    public Dictionary<string, string> Headers { get; set; } = [];
}

/// <summary>
/// Renders labels from template files (#75): <c>{set}/{kind}.{format}.{ext}</c>, else <c>{set}/{kind}.{ext}</c>, where kind is <c>lot</c>,
/// <c>container</c> or <c>location</c> and format the site's (<c>gs1_128</c>, <c>gs1_datamatrix</c>). <c>{{Name}}</c> is replaced by the
/// label's value, escaped for the file's language (ZPL, XML, CSV, JSON); unknown names become empty.
/// </summary>
public sealed partial class LabelTemplates(IOptions<LabelPrintingOptions> options, IWebHostEnvironment environment)
{
    private static readonly HashSet<string> Raw = ["Gs1Data", "Gs1Zpl128", "Gs1ZplDataMatrix"];

    public string Root => Path.IsPathRooted(options.Value.TemplateDirectory)
        ? options.Value.TemplateDirectory
        : Path.Combine(environment.ContentRootPath, options.Value.TemplateDirectory);

    public IReadOnlyList<string> Sets() =>
        Directory.Exists(Root) ? [.. Directory.GetDirectories(Root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal)] : [];

    public IReadOnlyList<string> DropFolders() => [.. options.Value.DropFolders.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The label as the template set writes it, and the template's extension.</summary>
    public (string Text, string Extension) Render(string set, string kind, LabelContent label)
    {
        var folder = Path.Combine(Root, set);
        if (!SafeName().IsMatch(set) || !Directory.Exists(folder))
        {
            throw new DomainRuleException($"There is no label template set '{set}' in {options.Value.TemplateDirectory}.");
        }

        var template = Find(folder, $"{kind}.{label.Format}.") ?? Find(folder, $"{kind}.")
            ?? throw new DomainRuleException($"The template set '{set}' has no template for a {kind} label.");
        var extension = Path.GetExtension(template).TrimStart('.').ToLowerInvariant();
        var values = Values(label);
        var text = Placeholder().Replace(File.ReadAllText(template), m =>
        {
            var name = m.Groups[1].Value;
            var value = values.GetValueOrDefault(name) ?? "";
            return Raw.Contains(name) ? value : Escape(value, extension);
        });
        return (text, extension);
    }

    /// <summary>Every value a template can use.</summary>
    public static IReadOnlyDictionary<string, string?> Values(LabelContent label)
    {
        var values = new Dictionary<string, string?>(label.Fields, StringComparer.Ordinal)
        {
            ["Kind"] = label.Kind,
            ["Title"] = label.Title,
            ["Code"] = label.Barcode,
            ["Format"] = label.Format,
        };
        for (var i = 0; i < 10; i++)
        {
            values[$"Line{i + 1}"] = i < label.Lines.Count ? label.Lines[i] : "";
        }

        if (label.Gs1 is { } gs1)
        {
            values["Gs1Hri"] = Gs1.HumanReadable(gs1);
            values["Gs1Data"] = Gs1.Data(gs1, Gs1.GroupSeparator.ToString());

            // ZPL: Code 128 starts with >; (subset C) and >8 (FNC1), which also separates; DataMatrix (^BX with _ as escape) uses _1.
            values["Gs1Zpl128"] = ">;>8" + Gs1.Data(gs1, ">8");
            values["Gs1ZplDataMatrix"] = "_1" + Gs1.Data(gs1, "_1");
        }

        return values;
    }

    private static string? Find(string folder, string prefix) =>
        Directory.GetFiles(folder).Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.Ordinal) && Path.GetFileName(f).Count(c => c == '.') == prefix.Count(c => c == '.'))
            .Order(StringComparer.Ordinal).FirstOrDefault();

    private static string Escape(string value, string extension) => extension switch
    {
        // ^ and ~ start ZPL commands; a value never may.
        "zpl" or "prn" => value.Replace('^', ' ').Replace('~', ' '),
        "xml" or "btxml" => SecurityElement.Escape(value) ?? "",
        "csv" => value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value,
        "json" => JsonSerializer.Serialize(value)[1..^1],
        _ => value,
    };

    [GeneratedRegex(@"\{\{([A-Za-z0-9]+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,50}$")]
    private static partial Regex SafeName();
}

/// <summary>
/// Sends rendered labels to a printer (#75): over raw TCP (port 9100 is the usual), all in one connection, or as one file each into a
/// configured drop folder, written under a temporary name and then renamed so label software never picks up half a file; or, for BarTender
/// (#168), as one BTXML script per label posted to its web service, whose answer is read for errors.
/// </summary>
public sealed class LabelPrinting(LabelTemplates templates, IOptions<LabelPrintingOptions> options, IHttpClientFactory http, TimeProvider time)
{
    public const int MaxLabels = 200;

    /// <summary>The HTTP client BarTender scripts are posted with.</summary>
    public const string BarTenderClient = "bartender";

    /// <summary>
    /// Prints <paramref name="copies"/> of each label. For BarTender (#168) the .btw document is the one for <paramref name="operation"/>: the
    /// kind unless given, or the kind and what printed it (<c>lot.produced</c>).
    /// </summary>
    public async Task<PrintLabelsResponse> PrintAsync(
        LabelPrinterDto printer, string kind, IReadOnlyList<LabelContent> labels, int copies, CancellationToken cancellationToken, string? operation = null)
    {
        if (!printer.IsActive)
        {
            throw new DomainRuleException($"{printer.Code} is switched off.");
        }

        if (printer.Method == PrintMethods.BarTender)
        {
            await BarTenderAsync(printer, operation ?? kind, labels, copies, cancellationToken);
            return new PrintLabelsResponse(labels.Count * copies, printer.Code, printer.Method);
        }

        var set = printer.TemplateSet ?? throw new DomainRuleException($"{printer.Code} has no template set.");
        var rendered = labels.SelectMany(l => Enumerable.Repeat(templates.Render(set, kind, l), copies)).ToList();
        if (printer.Method == PrintMethods.RawTcp)
        {
            await SendAsync(printer, string.Concat(rendered.Select(r => r.Text)), cancellationToken);
        }
        else
        {
            await DropAsync(printer, kind, rendered, cancellationToken);
        }

        return new PrintLabelsResponse(rendered.Count, printer.Code, printer.Method);
    }

    private async Task BarTenderAsync(LabelPrinterDto printer, string operation, IReadOnlyList<LabelContent> labels, int copies, CancellationToken cancellationToken)
    {
        var btw = Btxml.BtwFor(printer.BtwFiles, operation)
            ?? throw new DomainRuleException($"{printer.Code} has no BarTender document for {operation} labels, and no default one.");
        var endpoint = printer.EndpointUrl ?? throw new DomainRuleException($"{printer.Code} has no BarTender address.");
        var template = printer.BtxmlTemplate ?? Btxml.StandardTemplate;
        var credentials = options.Value.BarTender.GetValueOrDefault(printer.Code);
        var client = http.CreateClient(BarTenderClient);
        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        for (var i = 0; i < labels.Count; i++)
        {
            var script = Btxml.Render(template, LabelTemplates.Values(labels[i]), btw, printer.BartenderPrinter, copies, $"Pulaski-{operation}-{stamp}-{i + 1:D3}");
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(script, Encoding.UTF8, "text/xml") };
            if (credentials is { Username: { Length: > 0 } user })
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{credentials.Password}")));
            }

            foreach (var (name, value) in credentials?.Headers ?? [])
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.BarTenderTimeoutSeconds));
            string answer;
            System.Net.HttpStatusCode status;
            try
            {
                using var response = await client.SendAsync(request, timeout.Token);
                (status, answer) = (response.StatusCode, await response.Content.ReadAsStringAsync(timeout.Token));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new DomainRuleException(
                    $"Couldn't send the labels to {printer.Code} through BarTender: {(ex is OperationCanceledException ? "it didn't answer in time" : ex.Message)}.");
            }

            var done = i == 0 ? "" : $" ({i} of {labels.Count} labels went before it)";
            if ((int)status is < 200 or > 299)
            {
                throw new DomainRuleException($"BarTender refused the label for {printer.Code}: {(int)status} {status}{done}.");
            }

            if (Btxml.ErrorIn(answer) is { } error)
            {
                throw new DomainRuleException($"BarTender couldn't print the label on {printer.Code}: {error}{done}");
            }
        }
    }

    private async Task SendAsync(LabelPrinterDto printer, string text, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.ConnectTimeoutSeconds));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(printer.Host!, printer.Port!.Value, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(text), timeout.Token);
            await stream.FlushAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DomainRuleException($"Couldn't send the labels to {printer.Code} at {printer.Host}:{printer.Port}: {(ex is OperationCanceledException ? "it didn't answer in time" : ex.Message)}.");
        }
    }

    private async Task DropAsync(LabelPrinterDto printer, string kind, List<(string Text, string Extension)> rendered, CancellationToken cancellationToken)
    {
        if (printer.DropFolder is not { } name || !options.Value.DropFolders.TryGetValue(name, out var folder) || !Directory.Exists(folder))
        {
            throw new DomainRuleException($"{printer.Code}'s drop folder '{printer.DropFolder}' isn't configured in Labels:DropFolders, or doesn't exist.");
        }

        var stamp = time.GetUtcNow().ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
        for (var i = 0; i < rendered.Count; i++)
        {
            var path = Path.Combine(folder, $"{kind}-{stamp}-{i + 1:D3}-{Guid.NewGuid():N}.{rendered[i].Extension}");
            await File.WriteAllTextAsync(path + ".tmp", rendered[i].Text, cancellationToken);
            File.Move(path + ".tmp", path);
        }
    }
}
