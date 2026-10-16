namespace Pulaski.Web.Services;

/// <summary>
/// The equipment the user has selected (a site, department or line), kept for the session so each
/// screen that filters by equipment opens on the same selection. Null means all equipment.
/// </summary>
public sealed class EquipmentSelection
{
    public Guid? EquipmentId { get; set; }
}
