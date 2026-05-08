using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RestaurantOrder.Data;

public class MenuItem : INotifyPropertyChanged
{
    private string _name = string.Empty;
    private string _category = string.Empty;
    private decimal _price;
    private bool _isAvailable = true;
    private int _estimatedAvailableQty;
    private int _availableQty;

    public int Id { get; set; }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(); } }
    }

    public string Category
    {
        get => _category;
        set { if (_category != value) { _category = value; OnPropertyChanged(); } }
    }

    public decimal Price
    {
        get => _price;
        set { if (_price != value) { _price = value; OnPropertyChanged(); } }
    }

    public bool IsAvailable
    {
        get => _isAvailable;
        set { if (_isAvailable != value) { _isAvailable = value; OnPropertyChanged(); } }
    }

    // Estimated qty available per day. 0 = stock not tracked (unlimited).
    public int EstimatedAvailableQty
    {
        get => _estimatedAvailableQty;
        set { if (_estimatedAvailableQty != value) { _estimatedAvailableQty = value; OnPropertyChanged(); } }
    }

    // Current qty available — decreases as orders are placed, resets daily from EstimatedAvailableQty.
    public int AvailableQty
    {
        get => _availableQty;
        set { if (_availableQty != value) { _availableQty = value; OnPropertyChanged(); } }
    }

    public bool TracksStock => EstimatedAvailableQty > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class Order
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public decimal Subtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal RoundingAmount { get; set; }
    public decimal Total { get; set; }
    public string PaymentMethod { get; set; } = "Cash";
    public string? CustomerName { get; set; }
    public string? Notes { get; set; }
    public bool IsVoided { get; set; }
    public bool IsParcel { get; set; }
    public List<OrderItem> Items { get; set; } = new();

    // Populated by GetBetween via aggregate join — used for list rendering
    // without round-tripping per row.
    public int LineCount { get; set; }
    public int PieceCount { get; set; }
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int MenuItemId { get; set; }
    public string MenuItemName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal LineTotal => UnitPrice * Quantity;
}
