using System;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RestaurantOrder.Data;

namespace RestaurantOrder.Services;

public static class ReceiptPrinter
{
    /// Print using saved default printer. Prompts only if none saved or it's gone.
    /// Returns true if the receipt was sent to a printer.
    public static bool PrintQuiet(Order order)
    {
        var s = AppSettings.Current;
        var dlg = new System.Windows.Controls.PrintDialog();
        PrintQueue? queue = TryFindSavedQueue(s.DefaultPrinterName);

        if (queue == null)
        {
            if (dlg.ShowDialog() != true) return false;
            s.DefaultPrinterName = dlg.PrintQueue?.FullName ?? "";
            try { s.Save(); } catch { }
            queue = dlg.PrintQueue;
        }
        else
        {
            dlg.PrintQueue = queue;
        }

        if (queue == null) return false;
        var doc = BuildDocument(order, dlg.PrintableAreaWidth, s.CompactReceipt);
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Order #{order.Id:D5}");
        return true;
    }

    /// Always show the printer chooser (used for "change printer" or first-run flow).
    public static bool PrintWithDialog(Order order)
    {
        var s = AppSettings.Current;
        var dlg = new System.Windows.Controls.PrintDialog();
        if (dlg.ShowDialog() != true) return false;

        s.DefaultPrinterName = dlg.PrintQueue?.FullName ?? s.DefaultPrinterName;
        try { s.Save(); } catch { }

        var doc = BuildDocument(order, dlg.PrintableAreaWidth, s.CompactReceipt);
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Order #{order.Id:D5}");
        return true;
    }

    public static FlowDocument BuildPreview(Order order, double width = 320)
        => BuildDocument(order, width, AppSettings.Current.CompactReceipt);

    private static PrintQueue? TryFindSavedQueue(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            using var server = new LocalPrintServer();
            foreach (var q in server.GetPrintQueues())
            {
                if (string.Equals(q.FullName, name, StringComparison.OrdinalIgnoreCase))
                    return q;
            }
        }
        catch { }
        return null;
    }

    public static string[] InstalledPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            var list = new System.Collections.Generic.List<string>();
            foreach (var q in server.GetPrintQueues())
                list.Add(q.FullName);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }
        catch { return Array.Empty<string>(); }
    }

    private static FlowDocument BuildDocument(Order order, double pageWidth, bool compact)
    {
        var s = AppSettings.Current;
        double bodySize = compact ? 9.5 : 11;
        double headerSize = compact ? 13 : 17;
        double subSize = compact ? 9 : 10.5;
        double totalSize = compact ? 11 : 13;
        double padX = compact ? 10 : 18;
        double padY = compact ? 8 : 14;

        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = bodySize,
            PagePadding = new Thickness(padX, padY, padX, padY),
            ColumnGap = 0,
            ColumnWidth = double.PositiveInfinity,
            PageWidth = pageWidth > 0 ? pageWidth : 320,
            TextAlignment = TextAlignment.Left,
            Foreground = Brushes.Black,
            LineHeight = compact ? 13 : 16,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        // Shop header — only print non-empty lines
        var header = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, compact ? 2 : 4)
        };
        if (!string.IsNullOrWhiteSpace(s.ShopName))
            header.Inlines.Add(new Run(s.ShopName) { FontSize = headerSize, FontWeight = FontWeights.Bold });
        if (!string.IsNullOrWhiteSpace(s.ShopLine2))
        {
            header.Inlines.Add(new LineBreak());
            header.Inlines.Add(new Run(s.ShopLine2) { FontSize = subSize });
        }
        if (!string.IsNullOrWhiteSpace(s.ShopAddress))
        {
            header.Inlines.Add(new LineBreak());
            header.Inlines.Add(new Run(s.ShopAddress) { FontSize = subSize - 0.5 });
        }
        if (header.Inlines.Count > 0) doc.Blocks.Add(header);

        doc.Blocks.Add(MakeRule(compact));

        // Compact meta row: Order # + date on one line; customer on next only if present
        var meta = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, compact ? 2 : 4),
            FontSize = subSize
        };
        meta.Inlines.Add(new Run($"#{order.Id:D5}") { FontWeight = FontWeights.Bold });
        meta.Inlines.Add(new Run($"   {order.CreatedAt:dd-MMM-yy HH:mm}"));
        meta.Inlines.Add(new Run($"   {order.PaymentMethod}"));
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
        {
            meta.Inlines.Add(new LineBreak());
            meta.Inlines.Add(new Run($"Customer: {order.CustomerName}"));
        }
        doc.Blocks.Add(meta);

        doc.Blocks.Add(MakeRule(compact));

        // Items table
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0),
            FontSize = bodySize
        };
        table.Columns.Add(new TableColumn { Width = new GridLength(2.6, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.6, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(0.9, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(1.0, GridUnitType.Star) });

        var headRG = new TableRowGroup();
        var headRow = new TableRow();
        headRow.Cells.Add(MakeCell("Item", FontWeights.Bold, compact));
        headRow.Cells.Add(MakeCell("Qty", FontWeights.Bold, compact, TextAlignment.Right));
        headRow.Cells.Add(MakeCell("Rate", FontWeights.Bold, compact, TextAlignment.Right));
        headRow.Cells.Add(MakeCell("Amt", FontWeights.Bold, compact, TextAlignment.Right));
        headRG.Rows.Add(headRow);
        table.RowGroups.Add(headRG);

        var rg = new TableRowGroup();
        decimal total = 0;
        int totalQty = 0;
        foreach (var it in order.Items)
        {
            var row = new TableRow();
            row.Cells.Add(MakeCell(it.MenuItemName, FontWeights.Normal, compact));
            row.Cells.Add(MakeCell(it.Quantity.ToString(), FontWeights.Normal, compact, TextAlignment.Right));
            row.Cells.Add(MakeCell(Money.Format(it.UnitPrice, withSymbol: false), FontWeights.Normal, compact, TextAlignment.Right));
            row.Cells.Add(MakeCell(Money.Format(it.LineTotal, withSymbol: false), FontWeights.Normal, compact, TextAlignment.Right));
            rg.Rows.Add(row);
            total += it.LineTotal;
            totalQty += it.Quantity;
        }
        table.RowGroups.Add(rg);
        doc.Blocks.Add(table);

        doc.Blocks.Add(MakeRule(compact));

        // Subtotal row (always shown)
        AddTotalLine(doc, "Subtotal", Money.Format(total), subSize, FontWeights.Normal);
        if (order.TaxAmount > 0m)
            AddTotalLine(doc, "Tax", Money.Format(order.TaxAmount), subSize, FontWeights.Normal);
        if (order.DiscountAmount > 0m)
            AddTotalLine(doc, "Discount", "− " + Money.Format(order.DiscountAmount), subSize, FontWeights.Normal);
        if (order.RoundingAmount != 0m)
            AddTotalLine(doc, "Rounding",
                (order.RoundingAmount >= 0 ? "+ " : "− ") + Money.Format(Math.Abs(order.RoundingAmount)),
                subSize, FontWeights.Normal);

        doc.Blocks.Add(MakeRule(compact));

        var totals = new Paragraph
        {
            FontSize = totalSize,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0)
        };
        totals.Inlines.Add(new Run($"Items: {totalQty}    Total: {Money.Format(order.Total > 0 ? order.Total : total)}"));
        doc.Blocks.Add(totals);

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            doc.Blocks.Add(new Paragraph(new Run("Notes: " + order.Notes)) { FontSize = subSize, Margin = new Thickness(0, compact ? 2 : 4, 0, 0) });
        }

        if (!compact)
        {
            doc.Blocks.Add(MakeRule(false));
            var footer = new Paragraph
            {
                TextAlignment = TextAlignment.Center,
                FontSize = subSize,
                Margin = new Thickness(0, 4, 0, 0)
            };
            footer.Inlines.Add(new Run("Thank you!"));
            doc.Blocks.Add(footer);
        }
        else
        {
            var footer = new Paragraph
            {
                TextAlignment = TextAlignment.Center,
                FontSize = subSize,
                Margin = new Thickness(0, 4, 0, 0)
            };
            footer.Inlines.Add(new Run("Thank you!"));
            doc.Blocks.Add(footer);
        }

        return doc;
    }

    private static void AddTotalLine(FlowDocument doc, string label, string value, double fontSize, FontWeight weight)
    {
        var p = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = weight,
            Margin = new Thickness(0)
        };
        p.Inlines.Add(new Run(label));
        p.Inlines.Add(new Run("\t" + value) { });
        // Use a Table-like trick: just right-align via a tab-spaced run isn't ideal in FlowDocument.
        // Simplest reliable approach: render as a single paragraph with right-aligned value run.
        p.Inlines.Clear();
        p.Inlines.Add(new Run(label));
        var space = new Run(value)
        {
            FontWeight = weight
        };
        p.Inlines.Add(new Run("    ")); // visual gap; the receipt is fixed width so this reads okay
        p.Inlines.Add(space);
        p.TextAlignment = TextAlignment.Right;
        doc.Blocks.Add(p);
    }

    private static TableCell MakeCell(string text, FontWeight weight, bool compact, TextAlignment alignment = TextAlignment.Left)
    {
        var p = new Paragraph(new Run(text))
        {
            Margin = new Thickness(2, compact ? 0 : 1, 2, compact ? 0 : 1),
            TextAlignment = alignment,
            FontWeight = weight
        };
        return new TableCell(p);
    }

    private static BlockUIContainer MakeRule(bool compact)
    {
        var line = new System.Windows.Shapes.Rectangle
        {
            Height = compact ? 0.6 : 1,
            Fill = new SolidColorBrush(Color.FromRgb(120, 120, 120)),
            Margin = new Thickness(0, compact ? 2 : 4, 0, compact ? 2 : 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        return new BlockUIContainer(line) { Margin = new Thickness(0) };
    }
}
