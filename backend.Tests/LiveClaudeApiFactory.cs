using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace backend.Tests;

/// <summary>
/// Deliberately separate from NivoraApiFactory — does NOT override
/// IMessageService, so Program.cs's own real registration (backed by
/// whatever Anthropic:ApiKey is actually configured) is what runs. Used
/// only by tests gated behind [LiveClaudeFact] (see LiveClaudeFactAttribute)
/// — never by ordinary tests, so a real, billed Anthropic call is only ever
/// possible when both the explicit opt-in flag and a real key are present.
/// </summary>
public class LiveClaudeApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}
