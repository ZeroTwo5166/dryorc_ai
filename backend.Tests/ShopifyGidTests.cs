using backend.Services;
using Xunit;

namespace backend.Tests;

/// <summary>
/// Step 21 design doc, Decision 19. Pure unit tests — no DB, no HTTP.
/// </summary>
public class ShopifyGidTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(123456789)]
    [InlineData(long.MaxValue)]
    public void ForCustomer_PositiveId_ReturnsCanonicalGid(long numericId)
    {
        var gid = ShopifyGid.ForCustomer(numericId);

        Assert.Equal($"gid://shopify/Customer/{numericId}", gid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void ForCustomer_NonPositiveId_Throws(long numericId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShopifyGid.ForCustomer(numericId));
    }

    [Fact]
    public void TryParseCustomerId_ValidGid_ReturnsTrueAndNumericId()
    {
        var succeeded = ShopifyGid.TryParseCustomerId("gid://shopify/Customer/987654321", out var numericId);

        Assert.True(succeeded);
        Assert.Equal(987654321, numericId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("987654321")]
    [InlineData("gid://shopify/Order/987654321")]
    [InlineData("gid://shopify/customer/987654321")]
    [InlineData("gid://shopify/Customer/")]
    [InlineData("gid://shopify/Customer/0")]
    [InlineData("gid://shopify/Customer/-5")]
    [InlineData("gid://shopify/Customer/12a34")]
    [InlineData("gid://shopify/Customer/123 ")]
    public void TryParseCustomerId_MalformedInput_ReturnsFalse(string? gid)
    {
        var succeeded = ShopifyGid.TryParseCustomerId(gid, out var numericId);

        Assert.False(succeeded);
        Assert.Equal(0, numericId);
    }

    [Fact]
    public void ForCustomer_ThenTryParse_RoundTrips()
    {
        var gid = ShopifyGid.ForCustomer(42);

        Assert.True(ShopifyGid.TryParseCustomerId(gid, out var numericId));
        Assert.Equal(42, numericId);
    }
}
