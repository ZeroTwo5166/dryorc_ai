using Anthropic.Models.Messages;
using Anthropic.Services;
using backend.Constants;
using backend.Data;
using backend.Enums;
using backend.Models;
using backend.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 3 — DI registration (decisions addendum OD-8,
/// registration half): Program.cs registers IAiInvocationRecorder and both
/// wrappers, and (since Slice 4a) IUnauthorizedActionAuditWriter for its
/// only caller, ClaudeReplyService. Run against the real Program host via
/// NivoraApiFactory, where the fake IMessageService/IVoyageEmbeddingClient
/// sit UNDER the real wrappers and the real recorder writes to the dev DB.
/// Not one of the design pass's §7 tests — added because nothing else
/// exercises the registrations (the Slice 2 recorder had never been resolved
/// through DI on any path, verification F-01).
/// </summary>
public class AiProviderWrapperRegistrationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new PersistingInvocationRecorderApiFactory();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await TestBusinessCleanup.DeleteThenDisposeAsync(_businessIdsToClean, _factory);
    }

    // Mutation: drop any one of the four registrations.
    [Fact]
    public async Task ProgramHost_ResolvesRecorderAndBothWrappers_RegistersAuditWriter()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        Assert.IsType<AiInvocationRecorder>(services.GetRequiredService<IAiInvocationRecorder>());
        Assert.IsType<AnthropicInvoker>(services.GetRequiredService<IAnthropicInvoker>());
        Assert.IsType<VoyageEmbeddingInvoker>(services.GetRequiredService<IVoyageEmbeddingInvoker>());
        Assert.IsType<UnauthorizedActionAuditWriter>(services.GetRequiredService<IUnauthorizedActionAuditWriter>());
    }

    // DL-0 (Slice 5): the raw clients stay ordinary registrations, so the only
    // thing between a test host and Program.cs's own client is the fake
    // registered by raw service type in NivoraApiFactory. Mutation: remove
    // either AddSingleton line there.
    [Fact]
    public async Task TestHost_ResolvesTheFakeRawClients_NotProgramsOwn()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        Assert.IsType<FakeMessageService>(services.GetRequiredService<IMessageService>());
        Assert.IsType<FakeVoyageEmbeddingClient>(services.GetRequiredService<IVoyageEmbeddingClient>());
    }

    // The DI-resolved Voyage wrapper over the test host's
    // FakeVoyageEmbeddingClient produces a real row with non-null usage —
    // the arrangement the design pass (§3, Option (b) testability) relies on
    // for Slice 4's integration tests. Mutation: remove the fake's
    // accumulator population (row usage null).
    [Fact]
    public async Task ProgramHost_VoyageInvoker_WritesRowWithUsage()
    {
        var businessId = await SeedBusinessAsync("Wrapper DI Voyage Business");
        var ctx = new AiCallContext(businessId, AiOperations.KnowledgeRetrievalQuery, Guid.NewGuid(), 0);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var invoker = scope.ServiceProvider.GetRequiredService<IVoyageEmbeddingInvoker>();
            await invoker.EmbedAsync(ctx, ["one query"], VoyageInputType.Query);
        }

        var row = await FindRowAsync(ctx.CorrelationId);
        Assert.NotNull(row);
        Assert.Equal(AiProviders.Voyage, row!.Provider);
        Assert.Equal(AiOutcomes.Succeeded, row.Outcome);
        Assert.Equal(1, row.InputItemCount);
        Assert.NotNull(row.InputTokens);
    }

    [Fact]
    public async Task ProgramHost_AnthropicInvoker_WritesRowWithUsage()
    {
        var businessId = await SeedBusinessAsync("Wrapper DI Anthropic Business");
        var ctx = new AiCallContext(businessId, AiOperations.CustomerReply, Guid.NewGuid(), 0);

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var invoker = scope.ServiceProvider.GetRequiredService<IAnthropicInvoker>();
            await invoker.CreateMessageAsync(ctx, new MessageCreateParams
            {
                Model = AnthropicModels.Default,
                MaxTokens = 16,
                Messages = [new MessageParam { Role = Role.User, Content = "hi" }],
            });
        }

        var row = await FindRowAsync(ctx.CorrelationId);
        Assert.NotNull(row);
        Assert.Equal(AiProviders.Anthropic, row!.Provider);
        Assert.Equal(AnthropicModels.Default, row.Model);
        Assert.Equal(AiOutcomes.Succeeded, row.Outcome);
        Assert.Equal(10, row.InputTokens);  // FakeMessageService's fixed usage
        Assert.Equal(10, row.OutputTokens);
    }

    private async Task<Guid> SeedBusinessAsync(string name)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var businessId = Guid.NewGuid();
        db.Businesses.Add(new Business
        {
            Id = businessId,
            Name = name,
            TimeZone = "Europe/Copenhagen",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        _businessIdsToClean.Add(businessId);
        return businessId;
    }

    private async Task<AiProviderInvocation?> FindRowAsync(Guid correlationId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiProviderInvocations.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.CorrelationId == correlationId);
    }
}
