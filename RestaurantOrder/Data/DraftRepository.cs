using System;
using System.Collections.Generic;
using System.Globalization;

namespace RestaurantOrder.Data;

public static class DraftRepository
{
    private const string Iso = "yyyy-MM-dd HH:mm:ss";

    public static List<Draft> GetAll()
    {
        using var conn = Database.Open();
        var drafts = new List<Draft>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT Id, CreatedAt, UpdatedAt, CustomerName, PaymentMethod,
                                       DiscountAmount, IsParcel, Notes
                                FROM Drafts ORDER BY CreatedAt ASC";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                drafts.Add(new Draft
                {
                    Id = rdr.GetInt32(0),
                    CreatedAt = DateTime.ParseExact(rdr.GetString(1), Iso, CultureInfo.InvariantCulture),
                    UpdatedAt = DateTime.ParseExact(rdr.GetString(2), Iso, CultureInfo.InvariantCulture),
                    CustomerName = rdr.IsDBNull(3) ? null : rdr.GetString(3),
                    PaymentMethod = rdr.GetString(4),
                    DiscountAmount = Database.ReadMoney(rdr.GetDouble(5)),
                    IsParcel = rdr.GetInt32(6) == 1,
                    Notes = rdr.IsDBNull(7) ? null : rdr.GetString(7),
                });
            }
        }

        // Hydrate items in a single follow-up query.
        if (drafts.Count == 0) return drafts;
        var byId = new Dictionary<int, Draft>(drafts.Count);
        foreach (var d in drafts) byId[d.Id] = d;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT Id, DraftId, MenuItemId, MenuItemName, UnitPrice, Quantity
                                FROM DraftItems ORDER BY DraftId, Id";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read())
            {
                var draftId = rdr.GetInt32(1);
                if (!byId.TryGetValue(draftId, out var draft)) continue;
                draft.Items.Add(new DraftItem
                {
                    Id = rdr.GetInt32(0),
                    DraftId = draftId,
                    MenuItemId = rdr.GetInt32(2),
                    MenuItemName = rdr.GetString(3),
                    UnitPrice = Database.ReadMoney(rdr.GetDouble(4)),
                    Quantity = rdr.GetInt32(5),
                });
            }
        }
        return drafts;
    }

    public static int Insert(Draft draft)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO Drafts
                                (CreatedAt, UpdatedAt, CustomerName, PaymentMethod,
                                 DiscountAmount, IsParcel, Notes)
                                VALUES ($c, $u, $cn, $pm, $disc, $pa, $nt);
                                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$c", draft.CreatedAt.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$u", draft.UpdatedAt.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$cn", (object?)draft.CustomerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm", draft.PaymentMethod ?? "Cash");
            cmd.Parameters.AddWithValue("$disc", (double)draft.DiscountAmount);
            cmd.Parameters.AddWithValue("$pa", draft.IsParcel ? 1 : 0);
            cmd.Parameters.AddWithValue("$nt", (object?)draft.Notes ?? DBNull.Value);
            draft.Id = Convert.ToInt32((long)(cmd.ExecuteScalar() ?? 0L));
        }

        InsertItems(conn, draft);
        tx.Commit();
        return draft.Id;
    }

    /// Replaces the header + the entire item list. Cheap and avoids the
    /// complexity of diffing per-row changes — drafts are tiny.
    public static void Update(Draft draft)
    {
        if (draft.Id <= 0) { Insert(draft); return; }
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"UPDATE Drafts
                                SET UpdatedAt = $u, CustomerName = $cn, PaymentMethod = $pm,
                                    DiscountAmount = $disc, IsParcel = $pa, Notes = $nt
                                WHERE Id = $id";
            cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString(Iso, CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$cn", (object?)draft.CustomerName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pm", draft.PaymentMethod ?? "Cash");
            cmd.Parameters.AddWithValue("$disc", (double)draft.DiscountAmount);
            cmd.Parameters.AddWithValue("$pa", draft.IsParcel ? 1 : 0);
            cmd.Parameters.AddWithValue("$nt", (object?)draft.Notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", draft.Id);
            cmd.ExecuteNonQuery();
        }

        using (var del = conn.CreateCommand())
        {
            del.CommandText = "DELETE FROM DraftItems WHERE DraftId = $id";
            del.Parameters.AddWithValue("$id", draft.Id);
            del.ExecuteNonQuery();
        }

        InsertItems(conn, draft);
        tx.Commit();
    }

    public static void Delete(int draftId)
    {
        using var conn = Database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Drafts WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", draftId);
        cmd.ExecuteNonQuery();
    }

    private static void InsertItems(Microsoft.Data.Sqlite.SqliteConnection conn, Draft draft)
    {
        foreach (var it in draft.Items)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO DraftItems (DraftId, MenuItemId, MenuItemName, UnitPrice, Quantity)
                                VALUES ($d, $mi, $mn, $up, $q)";
            cmd.Parameters.AddWithValue("$d", draft.Id);
            cmd.Parameters.AddWithValue("$mi", it.MenuItemId);
            cmd.Parameters.AddWithValue("$mn", it.MenuItemName);
            cmd.Parameters.AddWithValue("$up", (double)it.UnitPrice);
            cmd.Parameters.AddWithValue("$q", it.Quantity);
            cmd.ExecuteNonQuery();
        }
    }
}
