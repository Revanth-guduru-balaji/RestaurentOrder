using System;
using System.Collections.Generic;
using System.IO;
using ClosedXML.Excel;
using RestaurantOrder.Data;

namespace RestaurantOrder.Services;

public class ImportResult
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; } = new();
}

public static class ExcelService
{
    public static ImportResult ImportMenu(string path)
    {
        var result = new ImportResult();
        var items = new List<MenuItem>();

        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.Worksheet(1);
        var range = ws.RangeUsed();
        if (range == null) return result;

        int firstRow = range.FirstRow().RowNumber();
        int lastRow = range.LastRow().RowNumber();
        int startRow = firstRow;

        var headerRow = ws.Row(firstRow);
        var headerA = (headerRow.Cell(1).GetString() ?? "").Trim().ToLowerInvariant();
        if (headerA == "name" || headerA == "item" || headerA.Contains("name"))
            startRow = firstRow + 1;

        for (int r = startRow; r <= lastRow; r++)
        {
            var name = (ws.Cell(r, 1).GetString() ?? "").Trim();
            var category = (ws.Cell(r, 2).GetString() ?? "").Trim();
            var priceText = ws.Cell(r, 3).GetString();
            var availText = (ws.Cell(r, 4).GetString() ?? "").Trim();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(priceText))
                continue;

            if (string.IsNullOrWhiteSpace(name))
            {
                result.Errors.Add($"Row {r}: missing name");
                result.Skipped++;
                continue;
            }
            decimal price;
            if (!ws.Cell(r, 3).TryGetValue<decimal>(out price))
            {
                if (!decimal.TryParse(priceText, System.Globalization.NumberStyles.Number,
                                       System.Globalization.CultureInfo.InvariantCulture, out price)
                    && !decimal.TryParse(priceText, System.Globalization.NumberStyles.Number,
                                       System.Globalization.CultureInfo.CurrentCulture, out price))
                {
                    result.Errors.Add($"Row {r}: invalid price '{priceText}'");
                    result.Skipped++;
                    continue;
                }
            }
            if (price < 0)
            {
                result.Errors.Add($"Row {r}: negative price");
                result.Skipped++;
                continue;
            }
            bool available = true;
            if (!string.IsNullOrWhiteSpace(availText))
            {
                var t = availText.ToLowerInvariant();
                if (t is "1" or "true" or "yes" or "y" or "available") available = true;
                else if (t is "0" or "false" or "no" or "n" or "unavailable") available = false;
                else
                {
                    result.Errors.Add($"Row {r}: '{availText}' not understood for Available — assumed Yes");
                }
            }
            items.Add(new MenuItem
            {
                Name = name,
                Category = category,
                Price = price,
                IsAvailable = available
            });
        }

        result.Imported = MenuRepository.BulkUpsertByName(items);
        return result;
    }

    public static void WriteSampleTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Menu");

        ws.Cell(1, 1).Value = "Name";
        ws.Cell(1, 2).Value = "Category";
        ws.Cell(1, 3).Value = "Price";
        ws.Cell(1, 4).Value = "Available";
        var hdr = ws.Range(1, 1, 1, 4);
        hdr.Style.Font.Bold = true;
        hdr.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFE5E7EB");

        var samples = new (string n, string c, decimal p, bool a)[]
        {
            ("Idly (1 pc)", "Breakfast", 10m, true),
            ("Idly Plate (3 pcs)", "Breakfast", 25m, true),
            ("Plain Dosa", "Breakfast", 40m, true),
            ("Masala Dosa", "Breakfast", 60m, true),
            ("Vada (2 pcs)", "Breakfast", 30m, true),
            ("Filter Coffee", "Beverages", 20m, true),
            ("Tea", "Beverages", 15m, true),
            ("Veg Meals", "Lunch", 120m, true),
        };
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            ws.Cell(i + 2, 1).Value = s.n;
            ws.Cell(i + 2, 2).Value = s.c;
            ws.Cell(i + 2, 3).Value = (double)s.p;
            ws.Cell(i + 2, 4).Value = s.a ? "Yes" : "No";
        }
        ws.Columns().AdjustToContents();
        wb.SaveAs(path);
    }
}
