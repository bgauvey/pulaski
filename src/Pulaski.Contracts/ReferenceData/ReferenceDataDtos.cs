namespace Pulaski.Contracts.ReferenceData;

public sealed record UomDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string Dimension { get; init; }
}

public sealed record MaterialDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string MaterialType { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }
    public bool IsActive { get; init; }
}
