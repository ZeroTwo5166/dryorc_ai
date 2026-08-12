using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace backend.Tests;

/// <summary>
/// Hosts the real app (real Program.cs wiring, real Neon dev DB via the same
/// ConnectionStrings:DefaultConnection user-secret the app itself uses —
/// Development environment triggers ASP.NET Core's automatic user-secrets
/// loading for the entry assembly, same as running the app normally).
/// Deliberately not swapped to an in-memory/fake DB: the point of this
/// suite is proving the real EF Core global query filter + real Postgres
/// unique constraints + real cookie auth actually enforce tenant isolation,
/// not that a mock does.
/// </summary>
public class NivoraApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
