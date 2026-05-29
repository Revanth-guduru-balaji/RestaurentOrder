using RestaurantOrder.Data;
using RestaurantOrder.Services;
using Xunit;

namespace RestaurantOrder.Tests;

// Keep the static Database (single global connection string) from being shared
// across test classes running in parallel.
[CollectionDefinition("Serial", DisableParallelization = true)]
public class SerialCollection { }

[Collection("Serial")]
public class PriceBreakdownTests
{
    private static AppSettings Settings(decimal tax = 0m, bool round = true)
        => new() { TaxPercent = tax, RoundToNearestRupee = round };

    [Fact]
    public void Plain_subtotal_passes_through_when_no_tax_discount_or_rounding()
    {
        var bd = PriceBreakdown.Compute(120m, 0m, Settings(round: false));
        Assert.Equal(120m, bd.Subtotal);
        Assert.Equal(0m, bd.TaxAmount);
        Assert.Equal(0m, bd.DiscountAmount);
        Assert.Equal(0m, bd.RoundingAmount);
        Assert.Equal(120m, bd.Total);
    }

    [Fact]
    public void Total_never_goes_negative_and_discount_is_clamped_to_the_bill()
    {
        var bd = PriceBreakdown.Compute(100m, 500m, Settings());
        Assert.Equal(0m, bd.Total);
        Assert.Equal(100m, bd.DiscountAmount); // clamped to subtotal + tax
        // Additive invariant must hold on the stored row.
        Assert.Equal(bd.Total, bd.Subtotal + bd.TaxAmount - bd.DiscountAmount + bd.RoundingAmount);
    }

    [Fact]
    public void Tax_uses_half_up_rounding_not_bankers()
    {
        // 5% of 100.50 = 5.0250 -> half-up = 5.03 (banker's would give 5.02)
        var bd = PriceBreakdown.Compute(100.50m, 0m, Settings(tax: 5m, round: false));
        Assert.Equal(5.03m, bd.TaxAmount);
        Assert.Equal(105.53m, bd.Total);
        Assert.Equal(bd.Total, bd.Subtotal + bd.TaxAmount - bd.DiscountAmount + bd.RoundingAmount);
    }

    [Fact]
    public void Round_to_nearest_rupee_records_the_delta()
    {
        var bd = PriceBreakdown.Compute(99.40m, 0m, Settings());
        Assert.Equal(99m, bd.Total);
        Assert.Equal(-0.40m, bd.RoundingAmount);
        Assert.Equal(bd.Total, bd.Subtotal + bd.TaxAmount - bd.DiscountAmount + bd.RoundingAmount);
    }

    [Fact]
    public void Negative_discount_is_treated_as_zero()
    {
        var bd = PriceBreakdown.Compute(50m, -10m, Settings());
        Assert.Equal(0m, bd.DiscountAmount);
        Assert.Equal(50m, bd.Total);
    }

    [Fact]
    public void ReadMoney_strips_floating_point_drift()
    {
        Assert.Equal(90.00m, Database.ReadMoney(90.00000000000001));
        Assert.Equal(12.35m, Database.ReadMoney(12.345));
    }
}
