using System.Net;
using System.Net.Http.Json;
using backend.Enums;
using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Direct unit tests for VoyageEmbeddingClient against a stubbed
/// HttpMessageHandler — specifically covers the index-based
/// embedding-to-input mapping (Voyage's docs give no guarantee that response
/// ordering matches request ordering) and the "never send output_dimension"
/// requirement (voyage-3 doesn't support it). No real HTTP calls, no DB.
/// </summary>
public class VoyageEmbeddingClientTests
{
    private class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private static VoyageEmbeddingClient BuildClient(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)) { BaseAddress = new Uri("https://api.voyageai.com/") });

    private static HttpResponseMessage JsonResponse(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    [Fact]
    public async Task EmbedAsync_MapsEmbeddingsByReturnedIndex_NotResponseArrayPosition()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 2f }, index = 1 }, // scrambled order: index 1 returned first
                new { embedding = new[] { 1f }, index = 0 },
            },
        }));

        var result = await client.EmbedAsync(["first", "second"], VoyageInputType.Document);

        Assert.Equal(1f, result[0][0]); // texts[0] ("first") maps to index 0's embedding, not array position 0
        Assert.Equal(2f, result[1][0]);
    }

    [Fact]
    public async Task EmbedAsync_DuplicateIndex_ThrowsRatherThanMisassign()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 1f }, index = 0 },
                new { embedding = new[] { 2f }, index = 0 },
            },
        }));

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a", "b"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_OutOfRangeIndex_ThrowsRatherThanMisassign()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 1f }, index = 0 },
                new { embedding = new[] { 2f }, index = 5 },
            },
        }));

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a", "b"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_MissingIndex_ThrowsRatherThanReturnIncompleteResult()
    {
        var client = BuildClient(_ => JsonResponse(new
        {
            data = new[]
            {
                new { embedding = new[] { 1f }, index = 0 },
                // index 1 never returned
            },
        }));

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a", "b"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_NonSuccessStatus_ThrowsVoyageEmbeddingException()
    {
        var client = BuildClient(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
        });

        await Assert.ThrowsAsync<VoyageEmbeddingException>(() => client.EmbedAsync(["a"], VoyageInputType.Document));
    }

    [Fact]
    public async Task EmbedAsync_NeverSendsOutputDimension_SendsModelAndInputType()
    {
        string? capturedBody = null;
        var client = BuildClient(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { data = new[] { new { embedding = new[] { 1f }, index = 0 } } });
        });

        await client.EmbedAsync(["a"], VoyageInputType.Document);

        Assert.NotNull(capturedBody);
        Assert.DoesNotContain("output_dimension", capturedBody);
        Assert.Contains("\"model\":\"voyage-3\"", capturedBody);
        Assert.Contains("\"input_type\":\"document\"", capturedBody);
    }

    [Fact]
    public async Task EmbedAsync_QueryInputType_SendsQueryNotDocument()
    {
        string? capturedBody = null;
        var client = BuildClient(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { data = new[] { new { embedding = new[] { 1f }, index = 0 } } });
        });

        await client.EmbedAsync(["a"], VoyageInputType.Query);

        Assert.Contains("\"input_type\":\"query\"", capturedBody);
    }

    [Fact]
    public async Task EmbedAsync_EmptyInput_ReturnsEmptyWithoutCallingVoyage()
    {
        var called = false;
        var client = BuildClient(_ =>
        {
            called = true;
            return JsonResponse(new { data = Array.Empty<object>() });
        });

        var result = await client.EmbedAsync([], VoyageInputType.Document);

        Assert.Empty(result);
        Assert.False(called);
    }
}
