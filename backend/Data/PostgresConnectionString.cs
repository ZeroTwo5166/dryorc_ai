using Npgsql;

namespace backend.Data;

/// <summary>
/// Converts Neon's native "postgresql://user:pass@host/db?sslmode=require"
/// URI connection-string format into the ADO.NET keyword=value format
/// NpgsqlConnection actually requires. Npgsql does not accept a raw URI —
/// passing one straight into UseNpgsql() throws at connection-open time,
/// not at `dotnet build` or `dotnet ef migrations add` (neither needs a
/// live connection) — only a command that actually connects (`database
/// update`, the app starting) surfaces it.
///
/// Kept as a conversion step rather than pre-converting the stored value,
/// so configuration/.env can stay in Neon's native URI form — portable to
/// non-.NET tooling (e.g. Node scripts) that expect that format directly.
/// </summary>
public static class PostgresConnectionString
{
    public static string FromUri(string uri)
    {
        var parsed = new Uri(uri);
        var userInfo = parsed.UserInfo.Split(':', 2);

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = parsed.Host,
            Port = parsed.Port > 0 ? parsed.Port : 5432,
            Database = parsed.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
            // Neon always requires TLS; overridden below if the URI says otherwise.
            SslMode = SslMode.Require,
        };

        var query = parsed.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";

            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                builder.SslMode = Enum.Parse<SslMode>(value, ignoreCase: true);
            }
            // channel_binding (Neon-specific) has no Npgsql keyword equivalent —
            // Neon's pooler enforces it server-side regardless of client config.
        }

        return builder.ConnectionString;
    }
}
