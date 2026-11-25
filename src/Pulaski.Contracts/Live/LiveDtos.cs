namespace Pulaski.Contracts.Live;

/// <summary>
/// Something changed (#143, D-117): the event's type (or <c>machine</c> for ingest, <c>resync</c> when everything may have), what kind of thing
/// (the event's subject type), which one, and its site when known. No data: a screen that cares reads it again through the API.
/// </summary>
public sealed record LiveChangeDto(string Type, string Kind, Guid? Id, Guid? SiteId);
