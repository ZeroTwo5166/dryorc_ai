using backend.Interfaces;

namespace backend.Models;

/// <summary>
/// Step 21 (signed-token half). Records a consumed signed-token `jti` —
/// the sole database-level arbiter preventing replay. A row's mere
/// existence for (BusinessId, Jti) means that token has already been
/// used; see AppDbContext's unique index configuration and
/// SignedIdentityTokenService's insert-first sequencing.
///
/// Cleanup is storage hygiene only, not correctness — see
/// ConsumedSignedIdentityTokenCleanupService's own doc comment for why
/// deleting an old row can never make an expired token replayable again.
/// </summary>
public class ConsumedSignedIdentityToken : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid BusinessId { get; set; }

    public required string Jti { get; set; }
    public DateTimeOffset ConsumedAt { get; set; }
}
