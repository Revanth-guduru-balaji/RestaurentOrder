using System;

namespace RestaurantOrder.Services;

/// <summary>
/// Computes Subtotal → Tax → Discount → Rounding → Total from a line subtotal,
/// using the current AppSettings (tax %, rounding mode, discount).
/// </summary>
public readonly struct PriceBreakdown
{
    public decimal Subtotal { get; init; }
    public decimal TaxAmount { get; init; }
    public decimal DiscountAmount { get; init; }
    public decimal RoundingAmount { get; init; }
    public decimal Total { get; init; }

    public static PriceBreakdown Compute(decimal subtotal, decimal discount = 0)
    {
        var s = AppSettings.Current;
        decimal tax = subtotal * (s.TaxPercent / 100m);
        decimal afterTaxDisc = subtotal + tax - discount;
        if (afterTaxDisc < 0) afterTaxDisc = 0;

        decimal total = afterTaxDisc;
        decimal rounding = 0m;
        if (s.RoundToNearestRupee)
        {
            total = Math.Round(afterTaxDisc, 0, MidpointRounding.AwayFromZero);
            rounding = total - afterTaxDisc;
        }

        return new PriceBreakdown
        {
            Subtotal = subtotal,
            TaxAmount = decimal.Round(tax, 2),
            DiscountAmount = decimal.Round(discount, 2),
            RoundingAmount = decimal.Round(rounding, 2),
            Total = decimal.Round(total, 2),
        };
    }
}
