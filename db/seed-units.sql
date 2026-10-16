-- =====================================================================
-- Pulaski — units of measure and demo conversions             2026-10-15
-- The standard units (metric and US customary) with their exact factors to
-- each dimension's base unit, packaging units, and conversions for the demo
-- materials (D-026). Load after seed-materials.sql. Re-runnable: existing
-- unit codes and conversions are left alone.
--
-- Base units (factor 1): kg, m, m2, L, ea, s. Factors are exact by definition
-- (1 lb = 0.45359237 kg, 1 in = 0.0254 m, 1 US gal = 3.785411784 L).
-- =====================================================================

INSERT INTO core.uom (code, name, dimension, to_base_factor) VALUES
    -- Mass, base kg
    ('mg',    'Milligram',               'mass',   0.000001),
    ('g',     'Gram',                    'mass',   0.001),
    ('kg',    'Kilogram',                'mass',   1),
    ('t',     'Tonne (metric ton)',      'mass',   1000),
    ('oz',    'Ounce',                   'mass',   0.028349523125),
    ('lb',    'Pound',                   'mass',   0.45359237),
    ('cwt',   'Hundredweight (US, 100 lb)', 'mass', 45.359237),
    ('ton',   'Short ton (US, 2000 lb)', 'mass',   907.18474),
    ('lt',    'Long ton (UK, 2240 lb)',  'mass',   1016.0469088),
    -- Length, base m
    ('mm',    'Millimetre',              'length', 0.001),
    ('cm',    'Centimetre',              'length', 0.01),
    ('m',     'Metre',                   'length', 1),
    ('km',    'Kilometre',               'length', 1000),
    ('in',    'Inch',                    'length', 0.0254),
    ('ft',    'Foot',                    'length', 0.3048),
    ('yd',    'Yard',                    'length', 0.9144),
    ('mi',    'Mile',                    'length', 1609.344),
    -- Area, base m2
    ('mm2',   'Square millimetre',       'area',   0.000001),
    ('cm2',   'Square centimetre',       'area',   0.0001),
    ('m2',    'Square metre',            'area',   1),
    ('in2',   'Square inch',             'area',   0.00064516),
    ('ft2',   'Square foot',             'area',   0.09290304),
    ('yd2',   'Square yard',             'area',   0.83612736),
    -- Volume, base L
    ('mL',    'Millilitre',              'volume', 0.001),
    ('L',     'Litre',                   'volume', 1),
    ('m3',    'Cubic metre',             'volume', 1000),
    ('in3',   'Cubic inch',              'volume', 0.016387064),
    ('ft3',   'Cubic foot',              'volume', 28.316846592),
    ('qt',    'Quart (US liquid)',       'volume', 0.946352946),
    ('gal',   'Gallon (US liquid)',      'volume', 3.785411784),
    -- Count, base ea
    ('ea',    'Each',                    'count',  1),
    ('pr',    'Pair',                    'count',  2),
    ('dz',    'Dozen',                   'count',  12),
    ('gro',   'Gross (144)',             'count',  144),
    ('k',     'Thousand',                'count',  1000),
    -- Time, base s
    ('s',     'Second',                  'time',   1),
    ('min',   'Minute',                  'time',   60),
    ('h',     'Hour',                    'time',   3600),
    ('d',     'Day',                     'time',   86400)
ON CONFLICT (code) DO NOTHING;

-- Packaging: no standard factor. Each converts through a material's own conversion.
INSERT INTO core.uom (code, name, dimension, to_base_factor) VALUES
    ('bag',   'Bag',                     'other',  NULL),
    ('box',   'Box',                     'other',  NULL),
    ('ctn',   'Carton',                  'other',  NULL),
    ('roll',  'Roll',                    'other',  NULL),
    ('coil',  'Coil',                    'other',  NULL),
    ('drum',  'Drum',                    'other',  NULL),
    ('bb',    'Big bag (FIBC)',          'other',  NULL),
    ('pal',   'Pallet',                  'other',  NULL),
    ('bdl',   'Bundle',                  'other',  NULL)
ON CONFLICT (code) DO NOTHING;

-- Demo conversions: 1 from-unit = factor to-units, for each material. Weights are typical,
-- not measured. A pair already present in either direction is left alone.
WITH conversions(material_pattern, class_code, from_code, to_code, factor) AS (VALUES
    -- Extruded profiles weigh by series, in kg per metre, and ship 6 m lengths bundled.
    ('PRF-4410-%', NULL,      'm',   'kg',  1.20),
    ('PRF-4420-%', NULL,      'm',   'kg',  1.05),
    ('PRF-5100-%', NULL,      'm',   'kg',  1.60),
    ('PRF-5200-%', NULL,      'm',   'kg',  1.40),
    ('PRF-6000-%', NULL,      'm',   'kg',  0.90),
    ('PRF-7300-%', NULL,      'm',   'kg',  2.10),
    (NULL,         'PROFILE', 'bdl', 'm',   48),      -- 8 lengths of 6 m
    -- Granular and powder materials come in 25 kg bags and 1000 kg big bags.
    (NULL,         'RESIN',       'bag', 'kg', 25),
    (NULL,         'RESIN',       'bb',  'kg', 1000),
    (NULL,         'ADDITIVE',    'bag', 'kg', 25),
    (NULL,         'MASTERBATCH', 'bag', 'kg', 25),
    (NULL,         'COMPOUND',    'bag', 'kg', 25),
    (NULL,         'COMPOUND',    'bb',  'kg', 1000),
    -- Film is sold by the roll; moulded parts by the carton.
    (NULL,         'FILM',        'roll', 'kg', 25),
    (NULL,         'MOLDED',      'ctn',  'ea', 100),
    -- Packaging items.
    ('PKG-ENDCAP',   NULL, 'box',  'ea', 500),
    ('PKG-STRETCH',  NULL, 'ea',   'kg', 16),        -- a roll of stretch wrap
    ('PKG-STRAP-PET', NULL, 'ea',  'm',  1500),      -- a coil of strapping
    ('PKG-CTN-S',    NULL, 'pal',  'ea', 400),
    ('PKG-CTN-L',    NULL, 'pal',  'ea', 200)
)
INSERT INTO core.material_uom_conversion (material_id, from_uom_id, to_uom_id, factor)
SELECT m.id, f.id, t.id, c.factor
FROM conversions c
JOIN core.material m ON (c.material_pattern IS NOT NULL AND m.code LIKE c.material_pattern)
                     OR (c.class_code IS NOT NULL AND m.material_class_id = (SELECT id FROM core.material_class WHERE code = c.class_code))
JOIN core.uom f ON f.code = c.from_code
JOIN core.uom t ON t.code = c.to_code
WHERE NOT EXISTS (
    SELECT 1 FROM core.material_uom_conversion x
    WHERE x.material_id = m.id
      AND ((x.from_uom_id = f.id AND x.to_uom_id = t.id) OR (x.from_uom_id = t.id AND x.to_uom_id = f.id)));
