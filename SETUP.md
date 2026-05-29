# Setup Guide

This guide covers two paths:

- **Section A** — building the standalone `.exe` from source (one-time, on the developer's machine).
- **Section B** — installing and using it on a restaurant PC (no developer tools needed).

---

## A. Build the standalone `.exe`

### A.1 Prerequisites

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) — pick **SDK** (not Runtime). Verify with:

  ```cmd
  dotnet --version
  ```

### A.2 Build

From a `cmd` or PowerShell window in the project root, run the bundled batch script:

```cmd
build.bat
```

Defaults to `win-x64`. For ARM64 Windows machines (Surface Pro X, Snapdragon laptops):

```cmd
build.bat win-arm64
```

When it finishes, the single self-contained executable is at:

```
dist\RestaurantOrder.exe
```

That one file is everything — ~170 MB, no .NET install required on the target machine.

### A.3 What the script does

`build.bat` is a thin wrapper around:

```
dotnet publish RestaurantOrder\RestaurantOrder.csproj ^
    -c Release -r <RID> --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=embedded
```

It then copies the resulting exe out of the `bin\` tree into `dist\` so you have a clean, predictable path to grab.

If you want to tweak flags (e.g. trimming, ReadyToRun), call `dotnet publish` directly — `build.bat` is just a convenience.

---

## B. Install on a restaurant PC

### B.1 Copy the exe

Copy `dist\RestaurantOrder.exe` to a permanent location. Either is fine:

- `C:\Program Files\RestaurantOrder\RestaurantOrder.exe` (needs admin to write there)
- `%LocalAppData%\Programs\RestaurantOrder\RestaurantOrder.exe` (no admin required)

Right-click the exe → **Pin to taskbar** (or **Pin to Start**) for one-click access.

### B.2 First run

Double-click the exe. On first launch the app:

1. Creates `%LocalAppData%\RestaurantOrder\restaurant.db` (your data file).
2. Seeds a demo menu (idly, dosa, vada, coffee, tea, meals…) so you can try ordering immediately.
3. Creates `%LocalAppData%\RestaurantOrder\settings.json` once you save Settings.

> **SmartScreen warning?** The first time you launch an unsigned exe, Windows may show "Windows protected your PC". Click **More info** → **Run anyway**. Code-signing avoids this entirely but is out of scope for this guide.

### B.3 Configure (Settings ⚙)

Click the **⚙ Settings** button in the sidebar:

| Field | What it does |
| --- | --- |
| Shop name | Top line on the printed receipt (large, bold). |
| Tag line / cuisine | Optional second line. |
| Phone / address | Optional third line, smaller font. |
| Default printer | Picked once — receipts then print silently. Leave blank to be prompted each time. |
| Auto-print | **On**: "Place & Print" sends straight to the saved printer. **Off**: a receipt preview opens first; click "Print Receipt" to send. |
| Compact receipt | Smaller text and tighter spacing — saves paper on thermal slip printers. |

Click **Test print** to send a sample receipt through the currently selected printer.

### B.4 Daily use

- **Take Order** — tap menu tiles to add to cart, set customer name and payment method, then **Place & Print**.
- **Inventory** — add / edit (in-grid) / delete items. Toggle **Available** to hide items from order taking without losing history.
  - **Download Sample Excel** writes a `menu-sample.xlsx` template.
  - **Import from Excel** does an upsert by Name (re-importing updates prices in place).
- **Order History** — filter by date range, search by order # or customer, click a row to see line items, **Reprint** any past receipt.
- **Dashboard** — KPIs and trend charts for Today / Week / Month / Year, vs the previous period.

### B.5 Excel import format

| Column | Meaning | Required |
|---|---|---|
| A — Name | Item name (used as upsert key) | ✓ |
| B — Category | e.g. Breakfast, Lunch, Beverages | optional |
| C — Price | Number, in ₹ | ✓ |
| D — Available | `Yes` / `No` / `1` / `0` (default Yes) | optional |

The header row is detected automatically and skipped.

---

## C. Backups

The whole database is one file:

```
%LocalAppData%\RestaurantOrder\restaurant.db
```

Copy this to a USB stick, OneDrive folder, or any backup location. To restore, replace the file with the app closed. To wipe and start fresh, delete it — the demo menu re-seeds on next launch.

`settings.json` next to it holds the receipt header and printer selection. Back that up too if you want a one-click restore on a new PC.

A simple nightly backup script:

```cmd
xcopy /Y "%LocalAppData%\RestaurantOrder\restaurant.db" "D:\Backups\RestaurantOrder\"
```

Schedule it with Task Scheduler.

---

## D. Updating

1. On the developer machine, pull latest changes and run `build.bat` again.
2. Copy the new `dist\RestaurantOrder.exe` over the old one on each restaurant PC (close the app first).

Your data is unaffected — it lives in `%LocalAppData%`, not next to the exe.

---

## E. Uninstall

1. Delete the exe.
2. Optionally delete `%LocalAppData%\RestaurantOrder\` to remove all data and settings.

There are no registry entries, services, or system-wide installs to clean up.

---

## F. Troubleshooting

| Symptom | Fix |
| --- | --- |
| "Windows protected your PC" SmartScreen warning | Click **More info** → **Run anyway** the first time. Code-sign the exe to remove this for good. |
| No printers listed in Settings | Install your printer's Windows driver first. The app shows whatever Windows reports. |
| Receipt cut off on a thermal printer | Enable **Compact receipt** in Settings and verify the paper width in the Windows printer properties. |
| "print failed: …" toast | The Windows print spooler rejected the job. Check the printer is online; click **Test print** in Settings to isolate the issue. |
| Excel import says "invalid price" | Column C must be a number. Currency formatting in Excel is fine; text like "Rs. 50" is not. |
| Lost orders after a crash | Restore `restaurant.db` from your last backup. Each order's stock deduction and order record are written in one SQLite transaction (WAL mode), so a crash leaves the database consistent — you either have the whole order or none of it. If the file itself is ever found unreadable on launch, the app moves it aside as `restaurant.db.corrupt-…` and starts fresh, and tells you. |
| App won't start, no error window | Check `%LocalAppData%\RestaurantOrder\` is writable. Anti-virus quarantine can also block the unpacked single-file exe — whitelist it. |

---

## G. What's where

| Location | What it holds |
| --- | --- |
| `dist\RestaurantOrder.exe` | The thing you ship. |
| `%LocalAppData%\RestaurantOrder\restaurant.db` | All menu items, orders, order line-items. |
| `%LocalAppData%\RestaurantOrder\settings.json` | Shop name, default printer, auto-print / compact toggles. |

Source layout for the curious: see `README.md`.
