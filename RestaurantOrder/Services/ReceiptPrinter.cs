using System;
using System.IO;
using System.Printing;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using RestaurantOrder.Data;

namespace RestaurantOrder.Services;

/// Outcome of a print attempt. Lets the caller tell "cancelled at the chooser"
/// from "the bill printed but the kitchen copy failed" — the latter is an
/// operational hazard that must be surfaced, not swallowed.
public readonly record struct PrintResult(bool BillPrinted, bool KitchenRequested, bool KitchenPrinted, string? Error)
{
    public static PrintResult Cancelled => new(false, false, false, null);
    public static PrintResult Failed(string message) => new(false, false, false, message);
    /// Bill printed but a requested kitchen ticket did not.
    public bool KitchenFailed => BillPrinted && KitchenRequested && !KitchenPrinted;
}

public static class ReceiptPrinter
{
    // Sans-serif stack: Segoe UI ships with every Windows install, Roboto/Arial
    // fall back if the user has them. A proportional sans-serif renders cleaner
    // on 80mm thermal printers than monospace (Consolas was clipping).
    private static readonly FontFamily ReceiptFont =
        new FontFamily("Segoe UI, Roboto, Arial, sans-serif");

    /// Print to the saved default printer WITHOUT blocking the UI thread.
    /// The bill (and optional kitchen ticket) are spooled on a background STA
    /// thread so a slow / offline thermal printer can never freeze the POS.
    /// onDone is always invoked back on the UI thread with the outcome.
    /// printKitchen=true also fires the kitchen ticket (order time only).
    public static void PrintQuietAsync(Order order, bool printKitchen, Action<PrintResult> onDone)
    {
        var s = AppSettings.Current;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;

        void Deliver(PrintResult r)
        {
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(() => onDone(r)));
            else
                onDone(r);
        }

        // No saved printer yet → the chooser is modal UI and must run on the UI
        // thread. Rare (first run only); do it inline.
        if (string.IsNullOrWhiteSpace(s.DefaultPrinterName))
        {
            Deliver(PrintWithDialog(order, printKitchen));
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                var queue = TryFindSavedQueue(s.DefaultPrinterName);
                if (queue == null)
                {
                    // Saved printer vanished — fall back to the chooser on the UI thread.
                    if (dispatcher != null)
                        dispatcher.BeginInvoke(new Action(() => onDone(PrintWithDialog(order, printKitchen))));
                    else
                        onDone(PrintWithDialog(order, printKitchen));
                    return;
                }
                var dlg = new System.Windows.Controls.PrintDialog { PrintQueue = queue };
                Deliver(PrintCore(order, printKitchen, dlg));
            }
            catch (Exception ex)
            {
                RestaurantOrder.App.Log("Receipt print", ex);
                Deliver(PrintResult.Failed(ex.Message));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    /// Synchronous print to the saved default printer; prompts only if none is
    /// saved or it's gone. Used by occasional user-initiated prints (reprint,
    /// test print) where blocking briefly is acceptable.
    public static PrintResult PrintQuiet(Order order, bool printKitchen = false)
    {
        var s = AppSettings.Current;
        var dlg = new System.Windows.Controls.PrintDialog();
        var queue = TryFindSavedQueue(s.DefaultPrinterName);
        if (queue == null)
        {
            if (dlg.ShowDialog() != true) return PrintResult.Cancelled;
            s.DefaultPrinterName = dlg.PrintQueue?.FullName ?? "";
            try { s.Save(); } catch { }
            if (dlg.PrintQueue == null) return PrintResult.Cancelled;
        }
        else
        {
            dlg.PrintQueue = queue;
        }
        return PrintCore(order, printKitchen, dlg);
    }

    /// Always show the printer chooser (used for "change printer" or first-run flow).
    public static PrintResult PrintWithDialog(Order order, bool printKitchen = false)
    {
        var s = AppSettings.Current;
        var dlg = new System.Windows.Controls.PrintDialog();
        if (dlg.ShowDialog() != true) return PrintResult.Cancelled;

        s.DefaultPrinterName = dlg.PrintQueue?.FullName ?? s.DefaultPrinterName;
        try { s.Save(); } catch { }
        return PrintCore(order, printKitchen, dlg);
    }

    // Spools the bill on whatever thread we're on (must be STA), then the
    // optional kitchen ticket. Builds documents from order data only (no UI
    // resources) so it is safe on a background STA thread.
    private static PrintResult PrintCore(Order order, bool printKitchen, System.Windows.Controls.PrintDialog dlg)
    {
        var width = ResolveReceiptWidth(dlg.PrintableAreaWidth);
        var doc = BuildDocument(order, width, AppSettings.Current.CompactReceipt);
        dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, $"Order #{order.Id:D5}");

        bool kitchenPrinted = false;
        if (printKitchen) kitchenPrinted = TryPrintKitchen(dlg, order, width);
        return new PrintResult(BillPrinted: true, KitchenRequested: printKitchen, KitchenPrinted: kitchenPrinted, Error: null);
    }

    private static bool TryPrintKitchen(System.Windows.Controls.PrintDialog dlg, Order order, double width)
    {
        try
        {
            var kitchen = BuildKitchenTicket(order, width);
            dlg.PrintDocument(((IDocumentPaginatorSource)kitchen).DocumentPaginator, $"Kitchen #{order.Id:D5}");
            return true;
        }
        catch (Exception ex)
        {
            // No longer silent: a missing kitchen ticket means food doesn't get
            // cooked, so leave a breadcrumb. The caller surfaces KitchenFailed.
            RestaurantOrder.App.Log("Kitchen ticket print", ex);
            return false;
        }
    }

    public static FlowDocument BuildPreview(Order order, double width = 320)
        => BuildDocument(order, width, AppSettings.Current.CompactReceipt);

    // Thermal POS printers (80mm) usually have ~72mm printable width.
    // Some drivers report a much wider PrintableAreaWidth (e.g., 595 for A4
    // fallback) which causes layout to overflow paper and clip text. Cap at
    // a sane thermal width so the receipt always fits on 80mm rolls.
    private static double ResolveReceiptWidth(double reported)
    {
        const double thermalCap = 288; // ~72mm @ 96 DPI
        if (reported <= 0) return thermalCap;
        return Math.Min(reported, thermalCap);
    }

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
        double bodySize = compact ? 11 : 12;
        double headerSize = compact ? 14 : 17;
        double subSize = compact ? 10 : 11;
        double totalSize = compact ? 13 : 15;
        double padX = compact ? 6 : 12;
        double padY = compact ? 6 : 12;

        var doc = new FlowDocument
        {
            FontFamily = ReceiptFont,
            FontSize = bodySize,
            PagePadding = new Thickness(padX, padY, padX, padY),
            ColumnGap = 0,
            ColumnWidth = double.PositiveInfinity,
            PageWidth = pageWidth > 0 ? pageWidth : 288,
            TextAlignment = TextAlignment.Left,
            Foreground = Brushes.Black,
            LineHeight = compact ? 14 : 17,
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

        // Big, centred order number — readable across the counter so the
        // cashier can call out '# 00012 ready' from the bill.
        var idLine = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 22,
            Margin = new Thickness(0, 4, 0, 2)
        };
        idLine.Inlines.Add(new Run($"# {order.Id:D5}"));
        doc.Blocks.Add(idLine);

        // Parcel banner — bold, centred, easy to spot when bagging
        if (order.IsParcel)
        {
            var parcel = new Paragraph(new Run("*** PARCEL ***"))
            {
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontSize = totalSize,
                Margin = new Thickness(0, 2, 0, 2)
            };
            doc.Blocks.Add(parcel);
        }

        doc.Blocks.Add(MakeRule(compact));

        // Compact meta row: date + payment (order # is now in the big header above)
        var meta = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, compact ? 2 : 4),
            FontSize = subSize,
            TextAlignment = TextAlignment.Center
        };
        meta.Inlines.Add(new Run($"{order.CreatedAt:dd-MMM-yy HH:mm}   ·   {order.PaymentMethod}"));
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
        table.Columns.Add(new TableColumn { Width = new GridLength(2.4, GridUnitType.Star) });
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
            AddTotalLine(doc, "Discount", "- " + Money.Format(order.DiscountAmount), subSize, FontWeights.Normal);
        if (order.RoundingAmount != 0m)
            AddTotalLine(doc, "Rounding",
                (order.RoundingAmount >= 0 ? "+ " : "- ") + Money.Format(Math.Abs(order.RoundingAmount)),
                subSize, FontWeights.Normal);

        doc.Blocks.Add(MakeRule(compact));

        var totals = new Paragraph
        {
            FontSize = totalSize,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0)
        };
        // Print the stored Total verbatim for any order that has a breakdown
        // (Subtotal>0), so a legitimately-zero total (e.g. a 100% comp) prints as
        // 0 rather than falling back to the gross subtotal. Only truly legacy rows
        // (no stored Subtotal) fall back to the recomputed line sum.
        totals.Inlines.Add(new Run($"Items: {totalQty}    Total: {Money.Format(order.Subtotal > 0m ? order.Total : total)}"));
        doc.Blocks.Add(totals);

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            doc.Blocks.Add(new Paragraph(new Run("Notes: " + order.Notes)) { FontSize = subSize, Margin = new Thickness(0, compact ? 2 : 4, 0, 0) });
        }

        doc.Blocks.Add(MakeRule(compact));
        var footer = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            FontSize = subSize,
            Margin = new Thickness(0, 4, 0, 0)
        };
        footer.Inlines.Add(new Run("Thank you!"));
        doc.Blocks.Add(footer);

        return doc;
    }

    /// Kitchen ticket: order id + parcel banner + item names with quantity. No prices.
    public static FlowDocument BuildKitchenTicket(Order order, double pageWidth)
    {
        double bodySize = 14;
        double padX = 8;
        double padY = 8;

        var doc = new FlowDocument
        {
            FontFamily = ReceiptFont,
            FontSize = bodySize,
            PagePadding = new Thickness(padX, padY, padX, padY),
            ColumnGap = 0,
            ColumnWidth = double.PositiveInfinity,
            PageWidth = pageWidth > 0 ? pageWidth : 288,
            TextAlignment = TextAlignment.Left,
            Foreground = Brushes.Black,
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };

        // Big "ORDER #00012" header — the kitchen reads this from across the
        // counter to call out a ready order.
        var head = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = Brushes.Black
        };
        head.Inlines.Add(new Run("KITCHEN COPY"));
        doc.Blocks.Add(head);

        var idLine = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            FontWeight = FontWeights.Bold,
            FontSize = 26,
            Margin = new Thickness(0, 2, 0, 2)
        };
        idLine.Inlines.Add(new Run($"# {order.Id:D5}"));
        doc.Blocks.Add(idLine);

        if (order.IsParcel)
        {
            var parcel = new Paragraph(new Run("*** PARCEL ***"))
            {
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(0, 2, 0, 2)
            };
            doc.Blocks.Add(parcel);
        }

        var meta = new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            FontSize = bodySize,
            Margin = new Thickness(0, 0, 0, 4)
        };
        meta.Inlines.Add(new Run($"{order.CreatedAt:dd-MMM HH:mm}"));
        if (!string.IsNullOrWhiteSpace(order.CustomerName))
            meta.Inlines.Add(new Run($"   {order.CustomerName}"));
        doc.Blocks.Add(meta);

        doc.Blocks.Add(MakeRule(false));

        // Items table: qty + name only.
        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0),
            FontSize = bodySize
        };
        table.Columns.Add(new TableColumn { Width = new GridLength(0.5, GridUnitType.Star) });
        table.Columns.Add(new TableColumn { Width = new GridLength(2.5, GridUnitType.Star) });

        var rg = new TableRowGroup();
        foreach (var it in order.Items)
        {
            var row = new TableRow();
            var qtyCell = MakeCell(it.Quantity.ToString() + " ×", FontWeights.Bold, compact: false, alignment: TextAlignment.Right);
            var nameCell = MakeCell(it.MenuItemName, FontWeights.SemiBold, compact: false);
            row.Cells.Add(qtyCell);
            row.Cells.Add(nameCell);
            rg.Rows.Add(row);
        }
        table.RowGroups.Add(rg);
        doc.Blocks.Add(table);

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            doc.Blocks.Add(MakeRule(false));
            doc.Blocks.Add(new Paragraph(new Run("Notes: " + order.Notes))
            {
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0)
            });
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
        p.Inlines.Add(new Run("    "));
        p.Inlines.Add(new Run(value) { FontWeight = weight });
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
