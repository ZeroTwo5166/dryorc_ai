using System.Diagnostics;
using System.Text;
using backend.Auth;
using backend.Data;
using backend.Interfaces;
using backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace NivoraTestDatabaseTool;

/// <summary>
/// Provisions the separate test database nivora_test on the same server as the
/// development database: create the database, create the vector extension,
/// apply the migrations by generated script, create the roles through
/// RoleSeeder. "rebuild" drops nivora_test WITH (FORCE) first. Run by the
/// owner by hand; tests and app startup never provision.
///
/// No top-level statements: the backend's own Program class is in the global
/// namespace and a second one here would shadow it.
/// </summary>
internal static class TestDatabaseTool
{
    private const string N = "nivora_test";
    private const string TestSecretKey = "ConnectionStrings:TestConnection";
    private const string DevSecretKey = "ConnectionStrings:DefaultConnection";

    private const string Usage = "usage: TestDatabase [rebuild | --self-test | --write-script <absolute-path>]";
    private const string MaintenanceConnectionLiteral = "Host=127.0.0.1;Port=1;Database=nivora_test";
    private const string InstalledPsqlPath = @"C:\Program Files\PostgreSQL\18\bin\psql.exe";

    private const string GuardBlock =
        "DO $$ BEGIN IF current_database() <> 'nivora_test' THEN RAISE EXCEPTION 'provisioning refused: wrong database'; END IF; END $$;";

    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return await ProvisionAsync(rebuild: false);
        }

        if (args.Length == 1 && args[0] == "rebuild")
        {
            return await ProvisionAsync(rebuild: true);
        }

        if (args.Length == 1 && args[0] == "--self-test")
        {
            return SelfTest();
        }

        if (args.Length == 2 && args[0] == "--write-script")
        {
            return WriteScript(args[1]);
        }

        Console.WriteLine(Usage);
        return 1;
    }

    // ---------------------------------------------------------------------
    // The gate (X8): the one check made on the secrets before the first write.
    // ---------------------------------------------------------------------

    /// <summary>
    /// True only when the host of the test value equals the host of the
    /// development value and the test value's database name is exactly
    /// nivora_test. Null, empty or unparsable input returns false before any
    /// comparison. Uses nothing from the development value but its host.
    /// </summary>
    internal static bool SameServerAndName(string? devValue, string? testValue)
    {
        if (string.IsNullOrWhiteSpace(devValue) || string.IsNullOrWhiteSpace(testValue))
        {
            return false;
        }

        try
        {
            var devHost = new Uri(devValue).Host;
            var testHost = new Uri(testValue).Host;
            if (devHost.Length == 0 || testHost.Length == 0)
            {
                return false;
            }

            var database = new NpgsqlConnectionStringBuilder(PostgresConnectionString.FromUri(testValue)).Database ?? "";
            var hostsEqual = string.Equals(devHost, testHost, StringComparison.OrdinalIgnoreCase);
            var nameOk = string.Equals(database, N, StringComparison.Ordinal);
            return hostsEqual && nameOk;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // Pure helpers covered by --self-test.
    // ---------------------------------------------------------------------

    internal static string StripBom(string s) => s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;

    /// <summary>Copy of the environment without any variable whose name starts with PG (any case).</summary>
    internal static Dictionary<string, string?> ScrubPgEnvironment(IDictionary<string, string?> env)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in env)
        {
            if (!key.StartsWith("PG", StringComparison.OrdinalIgnoreCase))
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Npgsql's SslMode names are not libpq's; null for a value the table does not know.</summary>
    internal static string? LibpqSslMode(SslMode mode) => mode switch
    {
        SslMode.Disable => "disable",
        SslMode.Allow => "allow",
        SslMode.Prefer => "prefer",
        SslMode.Require => "require",
        SslMode.VerifyCA => "verify-ca",
        SslMode.VerifyFull => "verify-full",
        _ => null,
    };

    internal enum ChannelBindingKind { None, Value, Invalid }

    /// <summary>
    /// Reads the channel_binding query key (which FromUri ignores): key matched
    /// case-insensitively, value unescaped. Value only for disable, prefer or
    /// require; any other value, or a repeated key, is Invalid.
    /// </summary>
    internal static (ChannelBindingKind Kind, string? Value) ReadChannelBinding(string rawUri)
    {
        var query = new Uri(rawUri).Query.TrimStart('?');
        string? found = null;
        var count = 0;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (!string.Equals(Uri.UnescapeDataString(kv[0]), "channel_binding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            found = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
        }

        if (count == 0)
        {
            return (ChannelBindingKind.None, null);
        }

        if (count > 1)
        {
            return (ChannelBindingKind.Invalid, null);
        }

        return found is "disable" or "prefer" or "require"
            ? (ChannelBindingKind.Value, found)
            : (ChannelBindingKind.Invalid, null);
    }

    /// <summary>
    /// True when --write-script must refuse the path: not fully qualified,
    /// already existing, parent folder missing, or at or under the repository
    /// root (whole path segments, ignoring case).
    /// </summary>
    internal static bool PathRefusal(string path, string root, Func<string, bool> exists, Func<string, bool> parentExists)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return true;
        }

        var full = Path.GetFullPath(path);
        var trimmedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), trimmedRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (exists(full))
        {
            return true;
        }

        var parent = Path.GetDirectoryName(full);
        return parent is null || !parentExists(parent);
    }

    private static string? FindRepositoryRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            // A worktree's .git is a file, a normal checkout's is a directory.
            var marker = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // ---------------------------------------------------------------------
    // Script generation (step 1b): in-process, from a context that cannot connect.
    // ---------------------------------------------------------------------

    private sealed class NullTenantAccessor : ICurrentTenantAccessor
    {
        public Guid? BusinessId => null;
    }

    private sealed class ThrowOnOpenInterceptor : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(System.Data.Common.DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
            => throw new InvalidOperationException("script generation must not open a connection");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(System.Data.Common.DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("script generation must not open a connection");
    }

    private static string GenerateMigrationScript()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(MaintenanceConnectionLiteral, o => o.UseVector())
            .AddInterceptors(new ThrowOnOpenInterceptor())
            .Options;
        using var db = new AppDbContext(options, new NullTenantAccessor());
        return StripBom(db.GetService<IMigrator>().GenerateScript());
    }

    private static int WriteScript(string path)
    {
        try
        {
            var root = FindRepositoryRoot(AppContext.BaseDirectory);
            if (root is null)
            {
                Console.WriteLine("repository root not found; --write-script stopped");
                return 1;
            }

            if (PathRefusal(path, root, p => File.Exists(p) || Directory.Exists(p), Directory.Exists))
            {
                Console.WriteLine("path refused: it must be absolute, new, in an existing folder, and outside the repository");
                return 1;
            }

            File.WriteAllText(path, GenerateMigrationScript(), new UTF8Encoding(false));
            Console.WriteLine("script written");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"failed: {ex.GetType().Name}");
            return 1;
        }
    }

    // ---------------------------------------------------------------------
    // Create / rebuild.
    // ---------------------------------------------------------------------

    private static async Task<int> ProvisionAsync(bool rebuild)
    {
        // X8a: the gate line is the first line printed. Reading both secrets is
        // inside the gate: any failure to read is a "no".
        string? testRaw = null;
        string? devRaw = null;
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddUserSecrets(typeof(AppDbContext).Assembly, optional: true)
                .Build();
            testRaw = configuration[TestSecretKey];
            devRaw = configuration[DevSecretKey];
        }
        catch (Exception)
        {
            testRaw = null;
            devRaw = null;
        }

        var gateOk = SameServerAndName(devRaw, testRaw);
        devRaw = null;
        Console.WriteLine(gateOk ? "host and database name check: yes" : "host and database name check: no");
        if (!gateOk)
        {
            return 2;
        }

        try
        {
            return await RunAfterGateAsync(testRaw!, rebuild);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"failed: {ex.GetType().Name}, SQLSTATE {SqlStateOf(ex)}");
            return 1;
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    private static string SqlStateOf(Exception ex) => (ex as PostgresException)?.SqlState ?? "none";

    private static async Task<int> RunAfterGateAsync(string testRaw, bool rebuild)
    {
        // channel_binding (declared in the raw value, ignored by FromUri) is read after the gate.
        var channelBinding = ReadChannelBinding(testRaw);
        if (channelBinding.Kind == ChannelBindingKind.Invalid)
        {
            Console.WriteLine("stopped: the channel_binding option of the test value is not usable (disable, prefer or require, once)");
            return 1;
        }

        var psql = FindPsql();
        if (psql is null)
        {
            Console.WriteLine("stopped: psql.exe was not found on PATH or at the installed location");
            return 1;
        }

        // Step 1b: generate the script before any connection is opened.
        var migrationScript = GenerateMigrationScript();
        Console.WriteLine("migration script generated in process (no connection opened)");

        var keyword = PostgresConnectionString.FromUri(testRaw);
        var testBuilder = new NpgsqlConnectionStringBuilder(keyword);
        var sslMode = LibpqSslMode(testBuilder.SslMode);
        if (sslMode is null)
        {
            Console.WriteLine("stopped: the sslmode of the test value is not usable");
            return 1;
        }

        string MaintenanceString() => new NpgsqlConnectionStringBuilder(keyword) { Database = "postgres" }.ConnectionString;

        // Step 2: maintenance connection.
        await using (var maintenance = await OpenAsync(MaintenanceString(), "postgres"))
        {
            // Step 3.
            if (rebuild)
            {
                await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS {N} WITH (FORCE);");
                Console.WriteLine("dropped nivora_test if it existed");
            }
            else if (await DatabaseExistsAsync(maintenance))
            {
                Console.WriteLine("nivora_test exists; run with rebuild to drop and recreate it");
                return 1;
            }
        }

        // Step 4: CREATE DATABASE with a bounded retry (RV9).
        var attemptsUsed = 0;
        var created = false;
        var sqlStates = new List<string>();
        for (var attempt = 1; attempt <= 3 && !created; attempt++)
        {
            try
            {
                attemptsUsed = attempt;
                if (attempt > 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt == 2 ? 15 : 30));
                    await using var check = await OpenAsync(MaintenanceString(), "postgres");
                    if (await DatabaseExistsAsync(check))
                    {
                        // The failed attempt succeeded on the server (step 3 established none existed).
                        created = true;
                        attemptsUsed = attempt - 1;
                        break;
                    }
                }

                await using var maintenance = await OpenAsync(MaintenanceString(), "postgres");
                await ExecuteAsync(maintenance, $"CREATE DATABASE {N};");
                created = true;
            }
            catch (Exception ex)
            {
                sqlStates.Add($"attempt {attempt}: SQLSTATE {SqlStateOf(ex)}");
            }
        }

        Console.WriteLine($"create attempts used: {attemptsUsed}");
        if (!created)
        {
            foreach (var line in sqlStates)
            {
                Console.WriteLine(line);
            }

            Console.WriteLine("stopped: CREATE DATABASE failed three times; nivora_test is absent");
            return 1;
        }

        // Step 5: the test database, extension without VERSION (S6).
        await using (var db = await OpenAsync(testBuilder.ConnectionString, N))
        {
            await ExecuteAsync(db, "CREATE EXTENSION vector;");
            Console.WriteLine("created extension vector");
        }

        // Step 6: apply the generated script through psql.
        var tempFile = Path.Combine(Path.GetTempPath(), $"nivora_test_{Guid.NewGuid():N}.sql");
        try
        {
            await File.WriteAllTextAsync(tempFile, GuardBlock + "\n" + migrationScript, new UTF8Encoding(false));
            var exitCode = await RunPsqlAsync(psql, tempFile, testBuilder, sslMode, channelBinding.Value);
            if (exitCode != 0)
            {
                Console.WriteLine($"stopped: psql exit code {exitCode}");
                return 1;
            }
        }
        finally
        {
            File.Delete(tempFile);
        }

        Console.WriteLine("applied the migration script");

        var contextOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(testBuilder.ConnectionString, o => o.UseVector())
            .Options;
        await using (var context = new AppDbContext(contextOptions, new NullTenantAccessor()))
        {
            var applied = (await context.Database.GetAppliedMigrationsAsync()).ToList();
            var model = context.Database.GetMigrations().ToList();
            if (!applied.SequenceEqual(model, StringComparer.Ordinal))
            {
                Console.WriteLine($"stopped: applied migrations ({applied.Count}) do not equal the model's ({model.Count})");
                return 1;
            }
        }

        // Step 7: roles through RoleSeeder, without a web host.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICurrentTenantAccessor, NullTenantAccessor>();
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(testBuilder.ConnectionString, x => x.UseVector()));
        services.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddRoleStore<RoleStore<IdentityRole<Guid>, AppDbContext, Guid>>();
        await using (var provider = services.BuildServiceProvider())
        {
            await using var scope = provider.CreateAsyncScope();
            await RoleSeeder.SeedAsync(scope.ServiceProvider);
        }

        Console.WriteLine("seeded roles");

        // Step 8: report, in one read-only transaction.
        await ReportAsync(testBuilder.ConnectionString, attemptsUsed);

        // Step 9 (pools are cleared in the caller's finally).
        return 0;
    }

    private static async Task ReportAsync(string connectionString, int attemptsUsed)
    {
        await using var connection = await OpenAsync(connectionString, N);
        await ExecuteAsync(connection, "BEGIN TRANSACTION READ ONLY;");
        try
        {
            Console.WriteLine("report:");
            Console.WriteLine($"  database: {await ScalarAsync(connection, "SELECT current_database()")}");
            Console.WriteLine($"  server version: {await ScalarAsync(connection, "SELECT current_setting('server_version')")}");
            Console.WriteLine($"  vector version: {await ScalarAsync(connection, "SELECT extversion FROM pg_extension WHERE extname = 'vector'")}");
            Console.WriteLine($"  migrations: {await ScalarAsync(connection, "SELECT count(*) FROM \"__EFMigrationsHistory\"")}");
            Console.WriteLine($"  last migration: {await ScalarAsync(connection, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1")}");
            Console.WriteLine($"  tables in public: {await ScalarAsync(connection, "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE'")}");
            Console.WriteLine($"  roles: {await ScalarAsync(connection, "SELECT coalesce(string_agg(\"Name\", ', ' ORDER BY \"Name\"), '') FROM \"AspNetRoles\"")}");
            Console.WriteLine($"  Businesses: {await ScalarAsync(connection, "SELECT count(*) FROM \"Businesses\"")}");
            Console.WriteLine($"  AspNetUsers: {await ScalarAsync(connection, "SELECT count(*) FROM \"AspNetUsers\"")}");
            Console.WriteLine($"  DataProtectionKeys: {await ScalarAsync(connection, "SELECT count(*) FROM \"DataProtectionKeys\"")}");
            Console.WriteLine($"  create attempts used: {attemptsUsed}");
            Console.WriteLine("  catalog digest:");
            foreach (var line in await CatalogDigest.ComputeAsync(connection))
            {
                Console.WriteLine($"    {line}");
            }
        }
        finally
        {
            await ExecuteAsync(connection, "ROLLBACK;");
        }
    }

    // ---------------------------------------------------------------------
    // Small database and process helpers.
    // ---------------------------------------------------------------------

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, string expectedDatabase)
    {
        var connection = new NpgsqlConnection(connectionString);
        try
        {
            await connection.OpenAsync();
            var current = await ScalarAsync(connection, "SELECT current_database()");
            if (!string.Equals(current, expectedDatabase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("connected to an unexpected database");
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task<bool> DatabaseExistsAsync(NpgsqlConnection maintenance)
    {
        await using var command = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = 'nivora_test'", maintenance);
        return await command.ExecuteScalarAsync() is not null;
    }

    private static string? FindPsql()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "psql.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped.
            }
        }

        return File.Exists(InstalledPsqlPath) ? InstalledPsqlPath : null;
    }

    private static async Task<int> RunPsqlAsync(string psql, string scriptFile, NpgsqlConnectionStringBuilder builder, string sslMode, string? channelBinding)
    {
        var info = new ProcessStartInfo(psql)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-X", "-q", "-v", "ON_ERROR_STOP=1", "-f", scriptFile })
        {
            info.ArgumentList.Add(argument);
        }

        var inherited = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in info.Environment.Keys)
        {
            inherited[key] = info.Environment[key];
        }

        info.Environment.Clear();
        foreach (var (key, value) in ScrubPgEnvironment(inherited))
        {
            info.Environment[key] = value;
        }

        info.Environment["PGHOST"] = builder.Host;
        info.Environment["PGPORT"] = builder.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        info.Environment["PGUSER"] = builder.Username;
        info.Environment["PGPASSWORD"] = builder.Password ?? "";
        info.Environment["PGDATABASE"] = N;
        info.Environment["PGSSLMODE"] = sslMode;
        if (channelBinding is not null)
        {
            info.Environment["PGCHANNELBINDING"] = channelBinding;
        }

        info.Environment["PGCLIENTENCODING"] = "UTF8";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("psql did not start");
        // psql's own output is read to the end (so it cannot block) and never forwarded.
        await Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    // ---------------------------------------------------------------------
    // --self-test (T9): no secret, no connection.
    // ---------------------------------------------------------------------

    private static int SelfTest()
    {
        var results = new List<(string Id, bool Ok)>();
        void Check(string id, bool ok) => results.Add((id, ok));

        // (1) The gate, same host a.invalid unless stated.
        const string dev = "postgresql://devuser:devpass@a.invalid/devdb";
        string Test(string host, string name) => $"postgresql://u:p@{host}/{name}";
        Check("G1", SameServerAndName(dev, Test("a.invalid", "nivora_test")));
        Check("G2", !SameServerAndName(dev, Test("a.invalid", "neondb")));
        Check("G3", !SameServerAndName(dev, Test("a.invalid", "NIVORA_TEST")));
        Check("G4", !SameServerAndName(dev, Test("a.invalid", "nivora_test2")));
        Check("G5", !SameServerAndName(dev, Test("a.invalid", "postgres")));
        Check("G6", !SameServerAndName(dev, Test("a.invalid", "")));
        Check("G7", !SameServerAndName(dev, Test("b.invalid", "nivora_test")));
        Check("G7b", !SameServerAndName(dev, Test("a.invalid.x", "nivora_test")));
        Check("G8", SameServerAndName(dev, Test("A.INVALID", "nivora_test")));
        Check("G9", !SameServerAndName("", Test("a.invalid", "nivora_test")));
        Check("G10", !SameServerAndName("not a uri", Test("a.invalid", "nivora_test")));
        Check("G11", !SameServerAndName(dev, ""));
        Check("G12", !SameServerAndName(dev, "not a uri"));

        // (2) BOM strip.
        Check("BOM1", StripBom("\uFEFFselect 1;") == "select 1;");
        Check("BOM2", StripBom("select 1;") == "select 1;");

        // (3) Environment scrub.
        var scrubbed = ScrubPgEnvironment(new Dictionary<string, string?>
        {
            ["PGSERVICE"] = "x",
            ["pgoptions"] = "x",
            ["PGPASSFILE"] = "x",
            ["PATH"] = "keep",
        });
        Check("ENV1", !scrubbed.ContainsKey("PGSERVICE"));
        Check("ENV2", !scrubbed.ContainsKey("pgoptions") && !scrubbed.ContainsKey("PGOPTIONS"));
        Check("ENV3", !scrubbed.ContainsKey("PGPASSFILE"));
        Check("ENV4", scrubbed.TryGetValue("PATH", out var path) && path == "keep");

        // (4) SslMode mapping and the channel_binding query read.
        Check("SSL1", LibpqSslMode(SslMode.Disable) == "disable");
        Check("SSL2", LibpqSslMode(SslMode.Allow) == "allow");
        Check("SSL3", LibpqSslMode(SslMode.Prefer) == "prefer");
        Check("SSL4", LibpqSslMode(SslMode.Require) == "require");
        Check("SSL5", LibpqSslMode(SslMode.VerifyCA) == "verify-ca");
        Check("SSL6", LibpqSslMode(SslMode.VerifyFull) == "verify-full");
        Check("SSL7", LibpqSslMode((SslMode)99) is null);
        Check("CB1", ReadChannelBinding("postgresql://u:p@a.invalid/x?sslmode=require") == (ChannelBindingKind.None, null));
        Check("CB2", ReadChannelBinding("postgresql://u:p@a.invalid/x?Channel_Binding=require") == (ChannelBindingKind.Value, "require"));
        Check("CB3", ReadChannelBinding("postgresql://u:p@a.invalid/x?channel_binding=bogus").Kind == ChannelBindingKind.Invalid);
        Check("CB4", ReadChannelBinding("postgresql://u:p@a.invalid/x?channel_binding=require&channel_binding=require").Kind == ChannelBindingKind.Invalid);

        // (5) The --write-script path rule, root C:\fake-root.
        const string root = @"C:\fake-root";
        Func<string, bool> none = _ => false;
        Func<string, bool> all = _ => true;
        Check("PATH1", PathRefusal(@"C:\fake-root\sub\x.sql", root, none, all));
        Check("PATH2", PathRefusal(root, root, none, all));
        Check("PATH3", !PathRefusal(@"C:\fake-other\x.sql", root, none, all));
        Check("PATH4", PathRefusal("x.sql", root, none, all));
        Check("PATH5", PathRefusal(@"C:\fake-other\x.sql", root, all, all));
        Check("PATH6", !PathRefusal(@"C:\fake-root-scratch\x.sql", root, none, all));
        Check("PATH7", PathRefusal(@"C:\fake-other\x.sql", root, none, none));

        foreach (var (id, ok) in results)
        {
            Console.WriteLine($"{(ok ? "pass" : "fail")} {id}");
        }

        var failed = results.Count(r => !r.Ok);
        Console.WriteLine($"self-test: {results.Count - failed} pass, {failed} fail");
        return failed == 0 ? 0 : 1;
    }
}
