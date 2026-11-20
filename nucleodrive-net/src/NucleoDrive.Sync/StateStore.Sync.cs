using NucleoDrive.Sync.TwoWay;

namespace NucleoDrive.Sync;

/// <summary>Two-way sync baseline: per synced folder, what each item looked like on both sides at
/// the last successful sync.</summary>
public sealed partial class StateStore
{
    public Dictionary<string, BaseItem> LoadBaseline(string folder)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT path, is_dir, uid, rev, local_size, local_mtime, inode FROM sync_base WHERE folder = $f", ("$f", folder));
            using var r = cmd.ExecuteReader();
            var result = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            while (r.Read())
            {
                var path = r.GetString(0);
                result[path] = new BaseItem(path, r.GetInt64(1) != 0, r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6));
            }
            return result;
        }
    }

    public void UpsertBaseline(string folder, IEnumerable<BaseItem> items)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using var cmd = Command("""
                INSERT INTO sync_base (folder, path, is_dir, uid, rev, local_size, local_mtime, inode)
                VALUES ($f, $p, $d, $u, $r, $s, $m, $i)
                ON CONFLICT DO UPDATE SET is_dir = excluded.is_dir, uid = excluded.uid, rev = excluded.rev,
                    local_size = excluded.local_size, local_mtime = excluded.local_mtime, inode = excluded.inode
                """, ("$f", folder), ("$p", ""), ("$d", 0L), ("$u", ""), ("$r", ""), ("$s", 0L), ("$m", 0L), ("$i", 0L));
            cmd.Transaction = tx;
            foreach (var b in items)
            {
                cmd.Parameters["$p"].Value = b.Path;
                cmd.Parameters["$d"].Value = b.IsDir ? 1L : 0L;
                cmd.Parameters["$u"].Value = (object?)b.Uid ?? DBNull.Value;
                cmd.Parameters["$r"].Value = (object?)b.RevisionUid ?? DBNull.Value;
                cmd.Parameters["$s"].Value = b.LocalSize;
                cmd.Parameters["$m"].Value = b.LocalMtime;
                cmd.Parameters["$i"].Value = b.Inode;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    /// <summary>Removes an item and (for folders) everything under it.</summary>
    public void RemoveBaseline(string folder, string path)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM sync_base WHERE folder = $f AND (path = $p OR substr(path, 1, length($p) + 1) = $p || '/')",
                ("$f", folder), ("$p", path));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Moves an item and everything under it to a new path (after a rename/move).</summary>
    public void RekeyBaseline(string folder, string from, string to)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                UPDATE sync_base SET path = $to || substr(path, length($from) + 1)
                WHERE folder = $f AND (path = $from OR substr(path, 1, length($from) + 1) = $from || '/')
                """, ("$f", folder), ("$from", from), ("$to", to));
            cmd.ExecuteNonQuery();
        }
    }

    public void ClearBaseline(string folder)
    {
        lock (_gate)
        {
            using var cmd = Command("DELETE FROM sync_base WHERE folder = $f", ("$f", folder));
            cmd.ExecuteNonQuery();
        }
    }
}
