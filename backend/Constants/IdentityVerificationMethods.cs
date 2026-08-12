namespace backend.Constants;

/// <summary>
/// String constants for ExternalIdentity.VerificationMethod. Use these
/// instead of raw string literals. Backed by a DB-level CHECK constraint
/// (CK_ExternalIdentities_VerificationMethod) since this is a small,
/// closed set unlikely to grow — unlike Channel.
/// </summary>
public static class IdentityVerificationMethods
{
    public const string SignedToken = "SignedToken";
    public const string Otp = "Otp";
}