namespace Pulaski.Domain.Units;

/// <summary>
/// A unit of measure (D-026). <see cref="ToBaseFactor"/> converts it to its dimension's base unit
/// (1 lb = 0.45359237 kg) for any material; a packaging unit (box, bag, pallet) has none and
/// converts only through a material's own conversion.
/// </summary>
public sealed record Unit(Guid Id, string Code, string Dimension, decimal? ToBaseFactor)
{
    public bool IsPackaging => ToBaseFactor is null;
}

/// <summary>For one material: 1 <see cref="FromUomId"/> = <see cref="Factor"/> <see cref="ToUomId"/>. Works both ways.</summary>
public sealed record MaterialConversion(Guid FromUomId, Guid ToUomId, decimal Factor);

/// <summary>
/// Converts quantities of one material between units (D-026): within a dimension by the standard
/// factors, and across dimensions or to packaging units through the material's conversions, in
/// any combination (with 1 m = 1.6 kg, feet convert to pounds). Pure; built per material.
/// </summary>
public sealed class UnitConverter
{
    private readonly Dictionary<Guid, Unit> _units;
    private readonly Dictionary<string, List<(string To, decimal Factor)>> _edges = [];

    public UnitConverter(IEnumerable<Unit> units, IEnumerable<MaterialConversion> conversions)
    {
        _units = units.ToDictionary(u => u.Id);

        // Each standard unit links to a node for its dimension's base: a unit's factor takes it there.
        foreach (var unit in _units.Values.Where(u => !u.IsPackaging))
        {
            Link(Node(unit.Id), DimensionNode(unit.Dimension), unit.ToBaseFactor!.Value);
        }

        foreach (var conversion in conversions.Where(c => _units.ContainsKey(c.FromUomId) && _units.ContainsKey(c.ToUomId)))
        {
            Link(Node(conversion.FromUomId), Node(conversion.ToUomId), conversion.Factor);
        }
    }

    /// <summary>The number to multiply a quantity in <paramref name="from"/> by to get it in <paramref name="to"/>; null when they don't convert.</summary>
    public decimal? Factor(Guid from, Guid to)
    {
        if (from == to)
        {
            return 1m;
        }

        if (!_units.ContainsKey(from) || !_units.ContainsKey(to))
        {
            return null;
        }

        // Breadth first: conversions never form two routes, so the first one found is the only one.
        var target = Node(to);
        var seen = new HashSet<string> { Node(from) };
        var queue = new Queue<(string Node, decimal Factor)>([(Node(from), 1m)]);
        while (queue.TryDequeue(out var at))
        {
            if (at.Node == target)
            {
                return at.Factor;
            }

            foreach (var (next, factor) in _edges.GetValueOrDefault(at.Node, []))
            {
                if (seen.Add(next))
                {
                    queue.Enqueue((next, at.Factor * factor));
                }
            }
        }

        return null;
    }

    public bool CanConvert(Guid from, Guid to) => Factor(from, to) is not null;

    /// <summary>The quantity in <paramref name="to"/>, rounded to <paramref name="decimals"/> places.</summary>
    /// <exception cref="DomainRuleException">The units don't convert for this material.</exception>
    public decimal Convert(decimal quantity, Guid from, Guid to, int decimals = 6) =>
        Factor(from, to) is { } factor
            ? Math.Round(quantity * factor, decimals, MidpointRounding.AwayFromZero)
            : throw new DomainRuleException($"{Code(from)} doesn't convert to {Code(to)} for this material; add a conversion for it.");

    public string Code(Guid unitId) => _units.TryGetValue(unitId, out var unit) ? unit.Code : unitId.ToString();

    private void Link(string a, string b, decimal factor)
    {
        Add(a, b, factor);
        Add(b, a, 1m / factor);
    }

    private void Add(string from, string to, decimal factor)
    {
        if (!_edges.TryGetValue(from, out var list))
        {
            _edges[from] = list = [];
        }

        list.Add((to, factor));
    }

    private static string Node(Guid unitId) => unitId.ToString("N");

    private static string DimensionNode(string dimension) => "dimension:" + dimension;
}
