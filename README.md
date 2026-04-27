# Araya Vysya SSV — Restaurant Manager

A polished offline Windows desktop app for a single-location restaurant. Inventory, order taking with printable receipts, and a daily/weekly/monthly analytics dashboard. No internet required.

## Stack

- **WPF on .NET 8** — single self-contained `.exe`, native Windows look
- **SQLite** (via `Microsoft.Data.Sqlite`) — local file database, no server
- **ClosedXML** — Excel import without needing Microsoft Office installed
- **ModernWpfUI** — Fluent-style modern controls
- Hand-drawn WPF charts (no chart-library dependency)

## How to run

```powershell
# Restore + build
dotnet build .\RestaurantOrder\RestaurantOrder.csproj -c Release

# Run
dotnet run --project .\RestaurantOrder\RestaurantOrder.csproj -c Release
```

Or open `RestaurantOrder.slnx` in Visual Studio 2022+ and press **F5**.

The app stores its database at:

```
%LocalAppData%\RestaurantOrder\restaurant.db
```

A sample menu (idly, dosa, vada, coffee, tea, meals…) is seeded on first run so you can try ordering immediately.

## Build a self-contained `.exe` for distribution

The simplest path is the bundled batch script:

```cmd
build.bat
```

It produces `dist\RestaurantOrder.exe` — a single self-contained file (~170 MB) that runs on any Windows 10/11 machine without a .NET install. Pass `win-arm64` as the first argument to target ARM64 instead of x64.

For end-to-end install / configure / backup / troubleshooting steps, see [`SETUP.md`](SETUP.md).

## Features

### 📊 Dashboard
- KPIs: revenue, orders, average ticket, available menu items
- Period selector: Today / Week / Month / Year (vs previous period)
- Revenue trend (hourly for Today, daily for Week/Month, monthly for Year)
- Top items, orders-by-hour, category breakdown bars

### 🛒 Take Order
- Browse menu by category, search by name
- Tap tile → adds to cart (tap again to bump qty, or use ± buttons)
- Customer name (optional), payment method (Cash / UPI / Card)
- **Place & Print** opens a receipt preview, then prints to any installed Windows printer (works with thermal slip printers and standard A4 printers)

### 📋 Inventory
- Add, edit (in-grid), delete menu items
- Mark items unavailable to hide them from order taking without losing history
- **Download Sample Excel** writes a `menu-sample.xlsx` template
- **Import from Excel** does an upsert by Name (so re-importing updates prices)

### 📜 Order History
- Filter by date range; quick-pick Today / Week / Month buttons
- Search by order # or customer name
- Click a row to see line-item details on the right
- **Reprint** any past receipt

## Excel import format

| Column | Meaning | Required |
|---|---|---|
| A — Name | Item name (used as upsert key) | ✓ |
| B — Category | e.g. Breakfast, Lunch, Beverages | optional |
| C — Price | Number, in ₹ | ✓ |
| D — Available | `Yes` / `No` / `1` / `0` (default Yes) | optional |

The header row is detected automatically and skipped.

## Project layout

```
RestaurantOrder/
├── App.xaml(.cs)              Application entry, theme registration, DB init
├── MainWindow.xaml(.cs)       Sidebar nav + content host
├── Styles/AppStyles.xaml      Brand palette, card/button/tile styles
├── Controls/
│   ├── BarChart.cs            Hand-drawn bar chart
│   └── LineChart.cs           Hand-drawn line/area chart
├── Data/
│   ├── Models.cs              MenuItem, Order, OrderItem
│   ├── Database.cs            SQLite init + seeding
│   ├── MenuRepository.cs      CRUD + bulk upsert by name
│   ├── OrderRepository.cs     Create / list / detail
│   └── AnalyticsRepository.cs Aggregations powering the dashboard
├── Services/
│   ├── ExcelService.cs        ClosedXML import + sample writer
│   └── ReceiptPrinter.cs      FlowDocument receipt + PrintDialog
└── Views/
    ├── DashboardPage.xaml(.cs)
    ├── OrderPage.xaml(.cs)
    ├── InventoryPage.xaml(.cs)
    ├── OrderHistoryPage.xaml(.cs)
    ├── ConfirmReceiptWindow.xaml(.cs)
    └── EditItemWindow.xaml(.cs)
```

## Customising the receipt header

Open **Settings** (gear icon in the sidebar) to set the shop name, tag line, address, default printer, compact-receipt mode, and whether to auto-print on order placement. Values are persisted to `%LocalAppData%\RestaurantOrder\settings.json`.
