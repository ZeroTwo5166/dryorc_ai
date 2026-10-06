using backend.Data;
using backend.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace backend.Tests;

/// <summary>
/// Drift guard for TestBusinessCleanup (design section 5, K4): compares the
/// helper's statement list and FK lists with the EF model. Reads the model
/// only; the placeholder connection string points at a closed local port and
/// is never opened, and nothing here catches an exception, so an attempted
/// connection would fail the test. Each failure lists every offending item.
/// </summary>
public class TestBusinessCleanupModelGuardTests
{
    private const string NeverOpenedConnectionString = "Host=127.0.0.1;Port=1;Database=never_opened";

    private static readonly string[] IdentityChildTables =
        ["AspNetUserClaims", "AspNetUserLogins", "AspNetUserTokens", "AspNetUserRoles"];

    private sealed record Fk(string ChildTable, string Column, string PrincipalTable, DeleteBehavior OnDelete, int ColumnCount);

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(NeverOpenedConnectionString, x => x.UseVector())
            .Options;
        using var db = new AppDbContext(options, new FixedTenantAccessor(null));
        return db.Model;
    }

    private static readonly Lazy<IModel> Model = new(BuildModel);

    private static IReadOnlyList<string> ModelTables() =>
        Model.Value.GetEntityTypes().Select(e => e.GetTableName() ?? $"<no table: {e.Name}>").ToList();

    private static IReadOnlyList<Fk> ModelFks() =>
        Model.Value.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys().Select(fk => new Fk(
                e.GetTableName()!,
                fk.Properties[0].GetColumnName(),
                fk.PrincipalEntityType.GetTableName()!,
                fk.DeleteBehavior,
                fk.Properties.Count)))
            .ToList();

    private static HashSet<string> StatementTables() =>
        TestBusinessCleanup.Statements.Select(s => s.Table).ToHashSet();

    private static HashSet<string> BusinessIdTables() =>
        ModelFks().Where(f => f.Column == "BusinessId" && f.PrincipalTable == "Businesses").Select(f => f.ChildTable).ToHashSet();

    private static string Name(Fk fk) => $"{fk.ChildTable}.{fk.Column} -> {fk.PrincipalTable} ({fk.OnDelete})";

    private static string Name(ModelFk fk) => $"{fk.ChildTable}.{fk.Column} -> {fk.PrincipalTable} ({fk.OnDelete})";

    private static void AssertNone(string what, IEnumerable<string> offenders)
    {
        var list = offenders.ToList();
        if (list.Count > 0)
        {
            Assert.Fail($"{what} ({list.Count}):\n  " + string.Join("\n  ", list));
        }
    }

    [Fact]
    public void G1_Tables_AreExactlyStatementsPlusUntouched()
    {
        var model = ModelTables();
        var listed = TestBusinessCleanup.Statements.Select(s => s.Table)
            .Concat(TestBusinessCleanup.UntouchedTables)
            .ToList();

        var offenders = new List<string>();
        offenders.AddRange(model.Except(listed).Select(t => $"in the model, in neither list: {t}"));
        offenders.AddRange(listed.Except(model).Select(t => $"in a list, not in the model: {t}"));
        offenders.AddRange(listed.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => $"listed {g.Count()} times: {g.Key}"));
        offenders.AddRange(model.GroupBy(t => t).Where(g => g.Count() > 1).Select(g => $"mapped by {g.Count()} entity types: {g.Key}"));

        AssertNone("G1 table mismatch", offenders);
    }

    [Fact]
    public void G2_ForeignKeys_AreExactlyAccountedForeignKeys()
    {
        var model = ModelFks().Select(f => new ModelFk(f.ChildTable, f.Column, f.PrincipalTable, f.OnDelete)).ToList();
        var accounted = TestBusinessCleanup.AccountedForeignKeys.ToList();

        var offenders = new List<string>();
        offenders.AddRange(model.Except(accounted).Select(f => $"in the model, not accounted: {Name(f)}"));
        offenders.AddRange(accounted.Except(model).Select(f => $"accounted, not in the model: {Name(f)}"));
        offenders.AddRange(accounted.GroupBy(f => f).Where(g => g.Count() > 1).Select(g => $"accounted {g.Count()} times: {Name(g.Key)}"));

        AssertNone("G2 foreign key mismatch", offenders);
    }

    [Fact]
    public void G3_ForeignKeys_AreSingleColumnWithKnownDeleteBehavior()
    {
        DeleteBehavior[] allowed = [DeleteBehavior.Restrict, DeleteBehavior.Cascade, DeleteBehavior.SetNull];

        var offenders = ModelFks()
            .Where(f => f.ColumnCount != 1 || !allowed.Contains(f.OnDelete))
            .Select(f => $"{Name(f)}, {f.ColumnCount} column(s)");

        AssertNone("G3 foreign key needs review", offenders);
    }

    [Fact]
    public void G4_EveryChild_IsDeletedBeforeItsPrincipal()
    {
        var steps = TestBusinessCleanup.Statements.ToDictionary(s => s.Table, s => s.Step);

        var offenders = new List<string>();
        foreach (var fk in ModelFks().Where(f => steps.ContainsKey(f.PrincipalTable)))
        {
            if (!steps.TryGetValue(fk.ChildTable, out var childStep))
            {
                offenders.Add($"{fk.ChildTable}.{fk.Column} -> {fk.PrincipalTable}: child table is not in Statements");
            }
            else if (childStep >= steps[fk.PrincipalTable])
            {
                offenders.Add($"{fk.ChildTable}.{fk.Column} -> {fk.PrincipalTable}: child step {childStep}, principal step {steps[fk.PrincipalTable]}");
            }
        }

        AssertNone("G4 order violation", offenders);
    }

    [Fact]
    public void G5_EveryStatement_MatchesItsTablesOwnership()
    {
        var fks = ModelFks();
        var businessIdTables = BusinessIdTables();
        var statementTables = StatementTables();

        const string byId = "\"Id\" = ANY(@ids)";
        const string byBusinessId = "\"BusinessId\" = ANY(@ids)";
        const string byUser = "\"UserId\" IN (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"BusinessId\" = ANY(@ids))";

        var offenders = new List<string>();
        foreach (var statement in TestBusinessCleanup.Statements)
        {
            var table = statement.Table;
            string? expected;
            if (table == "Businesses")
            {
                expected = byId;
            }
            else if (businessIdTables.Contains(table))
            {
                expected = byBusinessId;
            }
            else if (IdentityChildTables.Contains(table)
                     && fks.Any(f => f.ChildTable == table && f.Column == "UserId" && f.PrincipalTable == "AspNetUsers")
                     && fks.Where(f => f.ChildTable == table && !(f.Column == "UserId" && f.PrincipalTable == "AspNetUsers"))
                           .All(f => !statementTables.Contains(f.PrincipalTable)))
            {
                expected = byUser;
            }
            else
            {
                offenders.Add($"step {statement.Step} {table}: table has no ownership path to Businesses");
                continue;
            }

            var where = statement.Sql.IndexOf(" WHERE ", StringComparison.Ordinal);
            var target = where < 0 ? "" : statement.Sql[..where];
            var predicate = where < 0 ? "" : statement.Sql[(where + " WHERE ".Length)..];
            if (!target.EndsWith($" \"{table}\"", StringComparison.Ordinal) || predicate != expected)
            {
                offenders.Add($"step {statement.Step} {table}: expected target \"{table}\" and predicate {expected}; got {statement.Sql}");
            }
        }

        AssertNone("G5 ownership mismatch", offenders);
    }

    [Fact]
    public void G6_CrossOwnerChecks_AreExactlyTheCascadeAndSetNullKeysBetweenOwnedRows()
    {
        var businessIdTables = BusinessIdTables();
        var expected = ModelFks()
            .Where(f => f.OnDelete is DeleteBehavior.Cascade or DeleteBehavior.SetNull
                        && businessIdTables.Contains(f.ChildTable)
                        && f.PrincipalTable != "AspNetRoles")
            .Select(f => new ModelFk(f.ChildTable, f.Column, f.PrincipalTable, f.OnDelete))
            .ToList();
        var actual = TestBusinessCleanup.CrossOwnerChecks.ToList();

        var offenders = new List<string>();
        offenders.AddRange(expected.Except(actual).Select(f => $"missing from CrossOwnerChecks: {Name(f)}"));
        offenders.AddRange(actual.Except(expected).Select(f => $"in CrossOwnerChecks, not expected: {Name(f)}"));
        offenders.AddRange(actual.GroupBy(f => f).Where(g => g.Count() > 1).Select(g => $"listed {g.Count()} times: {Name(g.Key)}"));

        AssertNone("G6 cross-owner list mismatch", offenders);
    }

    [Fact]
    public void G7_NoForeignKey_FromAnUntouchedTableToADeletedTable()
    {
        var untouched = TestBusinessCleanup.UntouchedTables.ToHashSet();
        var statementTables = StatementTables();

        var offenders = ModelFks()
            .Where(f => untouched.Contains(f.ChildTable) && statementTables.Contains(f.PrincipalTable))
            .Select(Name);

        AssertNone("G7 untouched table references a deleted table", offenders);
    }

    private sealed class FixedTenantAccessor(Guid? businessId) : ICurrentTenantAccessor
    {
        public Guid? BusinessId => businessId;
    }
}
