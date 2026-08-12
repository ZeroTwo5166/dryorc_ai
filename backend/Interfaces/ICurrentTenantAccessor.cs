namespace backend.Interfaces;

/// <summary>
/// Resolves the BusinessId that AppDbContext's global query filters scope
/// every ITenantOwned entity to. Returning null (no tenant resolved yet —
/// e.g. before Identity login determines which business a user belongs to,
/// or design-time tooling) makes the filter match zero rows rather than
/// all rows: fail-closed, not fail-open. Code that legitimately needs
/// cross-tenant access (background jobs iterating all businesses) must
/// call .IgnoreQueryFilters() explicitly on that query rather than relying
/// on a null accessor to bypass scoping.
/// </summary>
public interface ICurrentTenantAccessor
{
    Guid? BusinessId { get; }
}
