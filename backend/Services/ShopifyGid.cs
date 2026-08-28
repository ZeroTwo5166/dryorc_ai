using System.Globalization;

namespace backend.Services;

/// <summary>
/// Step 21 design doc, Decision 19. Canonical Shopify customer GID
/// construction/parsing — forward-provisioned for the future signed-token
/// flow (a business's storefront is expected to supply a raw/legacy numeric
/// Shopify customer id, not a GID) and independently testable now. NOT
/// wired into the OTP half's Customer resolution/auto-creation path — that
/// path never populates Customer.ExternalId at all (design doc Decision 20:
/// OTP success sets only the conversation-scoped Level2VerifiedUntil).
///
/// Step 20 (IOrderOwnershipAuthorizationService/ShopifyOrderLookupService)
/// already requires Customer.ExternalId in exactly this canonical form —
/// this utility is the only place in the repository that constructs one
/// from a raw numeric id, so any future writer of Customer.ExternalId
/// should go through it rather than string-formatting a GID inline.
/// </summary>
public static class ShopifyGid
{
    private const string CustomerResourcePrefix = "gid://shopify/Customer/";

    /// <summary>
    /// Constructs the canonical Shopify customer GID from a raw numeric
    /// Shopify customer id. Throws ArgumentOutOfRangeException for any
    /// non-positive input — a malformed/negative/zero id must never
    /// silently produce a plausible-looking-but-wrong GID.
    /// </summary>
    public static string ForCustomer(long numericId)
    {
        if (numericId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numericId), numericId,
                "Shopify customer id must be a positive integer.");
        }

        return $"{CustomerResourcePrefix}{numericId}";
    }

    /// <summary>
    /// Parses the raw numeric Shopify customer id out of an already-
    /// canonical GID string. Returns false for anything that isn't exactly
    /// "gid://shopify/Customer/{positive integer}" — no partial, whitespace-
    /// tolerant, or case-insensitive matching, since a malformed GID must
    /// never be silently accepted as valid.
    /// </summary>
    public static bool TryParseCustomerId(string? gid, out long numericId)
    {
        numericId = 0;

        if (string.IsNullOrEmpty(gid) || !gid.StartsWith(CustomerResourcePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = gid[CustomerResourcePrefix.Length..];
        return long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out numericId) && numericId > 0;
    }
}
