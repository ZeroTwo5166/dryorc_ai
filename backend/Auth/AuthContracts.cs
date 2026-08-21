namespace backend.Auth;

/// <summary>
/// Registers a brand-new Business together with its first staff user, who
/// becomes that business's Owner. Step 4's only registration path — inviting
/// additional Admin/Agent staff into an existing business needs an
/// authenticated inviter and is out of this step's scope (no such endpoint
/// exists yet).
/// </summary>
public record RegisterRequest(string BusinessName, string TimeZone, string Email, string Password);

public record RegisterResponse(Guid UserId, Guid BusinessId, string Email);

public record LoginRequest(string Email, string Password, bool RememberMe = false);

public record LoginResponse(Guid UserId, Guid BusinessId, string Email, IReadOnlyList<string> Roles);

public record MeResponse(
    Guid UserId,
    Guid BusinessId,
    string Email,
    string BusinessName,
    string? PlanTier,
    IReadOnlyList<string> Roles,
    DateTimeOffset ExpiresAtUtc);

public record ConfirmEmailRequest(Guid UserId, string Token);

public record ResendVerificationRequest(string Email);

public record ForgotPasswordRequest(string Email);

public record ResetPasswordRequest(Guid UserId, string Token, string NewPassword);

/// <summary>
/// Response to POST /api/auth/extend-session — the new absolute expiry the
/// server just committed to the re-issued cookie, so the frontend never has
/// to guess/derive it locally. See AuthController.ExtendSession.
/// </summary>
public record ExtendSessionResponse(DateTimeOffset ExpiresAtUtc);
