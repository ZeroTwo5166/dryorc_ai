using Anthropic.Models.Messages;
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
/// wrappers, and deliberately NOT IUnauthorizedActionAuditWriter (its only
/// caller is Slice 4). Run against the real Program host via
/// NivoraApiFactory, where the fake IMessageService/IVoyageEmbeddingClient
/// sit UNDER the real wrappers and the real recorder writes to the dev DB.
/// Not one of the design pass's §7 tests — added because nothing else
/// exercises the registrations (the Slice 2 recorder had never been resolved
/// through DI on any path, verification F-01).
/// </summary>
public class AiProviderWrapperRegistrationTests : IAsyncLifetime
{
    private readonly NivoraApiFactory _factory = new();
    private readonly List<Guid> _businessIdsToClean = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_businessIdsToClean.Count > 0)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var ids = _businessIdsToClean.ToArray();

            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"AiProviderInvocations\" WHERE \"BusinessId\" = ANY({ids})");
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Businesses\" WHERE \"Id\" = ANY({ids})");
        }

        await _factory.DisposeAsync();
    }

    // Mutation: drop any one of the three registrations; register the
    // rule (c) audit writer.
    [Fact]
    public async Task ProgramHost_ResolvesRecorderAndBothWrappers_DoesNotRegisterAuditWriter()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;

        Assert.IsType<AiInvocationRecorder>(services.GetRequiredService<IAiInvocationRecorder>());
        Assert.IsType<AnthropicInvoker>(services.GetRequiredService<IAnthropicInvoker>());
        Assert.IsType<VoyageEmbeddingInvoker>(services.GetRequiredService<IVoyageEmbeddingInvoker>());
        Assert.Null(services.GetService<IUnauthorizedActionAuditWriter>());
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
