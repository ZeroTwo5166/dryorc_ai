using backend.Models;


namespace backend.Helpers
{
	public static class ExternalIdentityExtensions
	{
		/// <summary>
		/// The only sanctioned way to check whether an ExternalIdentity is
		/// currently valid. RevokedAt IS NULL alone is NOT sufficient — an
		/// identity can be expired without ever being explicitly revoked.
		/// Every Level 2 verification check must use this, not a raw
		/// RevokedAt check.
		///
		/// If this needs to run inside an EF Where() clause for a DB-level
		/// filter, this plain C# method won't translate to SQL automatically —
		/// use the raw condition directly at the query site instead:
		///   x.RevokedAt == null &amp;&amp; (x.ExpiresAt == null || x.ExpiresAt > now)
		/// kept in sync with the logic below.
		/// </summary>
		public static bool IsCurrentlyValid(this ExternalIdentity identity, DateTimeOffset now)
		{
			if (identity.RevokedAt != null) return false;
			if (identity.ExpiresAt != null && identity.ExpiresAt <= now) return false;
			return true;
		}
	}
}
