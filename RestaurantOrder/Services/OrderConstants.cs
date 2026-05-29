using System;

namespace RestaurantOrder.Services;

/// Canonical payment-method values, persisted verbatim. Centralized so the
/// strings aren't re-typed (and mis-cased) across views and repositories.
public static class PaymentMethods
{
    public const string Cash = "Cash";
    public const string Upi = "UPI";
    public const string Card = "Card";

    public static readonly string[] All = { Cash, Upi, Card };

    /// Map any stored/typed value back to a canonical method (defaults to Cash).
    public static string Normalize(string? method)
    {
        if (string.Equals(method, Upi, StringComparison.OrdinalIgnoreCase)) return Upi;
        if (string.Equals(method, Card, StringComparison.OrdinalIgnoreCase)) return Card;
        return Cash;
    }
}

/// Order channel labels (walk-in vs parcel) shared across views.
public static class OrderChannels
{
    public const string WalkIn = "Walk-in";
    public const string Parcel = "Parcel";
}
