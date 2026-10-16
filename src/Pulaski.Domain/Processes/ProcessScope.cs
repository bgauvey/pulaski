namespace Pulaski.Domain.Processes;

/// <summary>
/// What a process can make (D-023): every material, or chosen material classes and single
/// materials, or a mix of classes and materials. An empty scope makes nothing yet.
/// </summary>
public sealed record ProcessScope(bool AllMaterials, IReadOnlyCollection<Guid> MaterialClassIds, IReadOnlyCollection<Guid> MaterialIds)
{
    public static ProcessScope Nothing { get; } = new(false, [], []);

    /// <summary>All materials makes the class and item lists moot, so they are dropped.</summary>
    public static ProcessScope Of(bool allMaterials, IEnumerable<Guid> materialClassIds, IEnumerable<Guid> materialIds) =>
        allMaterials ? new(true, [], []) : new(false, [.. materialClassIds.Distinct()], [.. materialIds.Distinct()]);

    public bool IsEmpty => !AllMaterials && MaterialClassIds.Count == 0 && MaterialIds.Count == 0;

    public bool Covers(Guid materialId, Guid? materialClassId) =>
        AllMaterials
        || MaterialIds.Contains(materialId)
        || (materialClassId is { } classId && MaterialClassIds.Contains(classId));
}
