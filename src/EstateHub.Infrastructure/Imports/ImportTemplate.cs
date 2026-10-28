using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using EstateHub.Application.Imports;

namespace EstateHub.Infrastructure.Imports;

/// <summary>
/// The downloadable template: "Units" (headers only, to fill in), "Example" and "Read me". Mobile and
/// door columns are formatted as text so Excel keeps the leading 0 of 082….
/// </summary>
public sealed class ImportTemplate : IImportTemplate
{
    public const string FileName = "estatehub-import.xlsx";

    private static readonly string[][] Example =
    [
        ["1", "A1", "Block A", "2.3456", "84.5", "Thandi Mokoena", "thandi@example.co.za", "082 123 4567", "", "2019-03-01", "yes", "", "", "", ""],
        ["2", "A2", "Block A", "2.1000", "72", "Pieter van Wyk", "pieter@example.co.za", "", "50", "2015-07-15", "no", "Sipho Dlamini", "sipho@example.co.za", "073 555 0101", "2024-02-01"],
        ["2", "", "", "", "", "Anna van Wyk", "anna@example.co.za", "", "50", "2015-07-15", "no", "", "", "", ""],
        ["3", "B1", "Block B", "1.9500", "", "Thandi Mokoena", "thandi@example.co.za", "", "", "2021-11-30", "no", "", "", "", ""],
    ];

    public byte[] Create() => Build(rows: []);

    /// <summary>The template with <paramref name="rows"/> on the Units sheet (also used by tests).</summary>
    public static byte[] Build(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var headers = ImportColumns.All.Select(c => c.Header).ToArray();
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
        {
            var workbook = document.AddWorkbookPart();
            workbook.Workbook = new Workbook();
            var styles = workbook.AddNewPart<WorkbookStylesPart>();
            styles.Stylesheet = Stylesheet();
            var sheets = workbook.Workbook.AppendChild(new Sheets());

            AddSheet(workbook, sheets, 1, "Units", [headers, .. rows], textColumns: true);
            AddSheet(workbook, sheets, 2, "Example", [headers, .. Example], textColumns: true);
            AddSheet(workbook, sheets, 3, "Read me",
            [
                ["Fill in the Units sheet: one row per unit and owner. Repeat the section number for co-owners. Headers must stay in row 1.", ""],
                ["", ""],
                ["Column", "What to enter"],
                .. ImportColumns.All.Select(c => new[] { c.Header, c.Help }),
                ["", ""],
                ["Limits: 5 MB, 5,000 rows. Nothing is saved until you check the preview and commit; the import only adds new units.", ""],
            ], textColumns: false);
            workbook.Workbook.Save();
        }
        return stream.ToArray();
    }

    private static void AddSheet(WorkbookPart workbook, Sheets sheets, uint id, string name, IReadOnlyList<IReadOnlyList<string>> rows, bool textColumns)
    {
        var part = workbook.AddNewPart<WorksheetPart>();
        var columns = new Columns();
        var count = rows.Max(r => r.Count);
        for (uint i = 1; i <= count; i++)
        {
            var header = rows[0].ElementAtOrDefault((int)i - 1) ?? "";
            var text = textColumns && (header.Contains("mobile", StringComparison.OrdinalIgnoreCase) || header == "Door");
            columns.Append(new Column { Min = i, Max = i, Width = textColumns ? 18 : (i == 1 ? 22 : 110), CustomWidth = true, Style = text ? 2u : null });
        }

        var data = new SheetData();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new Row { RowIndex = (uint)(r + 1) };
            for (var c = 0; c < rows[r].Count; c++)
            {
                var bold = r == 0 && textColumns || (!textColumns && r == 2);
                row.Append(new Cell
                {
                    CellReference = $"{(char)('A' + c)}{r + 1}",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(rows[r][c])),
                    StyleIndex = bold ? 1u : 0u,
                });
            }
            data.Append(row);
        }

        part.Worksheet = new Worksheet(columns, data);
        sheets.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = id, Name = name });
    }

    // 0 = normal, 1 = bold (headers), 2 = text format "@" (keeps 082… as typed).
    private static Stylesheet Stylesheet() => new(
        new Fonts(new Font(), new Font(new Bold())) { Count = 2 },
        new Fills(new Fill(new PatternFill { PatternType = PatternValues.None }), new Fill(new PatternFill { PatternType = PatternValues.Gray125 })) { Count = 2 },
        new Borders(new Border()) { Count = 1 },
        new CellFormats(
            new CellFormat(),
            new CellFormat { FontId = 1, ApplyFont = true },
            new CellFormat { NumberFormatId = 49, ApplyNumberFormat = true }) { Count = 3 });
}
