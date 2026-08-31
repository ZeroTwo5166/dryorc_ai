namespace backend.Services;

public enum SignedIdentityTokenOutcome
{
    Verified,
    Rejected,
}

/// <summary>CustomerId is non-null if and only if Outcome is Verified.</summary>
public record SignedIdentityTokenResult(SignedIdentityTokenOutcome Outcome, Guid? CustomerId);

/// <summary>
/// Step 21 (signed-token half). Validates a business-signed RS256
/// customer-identity assertion and, on success, resolves/creates the
/// Customer and ExternalIdentity it represents. See
/// docs/handoffs/step-21-signed-token-implementation-ready.md for the full
/// design. Every failure degrades to Rejected — never throws for an
/// untrusted/malformed input, matching IntegrationStateTokenService's own
/// fail-closed-via-return-value discipline (a widget init call is an
/// untrusted-input boundary, same as an OAuth callback).
/// </summary>
public interface ISignedIdentityTokenService
{
    /// <summary>
    /// businessId is the caller's own already-resolved tenant (e.g. from
    /// WidgetController.Init's Widget lookup) — never derived from the
    /// token itself. The token's kid-selected BusinessSigningKey row's own
    /// BusinessId must match this value or the token is rejected; this is
    /// the mandatory business-binding check, not an optional cross-check.
    /// </summary>
    Task<SignedIdentityTokenResult> ValidateAndResolveAsync(
        Guid businessId, string token, CancellationToken cancellationToken);
}
