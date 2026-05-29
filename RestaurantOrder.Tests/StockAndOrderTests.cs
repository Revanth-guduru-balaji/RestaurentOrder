using System;
using System.IO;
using System.Linq;
using RestaurantOrder.Data;
using Xunit;

namespace RestaurantOrder.Tests;

[Collection("Serial")]
public class StockAndOrderTests : IDisposable
{
    private readonly string _dir;

    public StockAndOrderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ROTest_" + Path.GetRandomFileName());
        Database.Initialize(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static int InsertTracked(string name, decimal price, int qty)
        => MenuRepository.Insert(new MenuItem
        {
            Name = name,
            Price = price,
            EstimatedAvailableQty = qty,
            IsAvailable = true
        });

    private static Order OrderFor(int menuId, string name, decimal price, int qty)
    {
        var o = new Order { CreatedAt = DateTime.Now, PaymentMethod = "Cash" };
        o.Items.Add(new OrderItem { MenuItemId = menuId, MenuItemName = name, UnitPrice = price, Quantity = qty });
        o.Subtotal = price * qty;
        o.Total = price * qty;
        return o;
    }

    [Fact]
    public void CreateWithStock_deducts_stock_and_records_the_order()
    {
        int id = InsertTracked("Dosa", 40m, 10);
        var order = OrderFor(id, "Dosa", 40m, 3);

        var outcome = OrderRepository.CreateWithStock(order, new[] { (id, 3) });

        Assert.True(outcome.Ok);
        Assert.True(order.Id > 0);
        Assert.Equal(7, MenuRepository.GetById(id)!.AvailableQty);
    }

    [Fact]
    public void CreateWithStock_rejects_when_short_and_changes_nothing()
    {
        int id = InsertTracked("Vada", 30m, 2);
        var order = OrderFor(id, "Vada", 30m, 5);

        var outcome = OrderRepository.CreateWithStock(order, new[] { (id, 5) });

        Assert.False(outcome.Ok);
        Assert.Equal("Vada", outcome.ShortItemName);
        Assert.Equal(2, MenuRepository.GetById(id)!.AvailableQty);          // stock untouched
        Assert.Empty(OrderRepository.GetBetween(DateTime.Today, DateTime.Today.AddDays(1))); // no order recorded
    }

    [Fact]
    public void Untracked_items_sell_without_a_stock_check()
    {
        int id = MenuRepository.Insert(new MenuItem { Name = "Water", Price = 10m, EstimatedAvailableQty = 0, IsAvailable = true });
        var order = OrderFor(id, "Water", 10m, 999);

        var outcome = OrderRepository.CreateWithStock(order, new[] { (id, 999) });

        Assert.True(outcome.Ok);
        Assert.Equal(0, MenuRepository.GetById(id)!.AvailableQty); // untracked stays 0
    }

    [Fact]
    public void Voiding_restores_stock_but_never_above_the_daily_estimate()
    {
        int id = InsertTracked("Idly", 10m, 20);
        var order = OrderFor(id, "Idly", 10m, 5);
        OrderRepository.CreateWithStock(order, new[] { (id, 5) });
        Assert.Equal(15, MenuRepository.GetById(id)!.AvailableQty);

        // Simulate the overnight daily reset having refilled AvailableQty.
        var mi = MenuRepository.GetById(id)!;
        mi.AvailableQty = 20;
        MenuRepository.Update(mi);

        // Voiding the prior order must NOT push stock to 25 — clamp at the estimate.
        OrderRepository.SetVoided(order.Id, true);
        Assert.Equal(20, MenuRepository.GetById(id)!.AvailableQty);
    }

    [Fact]
    public void Voiding_is_idempotent()
    {
        int id = InsertTracked("Pongal", 50m, 20);
        var order = OrderFor(id, "Pongal", 50m, 5);
        OrderRepository.CreateWithStock(order, new[] { (id, 5) }); // 15 left

        OrderRepository.SetVoided(order.Id, true);  // restore -> 20
        OrderRepository.SetVoided(order.Id, true);  // already voided -> no-op

        Assert.Equal(20, MenuRepository.GetById(id)!.AvailableQty); // not 25
    }

    [Fact]
    public void GetBetween_returns_correct_line_and_piece_counts()
    {
        int a = InsertTracked("Dosa", 40m, 50);
        int b = InsertTracked("Vada", 30m, 50);
        var order = new Order { CreatedAt = DateTime.Now, PaymentMethod = "Cash" };
        order.Items.Add(new OrderItem { MenuItemId = a, MenuItemName = "Dosa", UnitPrice = 40m, Quantity = 3 });
        order.Items.Add(new OrderItem { MenuItemId = b, MenuItemName = "Vada", UnitPrice = 30m, Quantity = 2 });
        order.Subtotal = 180m; order.Total = 180m;
        OrderRepository.CreateWithStock(order, new[] { (a, 3), (b, 2) });

        var rows = OrderRepository.GetBetween(DateTime.Today, DateTime.Today.AddDays(1));
        var row = rows.Single(o => o.Id == order.Id);
        Assert.Equal(2, row.LineCount);   // two distinct items
        Assert.Equal(5, row.PieceCount);  // 3 + 2 pieces
    }

    [Fact]
    public void BulkUpsertByName_updates_case_insensitively_instead_of_duplicating()
    {
        // A name that isn't part of the first-run seed.
        MenuRepository.Insert(new MenuItem { Name = "Special Lassi", Price = 15m, IsAvailable = true });

        MenuRepository.BulkUpsertByName(new[]
        {
            new MenuItem { Name = "special lassi", Price = 18m, IsAvailable = true }
        });

        var matches = MenuRepository.GetAll()
            .Where(i => string.Equals(i.Name, "Special Lassi", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Single(matches);              // not duplicated
        Assert.Equal(18m, matches[0].Price); // price updated
    }
}
