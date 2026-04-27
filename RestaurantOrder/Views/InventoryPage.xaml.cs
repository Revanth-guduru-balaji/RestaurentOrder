using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using RestaurantOrder.Data;
using RestaurantOrder.Services;
using MenuItem = RestaurantOrder.Data.MenuItem;

namespace RestaurantOrder.Views;

public partial class InventoryPage : UserControl
{
    private List<MenuItem> _all = new();
    private readonly ObservableCollection<MenuItem> _view = new();

    public InventoryPage()
    {
        InitializeComponent();
        ItemsGrid.ItemsSource = _view;
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        _all = MenuRepository.GetAll();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        _view.Clear();
        var s = (SearchBox.Text ?? "").Trim();
        IEnumerable<MenuItem> q = _all;
        if (!string.IsNullOrEmpty(s))
            q = q.Where(i => i.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                          || (i.Category ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));
        foreach (var i in q) _view.Add(i);
        CountText.Text = $"{_view.Count} item{(_view.Count == 1 ? "" : "s")}";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void AddItem_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EditItemWindow(null) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            MenuRepository.Insert(dlg.Result);
            Refresh();
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is int id)
        {
            var item = _all.FirstOrDefault(x => x.Id == id);
            if (item == null) return;
            var ok = MessageBox.Show($"Delete '{item.Name}'? This cannot be undone.",
                "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (ok == MessageBoxResult.Yes)
            {
                MenuRepository.Delete(id);
                Refresh();
            }
        }
    }

    private void ItemsGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.Item is not MenuItem item) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { MenuRepository.Update(item); }
            catch (Exception ex)
            {
                MessageBox.Show("Update failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Refresh();
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ItemsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ItemsGrid.SelectedItem is not MenuItem item) return;
        var dlg = new EditItemWindow(item) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            MenuRepository.Update(dlg.Result);
            Refresh();
        }
    }

    private void DownloadSample_Click(object sender, RoutedEventArgs e)
    {
        var sfd = new SaveFileDialog
        {
            Filter = "Excel files|*.xlsx",
            FileName = "menu-sample.xlsx",
            Title = "Save sample template"
        };
        if (sfd.ShowDialog() == true)
        {
            try
            {
                ExcelService.WriteSampleTemplate(sfd.FileName);
                MessageBox.Show("Template saved.\nEdit the rows and use 'Import from Excel' to load.",
                    "Sample saved", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save template: " + ex.Message,
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "Excel files|*.xlsx;*.xls", Title = "Import menu" };
        if (ofd.ShowDialog() != true) return;

        try
        {
            var result = ExcelService.ImportMenu(ofd.FileName);
            var msg = $"Imported / updated {result.Imported} item(s).";
            if (result.Skipped > 0)
                msg += $"\nSkipped {result.Skipped} row(s).";
            if (result.Errors.Count > 0)
                msg += "\n\nFirst issues:\n• " + string.Join("\n• ", result.Errors.Take(5));
            MessageBox.Show(msg, "Import complete", MessageBoxButton.OK, MessageBoxImage.Information);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Import failed: " + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
