using System.IO.Compression;
using System.Text;
using EstateHub.Application.Imports;
using EstateHub.Infrastructure.Imports;
using FluentAssertions;

namespace EstateHub.Tests.Infrastructure;

// Reading uploaded spreadsheets safely (slice 3.3): the template round-trips, CSV quirks are handled,
// and files that would be dangerous or pointless to parse are refused with a reason staff can act on.
public class SpreadsheetReaderTests
{
    private readonly SpreadsheetReader _reader = new();

    [Fact]
    public void The_Template_Reads_Back_From_Its_Units_Sheet_Not_The_Example()
    {
        var sheet = _reader.Read("units.xlsx", ImportTemplate.Build([["1", "A1", "", "33.3333", "", "Thandi"], ["2", "A2"]]));

        sheet.Headers.Select(ImportColumns.Match).Should().Equal(ImportColumns.All.Select(c => c.Key));
        sheet.Rows.Select(r => (r.RowNumber, r.Cells[0], r.Cells[1])).Should().Equal((2, "1", "A1"), (3, "2", "A2"));
        sheet.Rows[0].Cells[5].Should().Be("Thandi");
    }

    [Fact]
    public void The_Empty_Template_Has_Headers_And_No_Rows()
    {
        var sheet = _reader.Read(ImportTemplate.FileName, new ImportTemplate().Create());

        sheet.Headers.Should().HaveCount(ImportColumns.All.Count);
        sheet.Rows.Should().BeEmpty();
    }

    [Fact]
    public void Csv_With_Semicolons_Quotes_And_A_Bom_Is_Read()
    {
        var csv = "﻿Section;Owner name;Door\r\n1;\"van Wyk; Pieter \"\"PJ\"\"\";A1\r\n\r\n2;Anna;\"B\n2\"\r\n";

        var sheet = _reader.Read("units.csv", Encoding.UTF8.GetBytes(csv));

        sheet.Headers.Should().Equal("Section", "Owner name", "Door");
        sheet.Rows.Select(r => r.Cells[1]).Should().Equal("van Wyk; Pieter \"PJ\"", "Anna");
        sheet.Rows[1].Cells[2].Should().Be("B\n2");
    }

    [Fact]
    public void Old_Xls_Files_Are_Refused_With_How_To_Fix_It()
    {
        var act = () => _reader.Read("units.xls", [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

        act.Should().Throw<ArgumentException>().WithMessage("*Save As*Excel Workbook (.xlsx)*");
    }

    [Fact]
    public void Binary_Files_That_Arent_Spreadsheets_Are_Refused()
    {
        var act = () => _reader.Read("photo.csv", [0xFF, 0xD8, 0xFF, 0x00, 0x10]);

        act.Should().Throw<ArgumentException>().WithMessage("*doesn't look like*");
    }

    [Fact]
    public void A_Zip_Bomb_Is_Refused_Before_Parsing()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open();
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 70; i++)
                entry.Write(zeros);
        }

        var bytes = stream.ToArray();
        bytes.Length.Should().BeLessThan(1024 * 1024, "70 MB of zeros packs into a tiny upload");
        var act = () => _reader.Read("units.xlsx", bytes);

        act.Should().Throw<ArgumentException>().WithMessage("*unpacks to far more data*");
    }

    [Fact]
    public void More_Than_5000_Rows_Are_Refused()
    {
        var csv = new StringBuilder("Section,PQ %\n");
        for (var i = 1; i <= ImportPlanner.MaxRows + 1; i++)
            csv.Append(i).Append(",0.01\n");

        var act = () => _reader.Read("big.csv", Encoding.UTF8.GetBytes(csv.ToString()));

        act.Should().Throw<ArgumentException>().WithMessage("*more than 5,000 rows*");
    }

    [Fact]
    public void A_Damaged_Xlsx_Is_Refused()
    {
        var act = () => _reader.Read("units.xlsx", [(byte)'P', (byte)'K', 3, 4, 0, 0, 1, 2, 3]);

        act.Should().Throw<ArgumentException>();
    }
}
