using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using backend.Auth;
using backend.Controllers;

namespace backend.Tests;

/// <summary>
/// Trace-redaction guard (D-10, D-14): every request record that a
/// controller action takes and that holds a secret-bearing member has a
/// hand-written PrintMembers, so ASP.NET Core's Trace-level argument line
/// cannot print the member. No host, no database.
/// </summary>
public class RequestRecordPrintMembersGuardTests
{
    // "TypeName.PropertyName" entries, each with a comment naming why the
    // member is not a secret. Empty: no exclusion is needed.
    private static readonly string[] ExcludedMembers = [];

    private static readonly string[] SensitiveSuffixes = ["Password", "Token", "Code", "Secret"];

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly) is not null;

    private static bool IsSensitive(PropertyInfo property) =>
        SensitiveSuffixes.Any(suffix => property.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        || string.Equals(property.Name, "VisitorSessionId", StringComparison.Ordinal);

    private static bool HasHandWrittenPrintMembers(Type type)
    {
        var method = type.GetMethod(
            "PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, [typeof(StringBuilder)]);
        return method is not null && !method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false);
    }

    internal static IReadOnlyList<Type> SensitiveDomainRecords(Assembly assembly, IEnumerable<Type> candidates, IReadOnlySet<string> exclusions) =>
        candidates
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => !method.IsSpecialName && !method.IsDefined(typeof(NonActionAttribute), inherit: true))
            .SelectMany(method => method.GetParameters())
            .Select(parameter => Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType)
            .Where(type => type.Assembly == assembly && IsRecord(type))
            .Where(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(property => IsSensitive(property) && !exclusions.Contains($"{type.Name}.{property.Name}")))
            .Distinct()
            .ToList();

    internal static IReadOnlyList<string> Violations(Assembly assembly, IEnumerable<Type> candidates, IReadOnlySet<string> exclusions) =>
        SensitiveDomainRecords(assembly, candidates, exclusions)
            .Where(type => !HasHandWrittenPrintMembers(type))
            .Select(type => type.FullName!)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void EverySecretBearingRequestRecord_HasHandWrittenPrintMembers()
    {
        var assembly = typeof(Program).Assembly;
        var violations = Violations(assembly, assembly.GetTypes(), ExcludedMembers.ToHashSet(StringComparer.Ordinal));

        Assert.True(violations.Count == 0, "request records with a compiler-generated PrintMembers: " + string.Join(", ", violations));
    }

    [Fact]
    public void Domain_HoldsTheEightSecretBearingRequestRecords()
    {
        var assembly = typeof(Program).Assembly;
        var domain = SensitiveDomainRecords(assembly, assembly.GetTypes(), ExcludedMembers.ToHashSet(StringComparer.Ordinal));

        Type[] expected =
        [
            typeof(RegisterRequest),
            typeof(LoginRequest),
            typeof(ConfirmEmailRequest),
            typeof(ResetPasswordRequest),
            typeof(WidgetInitRequest),
            typeof(WidgetVerificationVerifyRequest),
            typeof(PreviewStaffInvitationRequest),
            typeof(AcceptStaffInvitationRequest),
        ];
        foreach (var type in expected)
        {
            Assert.True(domain.Contains(type), "not among the secret-bearing request records: " + type.FullName);
        }

        foreach (var type in new[] { typeof(PreviewStaffInvitationRequest), typeof(AcceptStaffInvitationRequest) })
        {
            Assert.True(HasHandWrittenPrintMembers(type), "no hand-written PrintMembers: " + type.FullName);
        }
    }

    [Fact]
    public void ExclusionList_IsHonoured()
    {
        var assembly = typeof(RequestRecordPrintMembersGuardTests).Assembly;
        Type[] candidates = [typeof(SyntheticExclusionController)];

        var withoutExclusion = Violations(assembly, candidates, new HashSet<string>(StringComparer.Ordinal));
        var withExclusion = Violations(
            assembly, candidates, new HashSet<string>(["SyntheticCurrencyRequest.CurrencyCode"], StringComparer.Ordinal));

        Assert.True(
            withoutExclusion.Count == 1 && withoutExclusion[0] == typeof(SyntheticCurrencyRequest).FullName,
            "without the exclusion, the only violation must be SyntheticCurrencyRequest");
        Assert.True(withExclusion.Count == 0, "with the exclusion, no violation may remain");
    }

    [Fact]
    public void ExactNameEntry_IsVisitorSessionIdOnly()
    {
        var assembly = typeof(RequestRecordPrintMembersGuardTests).Assembly;
        Type[] candidates = [typeof(SyntheticExactNameController)];

        var violations = Violations(assembly, candidates, new HashSet<string>(StringComparer.Ordinal));

        Assert.True(
            violations.Count == 1 && violations[0] == typeof(ExactNameRequest).FullName,
            "the exact-name entry must match ExactNameRequest only");
    }

    private record SyntheticCurrencyRequest(string CurrencyCode);

    private record ExactNameRequest(string VisitorSessionId);

    private record LowerCaseNameRequest(string visitorsessionid);

    private record SuffixNameRequest(string VisitorSessionIdx);

    private sealed class SyntheticExclusionController : ControllerBase
    {
        public void Take(SyntheticCurrencyRequest request)
        {
        }
    }

    private sealed class SyntheticExactNameController : ControllerBase
    {
        public void TakeExact(ExactNameRequest request)
        {
        }

        public void TakeLowerCase(LowerCaseNameRequest request)
        {
        }

        public void TakeSuffix(SuffixNameRequest request)
        {
        }
    }
}
