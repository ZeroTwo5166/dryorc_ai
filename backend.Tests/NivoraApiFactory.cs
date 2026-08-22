using Anthropic.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
///
/// IMessageService IS swapped, though — to a FakeMessageService, always,
/// regardless of whether Anthropic:ApiKey happens to be configured in this
/// environment. Every test using this factory predates Step 8 (or doesn't
/// care about Claude specifically) and must never incur a real, billed
/// Anthropic call just because it happens to POST a widget message. Tests
/// that genuinely want the real Claude integration use
/// LiveClaudeApiFactory instead, never this one.
/// </summary>
public class NivoraApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IMessageService, FakeMessageService>();
        });
    }
}
