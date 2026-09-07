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
                builder.SslMode = ParseDefinedSslMode(value);
            }
            // channel_binding (Neon-specific) has no Npgsql keyword equivalent —
            // Neon's pooler enforces it server-side regardless of client config.
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// Same Enum.TryParse-plus-Enum.IsDefined discipline established in
    /// ToolArgumentCanonicalizer.DecodeOrderCancellationArgs (Step 31) and
    /// OrderCancelProposalTool.ParseRequiredEnum (Step 33), applied here.
    /// Enum.Parse alone (the prior code) already throws for a non-numeric,
    /// unrecognized string, but — like Enum.TryParse — silently accepts any
    /// numeric string, including one with no corresponding SslMode member at
    /// all (e.g. "99"), converting it directly to an undefined enum value
    /// rather than failing. ignoreCase: true is preserved exactly to avoid
    /// changing behavior for any currently-valid sslmode value.
    /// </summary>
    private static SslMode ParseDefinedSslMode(string value)
    {
        if (!Enum.TryParse<SslMode>(value, ignoreCase: true, out var sslMode) || !Enum.IsDefined(sslMode))
        {
            throw new FormatException($"Invalid sslmode value '{value}' in connection string URI.");
        }

        return sslMode;
    }
}
