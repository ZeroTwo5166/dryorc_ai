using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace backend.Tests;

/// <summary>
/// Deliberately separate from NivoraApiFactory — does NOT override
/// IMessageService, so Program.cs's own real registration (backed by
/// whatever Anthropic:ApiKey is actually configured) is what runs. Used
/// only by tests gated behind [LiveClaudeFact] (see LiveClaudeFactAttribute)
/// — never by ordinary tests, so a real, billed Anthropic call is only ever
/// possible when both the explicit opt-in flag and a real key are present.
/// Like NivoraApiFactory, runs none of Program.cs's three application hosted services (TestHostedServiceFilter).
/// </summary>
public class LiveClaudeApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureTestServices(services => TestHostedServiceFilter.Apply(services, TestHostedServices.None));
    }
}
