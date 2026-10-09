using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace NivoraTestDatabaseTool;

/// <summary>
/// Per-section catalog digest for the final report (RV10): two consecutive
/// rebuilds must print nine equal lines. Every statement is a constant,
/// returns text columns only, and runs inside the report's one read-only
/// transaction. The vector version, role Ids and generated Ids are
/// deliberately not selected.
/// </summary>
internal static class CatalogDigest
{
    internal static readonly (string Section, string Sql)[] Sections =
    [
        ("tables", """
            SELECT table_name::text
            FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
            """),
        ("columns", """
            SELECT c.relname::text, a.attname::text, format_type(a.atttypid, a.atttypmod), a.attnotnull::text
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p') AND a.attnum > 0 AND NOT a.attisdropped
            """),
        ("defaults", """
            SELECT c.relname::text, a.attname::text, pg_get_expr(d.adbin, d.adrelid)
            FROM pg_attrdef d
            JOIN pg_class c ON c.oid = d.adrelid
            JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public'
            """),
        ("indexes", """
            SELECT tablename::text, indexname::text, indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
            """),
        ("constraints", """
            SELECT c.relname::text, con.conname::text, con.contype::text, pg_get_constraintdef(con.oid)
            FROM pg_constraint con
            JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND con.contype IN ('f', 'c', 'p', 'u', 'x')
            """),
        ("other objects", """
            SELECT 'sequence', c.relname::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'S'
            UNION ALL
            SELECT 'view', c.relname::text
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('v', 'm')
            UNION ALL
            SELECT 'trigger', c.relname::text || '.' || t.tgname::text
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND NOT t.tgisinternal
            UNION ALL
            SELECT 'schema', nspname::text
            FROM pg_namespace
            WHERE nspname NOT LIKE 'pg\_%' AND nspname <> 'information_schema'
            """),
        ("migration history", """
            SELECT "MigrationId"::text, "ProductVersion"::text
            FROM "__EFMigrationsHistory"
            """),
        ("extensions", """
            SELECT e.extname::text, n.nspname::text, r.rolname::text
            FROM pg_extension e
            JOIN pg_namespace n ON n.oid = e.extnamespace
            JOIN pg_roles r ON r.oid = e.extowner
            """),
        ("roles", """
            SELECT "Name"::text
            FROM "AspNetRoles"
            """),
    ];

    /// <summary>
    /// Runs the nine statements on an open connection that is already inside
    /// the report's read-only transaction; returns one line per section:
    /// "&lt;section&gt;: &lt;row count&gt;, &lt;first 12 hex characters&gt;".
    /// </summary>
    internal static async Task<List<string>> ComputeAsync(NpgsqlConnection connection)
    {
        var lines = new List<string>();
        foreach (var (section, sql) in Sections)
        {
            var rows = new List<string>();
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var fields = new string[reader.FieldCount];
                for (var i = 0; i < fields.Length; i++)
                {
                    fields[i] = reader.IsDBNull(i) ? "" : reader.GetString(i);
                }

                rows.Add(string.Join('|', fields));
            }

            lines.Add(Format(section, rows));
        }

        return lines;
    }

    /// <summary>Pure part of the digest: sort ordinally, join with LF, SHA-256 of the UTF-8 bytes.</summary>
    internal static string Format(string section, List<string> rows)
    {
        var sorted = new List<string>(rows);
        sorted.Sort(StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', sorted)));
        return $"{section}: {rows.Count}, {Convert.ToHexString(hash).ToLowerInvariant()[..12]}";
    }
}
