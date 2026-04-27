using System.Globalization;

namespace RestaurantOrder.Services;

public static class Money
{
    private static readonly CultureInfo InCulture = CultureInfo.GetCultureInfo("en-IN");
    public const string Symbol = "₹"; // ₹

    public static string Format(decimal amount, bool withSymbol = true, bool withDecimals = true)
    {
        var fmt = withDecimals ? "N2" : "N0";
        var s = amount.ToString(fmt, InCulture);
        return withSymbol ? Symbol + " " + s : s;
    }

    public static string Format(double amount, bool withSymbol = true, bool withDecimals = true)
        => Format((decimal)amount, withSymbol, withDecimals);
}
