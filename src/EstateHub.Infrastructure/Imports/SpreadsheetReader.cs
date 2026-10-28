using System.Globalization;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using EstateHub.Application.Imports;

namespace EstateHub.Infrastructure.Imports;

/// <summary>
/// Reads an uploaded .xlsx (Microsoft's Open XML SDK, streaming) or .csv into header + rows. Guards:
/// the zip's unpacked size and entry count are checked before parsing (zip bombs), every XML part is
/// capped in size, at most <see cref="MaxColumns"/> columns and <see cref="ImportPlanner.MaxRows"/>
/// data rows are read, formulas are never evaluated (only Excel's saved values are used) and macros
/// are ignored. The file itself is never stored.
/// </summary>
public sealed class SpreadsheetReader : IImportFileReader
{
    public const long MaxUnpackedBytes = 60L * 1024 * 1024;
    public const int MaxZipEntries = 500;
    public const int MaxColumns = 60;

    public ImportSheet Read(string fileName, byte[] content)
    {
        if (content is [0xD0, 0xCF, 0x11, 0xE0, ..])
            throw new ArgumentException("This is an old Excel file (.xls). In Excel choose File → Save As → Excel Workbook (.xlsx), then upload that.");
        if (content is [(byte)'P', (byte)'K', 3, 4, ..])
            return ReadXlsx(content);
        if (fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) || content.AsSpan().Contains((byte)0))
            throw new ArgumentException("This doesn't look like an Excel (.xlsx) or CSV file.");
        return ReadCsv(content);
    }

    // ---------- .xlsx ----------

    private static ImportSheet ReadXlsx(byte[] content)
    {
        CheckZip(content);
        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var document = SpreadsheetDocument.Open(stream, false, new OpenSettings { MaxCharactersInPart = MaxUnpackedBytes / 2 });
            var workbook = document.WorkbookPart ?? throw new ArgumentException("The workbook has no sheets.");
            var sheets = workbook.Workbook?.Sheets?.Elements<Sheet>().ToList() ?? [];
            var sheet = sheets.FirstOrDefault(s => string.Equals(s.Name?.Value?.Trim(), "Units", StringComparison.OrdinalIgnoreCase))
                        ?? sheets.FirstOrDefault()
                        ?? throw new ArgumentException("The workbook has no sheets.");
            if (sheet.Id?.Value is not { } partId || workbook.GetPartById(partId) is not WorksheetPart part)
                throw new ArgumentException("The 'Units' sheet couldn't be read.");

            var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToArray() ?? [];
            var rows = new List<ImportSheetRow>();
            IReadOnlyList<string>? headers = null;

            using var reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                    continue;
                var row = (Row)reader.LoadCurrentElement()!;
                var cells = Cells(row, shared);
                if (cells.All(string.IsNullOrWhiteSpace))
                    continue;

                if (headers is null)
                {
                    headers = cells;
                    continue;
                }
                if (rows.Count == ImportPlanner.MaxRows)
                    throw TooManyRows();
                rows.Add(new ImportSheetRow((int)(row.RowIndex?.Value ?? (uint)(rows.Count + 2)), cells));
            }

            return new ImportSheet(headers ?? [], rows);
        }
        catch (Exception e) when (e is OpenXmlPackageException or InvalidDataException or System.Xml.XmlException or InvalidOperationException)
        {
            throw new ArgumentException("The Excel file couldn't be read. Open it in Excel, save it again as .xlsx and upload that.");
        }
    }

    /// <summary>Before parsing: refuse archives that would unpack into something huge (zip bombs).</summary>
    private static void CheckZip(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > MaxZipEntries || zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
                throw new ArgumentException("This file unpacks to far more data than a unit list needs. Save a fresh copy from the template and try again.");
        }
        catch (InvalidDataException)
        {
            throw new ArgumentException("The Excel file is damaged or isn't really an .xlsx file.");
        }
    }

    private static List<string> Cells(Row row, string[] shared)
    {
        var cells = new List<string>();
        foreach (var cell in row.Elements<Cell>())
        {
            var index = cell.CellReference?.Value is { } reference ? ColumnIndex(reference) : cells.Count;
            if (index >= MaxColumns)
                continue;
            while (cells.Count < index)
                cells.Add(string.Empty);
            if (cells.Count == index)
                cells.Add(Value(cell, shared));
        }
        return cells;
    }

    private static string Value(Cell cell, string[] shared)
    {
        var raw = cell.CellValue?.Text ?? string.Empty;
        var type = cell.DataType?.Value;
        if (type == CellValues.SharedString)
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Length ? shared[i] : string.Empty;
        if (type == CellValues.InlineString)
            return cell.InlineString?.InnerText ?? raw;
        if (type == CellValues.Boolean)
            return raw == "1" ? "yes" : "no";
        if (type == CellValues.Error)
            return string.Empty;
        if (type == CellValues.String || type == CellValues.Date)
            return raw;

        // Numbers: Excel's saved double, without floating-point noise (33.333300000000001 → 33.3333).
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number.ToString("G15", CultureInfo.InvariantCulture)
            : raw;
    }

    /// <summary>"C7" → 2.</summary>
    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference.TakeWhile(char.IsAsciiLetter))
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        return index - 1;
    }

    // ---------- .csv ----------

    private static ImportSheet ReadCsv(byte[] content)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(content); // older Excel "CSV" exports
        }
        text = text.TrimStart('﻿');

        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ',', ';', '\t' }.MaxBy(d => firstLine.Count(c => c == d));

        IReadOnlyList<string>? headers = null;
        var rows = new List<ImportSheetRow>();
        var lineNumber = 0;
        foreach (var record in Records(text, delimiter))
        {
            lineNumber++;
            if (record.All(string.IsNullOrWhiteSpace))
                continue;
            var cells = record.Take(MaxColumns).ToList();
            if (headers is null)
            {
                headers = cells;
                continue;
            }
            if (rows.Count == ImportPlanner.MaxRows)
                throw TooManyRows();
            rows.Add(new ImportSheetRow(lineNumber, cells));
        }
        return new ImportSheet(headers ?? [], rows);
    }

    /// <summary>RFC 4180: quoted fields may hold the delimiter, quotes ("") and line breaks.</summary>
    private static IEnumerable<List<string>> Records(string text, char delimiter)
    {
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) quoted = true;
            else if (c == delimiter) { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\n')
            {
                record.Add(field.ToString().TrimEnd('\r'));
                field.Clear();
                yield return record;
                record = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString().TrimEnd('\r'));
            yield return record;
        }
    }

    private static ArgumentException TooManyRows() =>
        new(string.Create(CultureInfo.InvariantCulture, $"The file has more than {ImportPlanner.MaxRows:N0} rows. Split it into smaller files."));
}
