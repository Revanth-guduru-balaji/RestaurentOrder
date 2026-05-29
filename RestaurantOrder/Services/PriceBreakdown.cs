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
        => Compute(subtotal, discount, AppSettings.Current);

    /// Settings-injected overload — pure and unit-testable without the static singleton.
    public static PriceBreakdown Compute(decimal subtotal, decimal discount, AppSettings s)
    {
        decimal tax = subtotal * (s.TaxPercent / 100m);

        // Never discount more than the bill, and never below zero. Storing the
        // EFFECTIVE discount keeps the additive invariant intact:
        //   Total == Subtotal + Tax − Discount + Rounding
        decimal effectiveDiscount = discount < 0 ? 0m : Math.Min(discount, subtotal + tax);
        decimal afterTaxDisc = subtotal + tax - effectiveDiscount;
        if (afterTaxDisc < 0) afterTaxDisc = 0;

        decimal total = afterTaxDisc;
        decimal rounding = 0m;
        if (s.RoundToNearestRupee)
        {
            total = Math.Round(afterTaxDisc, 0, MidpointRounding.AwayFromZero);
            rounding = total - afterTaxDisc;
        }

        // One consistent rounding policy (half-up / away-from-zero) for every
        // money component — Indian retail expects half-up, not banker's rounding.
        return new PriceBreakdown
        {
            Subtotal = decimal.Round(subtotal, 2, MidpointRounding.AwayFromZero),
            TaxAmount = decimal.Round(tax, 2, MidpointRounding.AwayFromZero),
            DiscountAmount = decimal.Round(effectiveDiscount, 2, MidpointRounding.AwayFromZero),
            RoundingAmount = decimal.Round(rounding, 2, MidpointRounding.AwayFromZero),
            Total = decimal.Round(total, 2, MidpointRounding.AwayFromZero),
        };
    }
}
