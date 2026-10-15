-- =====================================================================
-- Pulaski — demo materials                                  2026-10-14
-- 200 materials across eleven classes, for lines that extrude, mould,
-- compound and pack plastics. Load after seed-demo.sql. Re-runnable:
-- existing classes and material codes are left alone.
--
-- Like the rest of the seed these are master data loaded before go-live,
-- so they carry no creation events.
-- =====================================================================

INSERT INTO core.material_class (code, name) VALUES
    ('RESIN',       'Resins'),
    ('PROFILE',     'Extruded profiles'),
    ('ADDITIVE',    'Additives and fillers'),
    ('MASTERBATCH', 'Masterbatches'),
    ('COMPOUND',    'Compounds and dry blends'),
    ('PIPE',        'Extruded pipe'),
    ('SHEET',       'Extruded sheet'),
    ('FILM',        'Blown film'),
    ('MOLDED',      'Injection-moulded parts'),
    ('PACKAGING',   'Packaging'),
    ('CONSUMABLE',  'Line consumables')
ON CONFLICT (code) DO NOTHING;

WITH colour(code, name) AS (VALUES
    ('WH', 'white'), ('GY', 'grey'), ('BR', 'brown'), ('AN', 'anthracite'), ('BG', 'beige'), ('CR', 'cream')),
items(code, name, material_type, class_code, uom_code, lot_tracked) AS (
    -- Extruded profiles: six series in six colours (34, the two 4410 whites/greys already exist).
    SELECT format('PRF-%s-%s', s.series, c.code), format('%s %s, %s', s.kind, s.series, c.name),
           'finished', 'PROFILE', 'm', true
    FROM (VALUES ('4410', 'Window frame profile'), ('4420', 'Window sash profile'), ('5100', 'Door frame profile'),
                 ('5200', 'Door leaf profile'), ('6000', 'Fence rail profile'), ('7300', 'Decking board profile')) AS s(series, kind)
    CROSS JOIN colour c
    WHERE (s.series, c.code) NOT IN (('4410', 'WH'), ('4410', 'GY'))

    UNION ALL
    -- PVC pressure pipe: ten diameters, two pressure classes (20).
    SELECT format('PIPE-PVC-%s-SDR%s', d, sdr), format('PVC pressure pipe %s mm, SDR %s', d, sdr),
           'finished', 'PIPE', 'm', true
    FROM unnest(ARRAY[20, 25, 32, 40, 50, 63, 75, 90, 110, 160]) AS d
    CROSS JOIN unnest(ARRAY[11, 17]) AS sdr

    UNION ALL
    -- Sheet: six thicknesses in three colours (18).
    SELECT format('SHT-PVC-%sMM-%s', t, c.code), format('PVC sheet %s mm, %s', t, c.name),
           'finished', 'SHEET', 'kg', true
    FROM unnest(ARRAY[1, 2, 3, 4, 5, 6]) AS t
    CROSS JOIN (VALUES ('WH', 'white'), ('BK', 'black'), ('CL', 'clear')) AS c(code, name)

    UNION ALL
    -- Blown film: three polymers, four gauges, natural or black (24).
    SELECT format('FLM-%s-%s-%s', p, g, c.code), format('%s film %s µm, %s', p, g, c.name),
           'finished', 'FILM', 'kg', true
    FROM unnest(ARRAY['LDPE', 'LLDPE', 'HDPE']) AS p
    CROSS JOIN unnest(ARRAY[20, 30, 50, 80]) AS g
    CROSS JOIN (VALUES ('NAT', 'natural'), ('BLK', 'black')) AS c(code, name)

    UNION ALL
    -- Moulded parts: eight parts in four colours (32).
    SELECT format('MLD-%s-%s', part.code, c.code), format('%s, %s', part.name, c.name),
           'finished', 'MOLDED', 'ea', true
    FROM (VALUES ('CAP28', 'Screw cap 28 mm'), ('CAP38', 'Screw cap 38 mm'), ('CL63', 'Flip-top closure 63 mm'),
                 ('CRT600', 'Stacking crate 600x400'), ('BKT10', 'Bucket 10 L'), ('BKT20', 'Bucket 20 L'),
                 ('LID10', 'Bucket lid 10 L'), ('LID20', 'Bucket lid 20 L')) AS part(code, name)
    CROSS JOIN (VALUES ('WH', 'white'), ('BK', 'black'), ('BL', 'blue'), ('RD', 'red')) AS c(code, name)

    UNION ALL
    -- Resins (21; PVC-K67 already exists).
    SELECT r.code, r.name, 'raw', 'RESIN', 'kg', true
    FROM (VALUES
        ('PVC-K57', 'PVC suspension resin K57'), ('PVC-K60', 'PVC suspension resin K60'),
        ('PVC-K65', 'PVC suspension resin K65'), ('PVC-K70', 'PVC suspension resin K70'),
        ('HDPE-PE100', 'HDPE pipe grade PE100'), ('HDPE-PE80', 'HDPE pipe grade PE80'),
        ('HDPE-BM5', 'HDPE blow moulding MFI 0.5'), ('HDPE-IM8', 'HDPE injection MFI 8'), ('HDPE-FLM07', 'HDPE film MFI 0.7'),
        ('LDPE-FLM2', 'LDPE film MFI 2'), ('LDPE-FLM4', 'LDPE film MFI 4'), ('LDPE-IM20', 'LDPE injection MFI 20'),
        ('LDPE-EXT1', 'LDPE extrusion coating MFI 1'),
        ('LLDPE-C4', 'LLDPE butene film MFI 1'), ('LLDPE-C6', 'LLDPE hexene film MFI 1'), ('LLDPE-C8', 'LLDPE octene film MFI 1'),
        ('PP-HO12', 'PP homopolymer MFI 12'), ('PP-HO25', 'PP homopolymer MFI 25'), ('PP-HO45', 'PP homopolymer MFI 45'),
        ('PP-CO15', 'PP impact copolymer MFI 15'), ('PP-CO30', 'PP impact copolymer MFI 30')) AS r(code, name)

    UNION ALL
    -- Additives and fillers (14).
    SELECT a.code, a.name, 'raw', 'ADDITIVE', 'kg', true
    FROM (VALUES
        ('ADD-CAZN-STAB', 'Calcium-zinc heat stabiliser'), ('ADD-SN-STAB', 'Organotin heat stabiliser'),
        ('ADD-LUB-INT', 'Internal lubricant'), ('ADD-LUB-EXT', 'External lubricant, paraffin wax'),
        ('ADD-PE-WAX', 'Polyethylene wax'), ('ADD-ACR-IMP', 'Acrylic impact modifier'),
        ('ADD-CPE-IMP', 'Chlorinated PE impact modifier'), ('ADD-PA-ACR', 'Acrylic processing aid'),
        ('ADD-CACO3-1', 'Calcium carbonate filler, 1 µm coated'), ('ADD-CACO3-3', 'Calcium carbonate filler, 3 µm'),
        ('ADD-TALC', 'Talc filler'), ('ADD-UV-HALS', 'UV stabiliser, HALS'),
        ('ADD-AO-1010', 'Primary antioxidant'), ('ADD-FR-ATH', 'Flame retardant, ATH')) AS a(code, name)

    UNION ALL
    -- Masterbatches (13; TIO2-MB already exists).
    SELECT m.code, m.name, 'raw', 'MASTERBATCH', 'kg', true
    FROM (VALUES
        ('MB-BK-CB40', 'Black masterbatch, 40% carbon black'), ('MB-GY-7035', 'Light grey masterbatch'),
        ('MB-BR-8017', 'Chocolate brown masterbatch'), ('MB-AN-7016', 'Anthracite masterbatch'),
        ('MB-BG-1015', 'Light ivory masterbatch'), ('MB-BL-5015', 'Sky blue masterbatch'),
        ('MB-RD-3020', 'Traffic red masterbatch'), ('MB-GN-6018', 'Yellow-green masterbatch'),
        ('MB-YL-1023', 'Traffic yellow masterbatch'), ('MB-UV-5', 'UV stabiliser masterbatch'),
        ('MB-AS-GMS', 'Antistatic masterbatch'), ('MB-SLIP-ER', 'Slip masterbatch, erucamide'),
        ('MB-AB-SIO2', 'Antiblock masterbatch')) AS m(code, name)

    UNION ALL
    -- Compounds and dry blends (8).
    SELECT x.code, x.name, 'intermediate', 'COMPOUND', 'kg', true
    FROM (VALUES
        ('CMP-PVC-PRF-WH', 'PVC profile dry blend, white'), ('CMP-PVC-PRF-GY', 'PVC profile dry blend, grey'),
        ('CMP-PVC-PRF-CAP', 'PVC capstock compound'), ('CMP-PVC-PIPE', 'PVC pipe dry blend'),
        ('CMP-PVC-SHT', 'PVC sheet compound'), ('CMP-PP-TALC20', 'PP compound, 20% talc'),
        ('CMP-HDPE-BK', 'HDPE pipe compound, black'), ('CMP-REGRIND', 'PVC regrind, mixed colour')) AS x(code, name)

    UNION ALL
    -- Packaging (10).
    SELECT p.code, p.name, 'packaging', 'PACKAGING', 'ea', false
    FROM (VALUES
        ('PKG-PAL-EUR', 'Euro pallet 1200x800'), ('PKG-PAL-LONG', 'Long pallet 6500 mm for profiles'),
        ('PKG-STRETCH', 'Stretch wrap roll, 23 µm'), ('PKG-STRAP-PET', 'PET strapping coil'),
        ('PKG-CTN-S', 'Carton, small'), ('PKG-CTN-L', 'Carton, large'), ('PKG-ENDCAP', 'Profile end cap'),
        ('PKG-EDGE', 'Cardboard edge protector'), ('PKG-LBL-PAL', 'Pallet label'), ('PKG-BAG-25', 'Valve bag 25 kg')) AS p(code, name)

    UNION ALL
    -- Line consumables (6).
    SELECT k.code, k.name, 'consumable', 'CONSUMABLE', 'ea', false
    FROM (VALUES
        ('CON-SCREEN-60', 'Screen pack, 60 mesh'), ('CON-SCREEN-100', 'Screen pack, 100 mesh'),
        ('CON-SAWBLADE', 'Saw blade, carbide 350 mm'), ('CON-PURGE', 'Purging compound bag'),
        ('CON-DIE-CLEAN', 'Die cleaning kit'), ('CON-INK-JET', 'Inkjet marking ink cartridge')) AS k(code, name)
)
INSERT INTO core.material (code, name, material_type, material_class_id, base_uom_id, is_lot_tracked)
SELECT i.code, i.name, i.material_type, mc.id, u.id, i.lot_tracked
FROM items i
JOIN core.material_class mc ON mc.code = i.class_code
JOIN core.uom u ON u.code = i.uom_code
ON CONFLICT (code) DO NOTHING;
