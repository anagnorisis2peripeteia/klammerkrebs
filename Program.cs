using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

// klammerkrebs — a Roslyn-native disposal-leak analyzer (krebs-suite engine prototype).
//
// Two detectors, both semantic (they resolve IDisposable through a real compilation, so they
// don't false-positive on same-named non-disposable types):
//   A. LOCAL LEAK      — an IDisposable is `new`-ed into a local and neither disposed, returned,
//                        nor handed to something that takes ownership. (CA2000 territory.)
//   B. FIELD-KEPT LEAK — an IDisposable is `new`-ed and stored in a field (or added to a field
//                        collection), but the owning type never disposes it anywhere. This is the
//                        class CA2000 misses and the one that leaks live handles for a process
//                        lifetime (e.g. Stryker's SseServer StreamWriters).

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: klammerkrebs <path-to-.csproj-or-.sln> [--json]");
    return 2;
}

var target = args[0];
var asJson = args.Contains("--json");

var instance = MSBuildLocator.RegisterDefaults();
Console.Error.WriteLine($"[krebs] MSBuild {instance.Version} @ {instance.MSBuildPath}");

using var workspace = MSBuildWorkspace.Create();
workspace.WorkspaceFailed += (_, e) =>
{
    if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
        Console.Error.WriteLine($"[krebs][ws] {e.Diagnostic.Message}");
};

var projects = new List<Project>();
if (target.EndsWith(".sln") || target.EndsWith(".slnx") || target.EndsWith(".slnf"))
{
    var sln = await workspace.OpenSolutionAsync(target);
    projects.AddRange(sln.Projects);
}
else
{
    projects.Add(await workspace.OpenProjectAsync(target));
}

Console.Error.WriteLine($"[krebs] loaded {projects.Count} project(s)");

var findings = new List<Finding>();

foreach (var project in projects)
{
    var compilation = await project.GetCompilationAsync();
    if (compilation is null) continue;

    var idisposable = compilation.GetTypeByMetadataName("System.IDisposable");
    var iasyncDisposable = compilation.GetTypeByMetadataName("System.IAsyncDisposable");
    if (idisposable is null) continue;

    foreach (var tree in compilation.SyntaxTrees)
    {
        if (tree.FilePath.Contains("/obj/") || tree.FilePath.Contains("\\obj\\")) continue;

        var model = compilation.GetSemanticModel(tree);
        var root = await tree.GetRootAsync();

        DetectFieldKeptLeaks(root, model, idisposable, iasyncDisposable, findings);
        DetectLocalLeaks(root, model, idisposable, findings);
    }
}

var ordered = findings
    .GroupBy(f => (f.File, f.Line, f.Rule))
    .Select(g => g.First())
    .OrderBy(f => f.File).ThenBy(f => f.Line)
    .ToList();

if (asJson)
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ordered,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
else
{
    foreach (var f in ordered)
        Console.WriteLine($"{f.Rule}  {Short(f.File)}:{f.Line}  {f.Message}");
    Console.WriteLine();
    Console.WriteLine($"[krebs] {ordered.Count} finding(s): "
        + $"{ordered.Count(f => f.Rule == "DK002-field-kept")} field-kept, "
        + $"{ordered.Count(f => f.Rule == "DK001-local")} local.");
}

return ordered.Count == 0 ? 0 : 1;

// ── Detector B: IDisposable stored in a field the owner never disposes ──────────────────────────
static void DetectFieldKeptLeaks(SyntaxNode root, SemanticModel model,
    INamedTypeSymbol idisposable, INamedTypeSymbol? iasyncDisposable, List<Finding> findings)
{
    foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
    {
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSymbol) continue;

        var disposableFields = typeSymbol.GetMembers().OfType<IFieldSymbol>()
            .Where(f => HoldsDisposable(f.Type, idisposable, iasyncDisposable))
            .Select(f => f.Name)
            .ToHashSet();
        if (disposableFields.Count == 0) continue;

        // Which of those fields receive a freshly-created disposable inside this type? Covers both
        // `new X()` and target-typed `new()` (BaseObjectCreationExpression) so field initializers like
        // `private readonly SemaphoreSlim _lock = new(1, 1);` are seen, not just method-body creations.
        var seeded = new Dictionary<string, Location>();
        foreach (var creation in typeDecl.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            var created = model.GetTypeInfo(creation).Type;
            if (created is null || !Implements(created, idisposable, iasyncDisposable)) continue;

            var fieldName = FieldSinkOf(creation, typeDecl, model, disposableFields);
            if (fieldName is not null && !seeded.ContainsKey(fieldName))
                seeded[fieldName] = creation.GetLocation();
        }
        if (seeded.Count == 0) continue;

        var disposedNames = DisposedFieldNames(typeDecl);

        foreach (var (fieldName, loc) in seeded)
        {
            if (disposedNames.Contains(fieldName)) continue;
            var lineSpan = loc.GetLineSpan();
            findings.Add(new Finding(
                "DK002-field-kept",
                loc.SourceTree!.FilePath,
                lineSpan.StartLinePosition.Line + 1,
                $"'{typeSymbol.Name}' stores a disposable in '{fieldName}' but never disposes it "
                + $"(no Dispose of '{fieldName}' anywhere in the type)."));
        }
    }
}

// ── Detector A: local IDisposable created and dropped ────────────────────────────────────────────
static void DetectLocalLeaks(SyntaxNode root, SemanticModel model,
    INamedTypeSymbol idisposable, List<Finding> findings)
{
    foreach (var decl in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
    {
        if (decl.UsingKeyword.RawKind != 0) continue; // `using var x = ...` — owned.
        if (decl.Parent is UsingStatementSyntax) continue;

        foreach (var v in decl.Declaration.Variables)
        {
            if (v.Initializer?.Value is not BaseObjectCreationExpressionSyntax creation) continue;
            var created = model.GetTypeInfo(creation).Type;
            if (created is null || !Implements(created, idisposable, null)) continue;

            var name = v.Identifier.Text;
            var block = decl.FirstAncestorOrSelf<BlockSyntax>();
            if (block is null) continue;

            if (LocalEscapesOrIsDisposed(block, name)) continue;

            var lineSpan = creation.GetLocation().GetLineSpan();
            findings.Add(new Finding(
                "DK001-local",
                creation.SyntaxTree.FilePath,
                lineSpan.StartLinePosition.Line + 1,
                $"local '{name}' is a disposable that is neither disposed, returned, nor handed off."));
        }
    }
}

// ── helpers ─────────────────────────────────────────────────────────────────────────────────────
static bool Implements(ITypeSymbol type, INamedTypeSymbol idisposable, INamedTypeSymbol? iasync)
    => SymbolEqualityComparer.Default.Equals(type, idisposable)
       || type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, idisposable)
            || (iasync is not null && SymbolEqualityComparer.Default.Equals(i, iasync)));

// A field "holds" a disposable if it is one, or is a collection whose element is one.
static bool HoldsDisposable(ITypeSymbol fieldType, INamedTypeSymbol idisposable, INamedTypeSymbol? iasync)
{
    if (Implements(fieldType, idisposable, iasync)) return true;
    if (fieldType is INamedTypeSymbol named && named.IsGenericType)
        return named.TypeArguments.Any(t => Implements(t, idisposable, iasync));
    return false;
}

// If `creation` is stored in one of `fieldNames` (direct assign, or `field.Add(...)`, possibly via
// an intervening local), return the field name. Uses the semantic model to resolve locals to fields.
static string? FieldSinkOf(BaseObjectCreationExpressionSyntax creation, TypeDeclarationSyntax typeDecl,
    SemanticModel model, HashSet<string> fieldNames)
{
    // _field = new X();  /  this._field = new X();
    if (creation.Parent is AssignmentExpressionSyntax asg && asg.Right == creation)
    {
        var target = NameOfTarget(asg.Left);
        if (target is not null && fieldNames.Contains(target)) return target;
    }

    // _field.Add(new X());  — creation is the direct argument.
    if (DirectAddSink(creation, fieldNames) is { } direct) return direct;

    if (creation.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax vd })
    {
        // Field initializer:  private readonly X _field = new(...);  — the declarator sits under a
        // FieldDeclaration (a local declaration sits under a LocalDeclarationStatement instead).
        if (vd.Parent?.Parent is FieldDeclarationSyntax && fieldNames.Contains(vd.Identifier.Text))
            return vd.Identifier.Text;

        // var w = new X(); _field.Add(w);  — creation initialises a local later Add-ed to a field.
        var localName = vd.Identifier.Text;
        foreach (var inv in typeDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" or "Push" or "Enqueue" } ma)
                continue;
            var fieldTarget = NameOfTarget(ma.Expression);
            if (fieldTarget is null || !fieldNames.Contains(fieldTarget)) continue;
            var arg = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (arg is IdentifierNameSyntax id && id.Identifier.Text == localName) return fieldTarget;
        }
    }
    return null;
}

static string? DirectAddSink(BaseObjectCreationExpressionSyntax creation, HashSet<string> fieldNames)
{
    if (creation.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } }
        && inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" or "Push" or "Enqueue" } ma)
    {
        var target = NameOfTarget(ma.Expression);
        if (target is not null && fieldNames.Contains(target)) return target;
    }
    return null;
}

static string? NameOfTarget(ExpressionSyntax expr) => expr switch
{
    IdentifierNameSyntax id => id.Identifier.Text,
    MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n } => n.Identifier.Text,
    _ => null,
};

// Field names the type disposes anywhere: `field.Dispose()/DisposeAsync()/Close()` or
// `foreach (var x in field) x.Dispose()`.
static HashSet<string> DisposedFieldNames(TypeDeclarationSyntax typeDecl)
{
    var disposed = new HashSet<string>();
    foreach (var inv in typeDecl.DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        // Resolve both `field.Dispose()` (member access) and `field?.Dispose()` (the null-conditional
        // idiom, whose invocation binds through a MemberBindingExpression under a ConditionalAccess).
        string method;
        ExpressionSyntax targetExpr;
        switch (inv.Expression)
        {
            case MemberAccessExpressionSyntax ma:            // field.Dispose()
                method = ma.Name.Identifier.Text;
                targetExpr = ma.Expression;
                break;
            case MemberBindingExpressionSyntax mb:           // field?.Dispose()
                var ca = inv.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>();
                if (ca is null) continue;
                method = mb.Name.Identifier.Text;
                targetExpr = ca.Expression;
                break;
            default:
                continue;
        }
        if (method is not ("Dispose" or "DisposeAsync" or "Close")) continue;

        var direct = NameOfTarget(targetExpr);
        if (direct is not null) disposed.Add(direct);

        // x.Dispose() inside `foreach (var x in _field)` — element disposal.
        if (targetExpr is IdentifierNameSyntax loopVar)
        {
            var fe = inv.Ancestors().OfType<ForEachStatementSyntax>()
                .FirstOrDefault(f => f.Identifier.Text == loopVar.Identifier.Text);
            var src = fe is null ? null : NameOfTarget(fe.Expression);
            if (src is not null) disposed.Add(src);
        }
    }
    return disposed;
}

// Rough escape/dispose analysis for a local: disposed, returned, or handed off.
static bool LocalEscapesOrIsDisposed(BlockSyntax block, string name)
{
    foreach (var node in block.DescendantNodes())
    {
        switch (node)
        {
            case ReturnStatementSyntax ret when Mentions(ret.Expression, name):
                return true;
            case AssignmentExpressionSyntax asg when Mentions(asg.Right, name):
                return true; // stored elsewhere (field/out/other) — ownership moves.
            case ArgumentSyntax arg when Mentions(arg.Expression, name):
                return true; // passed to something that may take ownership — be conservative.
            // x.Dispose()/Close()/Stop() — Stop() releases the socket family (TcpListener/HttpListener),
            // the common free-port idiom, so treat it as ownership-release to avoid that false positive.
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }
                when ma.Expression is IdentifierNameSyntax id && id.Identifier.Text == name
                     && ma.Name.Identifier.Text is "Dispose" or "DisposeAsync" or "Close" or "Stop":
                return true;
            // x?.Dispose() — the null-conditional idiom binds through a MemberBindingExpression.
            case ConditionalAccessExpressionSyntax ca
                when ca.Expression is IdentifierNameSyntax cid && cid.Identifier.Text == name
                     && ca.WhenNotNull is InvocationExpressionSyntax { Expression: MemberBindingExpressionSyntax mb }
                     && mb.Name.Identifier.Text is "Dispose" or "DisposeAsync" or "Close" or "Stop":
                return true;
            case UsingStatementSyntax us when Mentions(us.Expression, name):
                return true;
        }
    }
    return false;
}

static bool Mentions(ExpressionSyntax? expr, string name)
    => expr is not null && expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
        .Any(id => id.Identifier.Text == name);

static string Short(string path)
{
    var i = path.IndexOf("/src/", StringComparison.Ordinal);
    if (i >= 0) return path[(i + 1)..];
    i = path.IndexOf("\\src\\", StringComparison.Ordinal);
    return i >= 0 ? path[(i + 1)..] : path;
}

record Finding(string Rule, string File, int Line, string Message);
