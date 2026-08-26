using Microsoft.AspNetCore.DataProtection;

namespace backend.Services;

public interface IIntegrationTokenProtector
{
    string Protect(string platform, string plaintext);
    string Unprotect(string platform, string ciphertext);
}

/// <summary>
/// Encrypts/decrypts Integration access/refresh tokens at rest using
/// ASP.NET Core's Data Protection API — already active in this app (Identity
/// relies on it for cookie protection) and, as of Step 18, backed by a
/// persistent key ring in Postgres via PersistKeysToDbContext (see
/// Program.cs's DataProtectionKeys registration and its backup requirement).
///
/// One IDataProtector per platform, purpose-strung as
/// "Nivora.Integration.{Platform}". This provides cryptographic PURPOSE
/// SEPARATION ONLY: ciphertext produced for one platform's purpose can never
/// be unintentionally decrypted through a different platform's purpose
/// string, even by legitimate code. It is NOT blast-radius containment — a
/// compromise of the underlying Data Protection key ring itself (the
/// DataProtectionKeys table) lets an attacker derive any platform's
/// purpose-specific subkey and decrypt every Integration row across every
/// platform and every tenant. Do not describe purpose strings as limiting
/// the impact of a key-ring compromise anywhere this class is referenced.
/// </summary>
public class IntegrationTokenProtector(IDataProtectionProvider dataProtectionProvider) : IIntegrationTokenProtector
{
    private const string PurposePrefix = "Nivora.Integration.";

    public string Protect(string platform, string plaintext) =>
        dataProtectionProvider.CreateProtector(PurposePrefix + platform).Protect(plaintext);

    public string Unprotect(string platform, string ciphertext) =>
        dataProtectionProvider.CreateProtector(PurposePrefix + platform).Unprotect(ciphertext);
}
