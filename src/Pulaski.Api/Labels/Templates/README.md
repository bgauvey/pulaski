# Label templates

Each folder here is a template set a label printer can use (Configure → Site settings → Labels). Pulaski fills a template for each label
and sends it to the printer over raw TCP, or writes it into a drop folder for label software such as BarTender.

- A set holds one language (all `.zpl`, or all `.csv`…). File names: `{kind}.{format}.{ext}`, else `{kind}.{ext}`. Kind is `lot`, `container` or `location`; format is the site's label format
  (`gs1_128`, `gs1_datamatrix`); a label that can't be GS1 (no GTIN or SSCC) uses `{kind}.{ext}`.
- `{{Name}}` is replaced by a value, escaped for the file type (`.zpl`, `.xml`/`.btxml`, `.csv`, `.json`); unknown names become empty.
- Values: `Kind`, `Title`, `Code` (Pulaski's barcode), `Format`, `Line1`…`Line10`, `Gs1Hri` (`(01)…(10)…`), `Gs1Data` (with group
  separators), `Gs1Zpl128` (ZPL `^BC` data with `>;>8`/`>8` for FNC1), `Gs1ZplDataMatrix` (ZPL `^BX` data with `_1`, for `_` as the escape
  character); lots: `LotNumber`, `MaterialCode`, `MaterialName`, `Quantity`, `Uom`, `SupplierLot`, `Expiry`, `Gtin`, `Location`,
  `Container`, `Job`; containers: `ContainerCode`, `Location`, `Lots`, `Sscc`; locations: `LocationCode`, `LocationName`.
- To use your own, copy a set to a new folder (or point `Labels:TemplateDirectory` elsewhere) and edit it; changes apply on the next print.
