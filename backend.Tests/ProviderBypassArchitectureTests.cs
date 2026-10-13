using System.Reflection;
using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Services;
using backend.Services;
using Xunit.Abstractions;

namespace backend.Tests;

/// <summary>
/// Step 27 Slice A1, Slice 5 — AT-1, the reflection half of the provider-bypass
/// rule (design docs/handoffs/billing-usage-a1-slice5-design.md §2; rulings F1,
/// F2, F3). Every type of the backend assembly is read in its signature
/// positions for the four raw provider types, and the set of findings
/// (outermost declaring type, prohibited type, position) must equal the
/// design's allowlist exactly, in both directions. No host, no database, no
/// package. The method-body half is ProviderBypassSourceScanTests (AT-2).
///
/// Signature-level only. Not seen: anything inside a method body — new
/// AnthropicClient, a service-locator call such as
/// GetRequiredService&lt;IMessageService&gt;(), a raw HttpClient post to
/// Voyage, typeof/nameof, a local variable. Not seen: using directives,
/// comments, string literals, attribute arguments. Lambdas and local
/// functions are seen only through their own parameter and return types and
/// through variables they capture. Some locals of async and iterator methods
/// are hoisted into fields in a Debug build and so are seen there; that is
/// incidental and not relied on. The expected table was derived for Debug,
/// the only configuration built locally or in CI. A raw type hidden behind
/// object or a new interface declared in an allowlisted type is not seen.
/// Method bodies are the source scan's job (ProviderBypassSourceScanTests).
/// Tests are out of scope: backend.Tests builds raw clients and fakes by
/// design.
/// </summary>
public class ProviderBypassArchitectureTests(ITestOutputHelper output)
{
    // Signature positions (design §2.2). Generic arguments are not a kind of
    // their own: every type read is unwrapped recursively (array, by-ref and
    // pointer element types; generic type arguments) and a prohibited type
    // found by unwrapping is attributed to the position it was found under.
    private enum Kind
    {
        CtorParam,
        Field,
        Property,
        MethodParam,
        MethodReturn,
        BaseOrInterface,
        GenericConstraint,
    }

    private sealed record Finding(Type Outermost, Type Prohibited, Kind Kind, string Member)
    {
        public string Tuple => FormatTuple(Outermost, Prohibited, Kind);
    }

    // The prohibited set (design §2.1, F1). SDK message and exception types
    // are not in it: they are IAnthropicInvoker's own signature and
    // AiErrorCategorizer's input.
    private static readonly Type[] ProhibitedTypes =
    [
        typeof(AnthropicClient),
        typeof(IMessageService),
        typeof(IVoyageEmbeddingClient),
        typeof(VoyageEmbeddingClient),
    ];

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    // Roughly 80% of the type count the first build reported (design §2.4):
    // 1,067 types at the C4 build, so a floor of 850.
    private const int TypeCountFloor = 850;

    // Every type in the assembly, including nested and compiler-generated
    // ones (design §2.2). GetExportedTypes() would drop the closures, display
    // classes and state machines the Field and MethodReturn rows come from.
    private static readonly Lazy<Type[]> Subjects = new(() => typeof(Program).Assembly.GetTypes());

    private static readonly Lazy<IReadOnlyList<Finding>> Findings = new(() => Scan(Subjects.Value));

    // The allowlist (design §2.3, F2): six outermost types, ten tuples. Program
    // is not allowed blanket — two IMessageService tuples, the registration
    // lambda at Program.cs:182 and its cached delegate; its registration lines
    // are pinned by AT-2.
    private static readonly string[] ExpectedTuples = Sorted(
    [
        FormatTuple(typeof(AnthropicInvoker), typeof(IMessageService), Kind.CtorParam),
        FormatTuple(typeof(AnthropicInvoker), typeof(IMessageService), Kind.Field),
        FormatTuple(typeof(MissingAnthropicApiKeyMessageService), typeof(IMessageService), Kind.BaseOrInterface),
        FormatTuple(typeof(MissingAnthropicApiKeyMessageService), typeof(IMessageService), Kind.MethodReturn),
        FormatTuple(typeof(VoyageEmbeddingInvoker), typeof(IVoyageEmbeddingClient), Kind.CtorParam),
        FormatTuple(typeof(VoyageEmbeddingInvoker), typeof(IVoyageEmbeddingClient), Kind.Field),
        FormatTuple(typeof(VoyageEmbeddingClient), typeof(IVoyageEmbeddingClient), Kind.BaseOrInterface),
        FormatTuple(typeof(MissingVoyageApiKeyEmbeddingClient), typeof(IVoyageEmbeddingClient), Kind.BaseOrInterface),
        FormatTuple(typeof(Program), typeof(IMessageService), Kind.MethodReturn),
        FormatTuple(typeof(Program), typeof(IMessageService), Kind.Field),
    ]);

    // Guard: the scan sees what it must see. Ordered so that the mechanism
    // mutant G1 (GetExportedTypes instead of GetTypes, design §7.4) fails at
    // the compiler-generated condition and not at the type-count floor.
    [Fact]
    public void AT1_ScanSeesTheBackendAssembly()
    {
        var subjects = Subjects.Value;
        var compilerGenerated = subjects.Count(t => t.IsNested && t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));
        output.WriteLine($"AT1: {subjects.Length} types scanned; {subjects.Count(t => t.IsNested)} nested; {compilerGenerated} nested and compiler-generated.");

        foreach (var required in new[] { typeof(AnthropicInvoker), typeof(VoyageEmbeddingInvoker), typeof(ClaudeReplyService), typeof(Program) })
        {
            Assert.True(subjects.Contains(required), $"AT1 guard: {required.FullName} is not among the scanned types.");
        }

        Assert.True(compilerGenerated > 0,
            "AT1 guard: no compiler-generated nested type was scanned — the scan is not seeing closures, display classes or state machines.");
        Assert.True(subjects.Length >= TypeCountFloor,
            $"AT1 guard: {subjects.Length} types scanned, below the floor of {TypeCountFloor}.");
    }

    // Mutation (design §7.1 M2, M3, M4): a non-allowlisted signature use of a
    // raw type anywhere in the assembly, or an extra position on an
    // allowlisted type, is an unlisted tuple. A listed tuple that no longer
    // exists fails the same assertion from the other side.
    [Fact]
    public void AT1_RawProviderTypes_AppearOnlyInTheAllowlistedSignatures()
    {
        foreach (var finding in Findings.Value)
        {
            output.WriteLine($"AT1 finding: {finding.Tuple} <- {finding.Member}");
        }

        var actual = Sorted(Findings.Value.Select(f => f.Tuple).Distinct());

        Assert.Equal(ExpectedTuples, actual);
    }

    // The consumer rule (F6): what the container hands a raw client to by
    // constructor injection is exactly the two wrappers. A subset of the fact
    // above, kept separate so the DI invariant has its own name and its own
    // mutant (design §7.1 M1).
    [Fact]
    public void AT1_RawClientConstructorConsumers_AreExactlyTheTwoInvokers()
    {
        var expected = Sorted(
        [
            FormatTuple(typeof(AnthropicInvoker), typeof(IMessageService), Kind.CtorParam),
            FormatTuple(typeof(VoyageEmbeddingInvoker), typeof(IVoyageEmbeddingClient), Kind.CtorParam),
        ]);

        var actual = Sorted(Findings.Value.Where(f => f.Kind == Kind.CtorParam).Select(f => f.Tuple).Distinct());

        Assert.Equal(expected, actual);
    }

    private static IReadOnlyList<Finding> Scan(IEnumerable<Type> subjects)
    {
        var findings = new List<Finding>();

        foreach (var subject in subjects)
        {
            // A nested type's findings are attributed to its outermost
            // declaring type, so a state machine or closure class is judged
            // with the type that owns it.
            var outermost = subject;
            while (outermost.DeclaringType is { } declaring)
            {
                outermost = declaring;
            }

            void Visit(Type? type, Kind kind, string member)
            {
                if (type is null)
                {
                    return;
                }

                foreach (var hit in Unwrap(type))
                {
                    // Self-exclusion: a prohibited type declared in this
                    // assembly is not a finding inside its own declaration or
                    // its own nested types (its async state machines hold
                    // their owner in a field).
                    if (ProhibitedTypes.Contains(hit) && hit != outermost)
                    {
                        findings.Add(new Finding(outermost, hit, kind, member));
                    }
                }
            }

            foreach (var ctor in subject.GetConstructors(Declared))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    Visit(parameter.ParameterType, Kind.CtorParam, $"{subject.FullName}::.ctor({parameter.Name})");
                }
            }

            foreach (var field in subject.GetFields(Declared))
            {
                Visit(field.FieldType, Kind.Field, $"{subject.FullName}::{field.Name}");
            }

            foreach (var property in subject.GetProperties(Declared))
            {
                Visit(property.PropertyType, Kind.Property, $"{subject.FullName}::{property.Name}");
            }

            foreach (var method in subject.GetMethods(Declared))
            {
                foreach (var parameter in method.GetParameters())
                {
                    Visit(parameter.ParameterType, Kind.MethodParam, $"{subject.FullName}::{method.Name}({parameter.Name})");
                }

                Visit(method.ReturnType, Kind.MethodReturn, $"{subject.FullName}::{method.Name}");

                if (method.IsGenericMethodDefinition)
                {
                    foreach (var argument in method.GetGenericArguments())
                    {
                        foreach (var constraint in argument.GetGenericParameterConstraints())
                        {
                            Visit(constraint, Kind.GenericConstraint, $"{subject.FullName}::{method.Name}<{argument.Name}>");
                        }
                    }
                }
            }

            for (var baseType = subject.BaseType; baseType is not null; baseType = baseType.BaseType)
            {
                Visit(baseType, Kind.BaseOrInterface, $"{subject.FullName} : {baseType.Name}");
            }

            foreach (var implemented in subject.GetInterfaces())
            {
                Visit(implemented, Kind.BaseOrInterface, $"{subject.FullName} : {implemented.Name}");
            }

            if (subject.IsGenericTypeDefinition)
            {
                foreach (var argument in subject.GetGenericArguments())
                {
                    foreach (var constraint in argument.GetGenericParameterConstraints())
                    {
                        Visit(constraint, Kind.GenericConstraint, $"{subject.FullName}<{argument.Name}>");
                    }
                }
            }
        }

        return findings;
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;

        if (type.HasElementType)
        {
            foreach (var inner in Unwrap(type.GetElementType()!))
            {
                yield return inner;
            }
        }

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var inner in Unwrap(argument))
                {
                    yield return inner;
                }
            }
        }
    }

    private static string FormatTuple(Type outermost, Type prohibited, Kind kind) =>
        $"{outermost.Name} | {prohibited.FullName} | {kind}";

    private static string[] Sorted(IEnumerable<string> tuples) =>
        tuples.OrderBy(t => t, StringComparer.Ordinal).ToArray();
}
