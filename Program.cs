using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;
using ModelContextProtocol.Server;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace RoslynMcp.Cs.SingleFile;

public record SimplePosition(int Line, int Column);
public record SimpleSpan(SimplePosition Start, SimplePosition End);
public record FileLocation(string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn, string? Context = null);
public record SymbolInfo(string Name, string Kind, string Accessibility, string Modifiers, string ContainingType, string ContainingNamespace, List<string> BaseTypes, List<string> Interfaces, List<string> Members, List<string> Parameters, string ReturnType, string? Documentation, List<FileLocation> Locations);
public record DiagnosticInfo(string Id, string Severity, string Message, string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn);
public record CodeMetricInfo(string SymbolName, int CyclomaticComplexity, int LinesOfCode, double? MaintainabilityIndex, int ClassCoupling, int DepthOfInheritance);
public record ReferenceResult(string FilePath, int StartLine, int StartColumn, int EndLine, int EndColumn, bool IsDefinition, bool IsWrite, string? ContextSnippet);
public record HierarchyItem(string Name, string Kind, string? FilePath, int? StartLine, int? StartColumn);
public record ChangePreview(string FilePath, string? Original, string? Modified, string DiffSummary);
public record DataFlowResult(List<string> AlwaysAssigned, List<string> Captured, List<string> DataFlowsIn, List<string> DataFlowsOut, List<string> Declared, List<string> Read, List<string> Written);
public record ControlFlowResult(bool StartPointReachable, bool EndPointReachable, List<FileLocation> ReturnStatements, List<FileLocation> ExitPoints, bool Succeeded);
public record ApplyResult(List<string> ChangedDocuments, List<ChangePreview> Changes);
public record PreviewOutput(bool Success, string Message, List<ChangePreview> Changes, int FilesModified);
public record ProjectInfo(string Name, string? FilePath, string Language, int DocumentCount, int MetadataReferences, int ProjectReferences);
public record SolutionInfo(string SolutionPath, int ProjectCount, int DocumentCount, List<ProjectInfo> Projects);

public enum RoslynErrorKind { WorkspaceError, DocumentNotFound, SymbolNotFound, InvalidPosition, RefactoringError, ParseError, IoError }
public class RoslynError : Exception { public RoslynErrorKind Kind { get; } public RoslynError(RoslynErrorKind kind, string message) : base(message) { Kind = kind; } }

public static class RoslynHelpers
{
    public static string NormalizePath(string path) => Path.GetFullPath(path).Replace('\\', '/');
    public static TextSpan? TextSpanFromLines(int startLine, int? startCol, int endLine, int? endCol, SourceText sourceText)
    {
        try
        {
            int sl = startLine, el = endLine;
            if (sl < 0 || sl >= sourceText.Lines.Count || el < 0 || el >= sourceText.Lines.Count) return null;
            int s = sourceText.Lines[sl].Start + (startCol ?? 0);
            int e = sourceText.Lines[el].Start + (endCol ?? sourceText.Lines[el].Span.Length);
            return s <= e ? TextSpan.FromBounds(s, e) : null;
        }
        catch { return null; }
    }

    public static FileLocation? GetLocationInfo(Location location)
    {
        if (location.IsInSource && location.SourceTree != null)
        {
            var lp = location.GetLineSpan();
            return new FileLocation(NormalizePath(location.SourceTree.FilePath), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character);
        }
        return null;
    }

    public static List<FileLocation> SymbolToFileLocations(ISymbol symbol)
    {
        var results = new List<FileLocation>();
        foreach (var loc in symbol.Locations) { var info = GetLocationInfo(loc); if (info != null) results.Add(info); }
        foreach (var sref in symbol.DeclaringSyntaxReferences)
        {
            var tree = sref.SyntaxTree;
            if (tree != null) { var lp = tree.GetLineSpan(sref.Span); results.Add(new FileLocation(NormalizePath(tree.FilePath), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character)); }
        }
        return results;
    }

    public static string SymbolKindString(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol nt => nt.TypeKind switch { TypeKind.Class => "class", TypeKind.Interface => "interface", TypeKind.Struct => "struct", TypeKind.Enum => "enum", TypeKind.Delegate => "delegate", TypeKind.Error => "error", _ => "type" },
        IMethodSymbol m => m.MethodKind switch { MethodKind.Constructor => "constructor", MethodKind.StaticConstructor => "static constructor", MethodKind.Destructor => "destructor", MethodKind.PropertyGet => "getter", MethodKind.PropertySet => "setter", MethodKind.UserDefinedOperator => "operator", MethodKind.Conversion => "conversion", MethodKind.LocalFunction => "local function", MethodKind.EventAdd => "event add", MethodKind.EventRemove => "event remove", _ => "method" },
        IPropertySymbol => "property", IFieldSymbol => "field", IEventSymbol => "event", IParameterSymbol => "parameter", ILocalSymbol => "local", IRangeVariableSymbol => "range variable", ITypeParameterSymbol => "type parameter", _ => "symbol"
    };

    public static string SymbolAccessibilityString(ISymbol symbol) => symbol.DeclaredAccessibility switch
    { Accessibility.Public => "public", Accessibility.Private => "private", Accessibility.Protected => "protected", Accessibility.Internal => "internal", Accessibility.ProtectedOrInternal => "protected internal", Accessibility.ProtectedAndInternal => "private protected", _ => "" };

    public static string SymbolModifiersString(ISymbol symbol)
    {
        var mods = new List<string>();
        if (symbol.IsStatic) mods.Add("static"); if (symbol.IsAbstract) mods.Add("abstract"); if (symbol.IsVirtual) mods.Add("virtual");
        if (symbol.IsOverride) mods.Add("override"); if (symbol.IsSealed) mods.Add("sealed"); if (symbol.IsExtern) mods.Add("extern");
        if (symbol is IFieldSymbol f && f.IsReadOnly) mods.Add("readonly"); if (symbol is IPropertySymbol p && p.IsReadOnly) mods.Add("readonly");
        if (symbol is IFieldSymbol fc && fc.IsConst) mods.Add("const");
        return string.Join(" ", mods);
    }

    public static string GetContainingTypeName(ISymbol symbol) => symbol.ContainingType?.ToDisplayString() ?? "";
    public static string GetContainingNamespaceName(ISymbol symbol) => symbol.ContainingNamespace?.ToDisplayString() ?? "";
    public static List<string> GetBaseTypes(INamedTypeSymbol symbol)
    {
        var result = new List<string>();
        if (symbol.BaseType != null && symbol.BaseType.SpecialType != SpecialType.System_Object) result.Add(symbol.BaseType.ToDisplayString());
        result.AddRange(symbol.AllInterfaces.Select(i => i.ToDisplayString()));
        return result;
    }
    public static List<string> GetTypeMembers(INamedTypeSymbol symbol) => symbol.GetMembers().Where(m => !m.IsImplicitlyDeclared && !m.IsOverride && (m is IPropertySymbol || m is IMethodSymbol || m is IFieldSymbol || m is IEventSymbol)).Select(m => $"{SymbolKindString(m)} {m.Name}").ToList();
}

public class CyclomaticComplexityWalker : CSharpSyntaxWalker
{
    public CyclomaticComplexityWalker() : base(SyntaxWalkerDepth.Node) { }
    public int Complexity { get; set; } = 1;
    public override void VisitIfStatement(IfStatementSyntax node) { Complexity++; base.VisitIfStatement(node); }
    public override void VisitForStatement(ForStatementSyntax node) { Complexity++; base.VisitForStatement(node); }
    public override void VisitForEachStatement(ForEachStatementSyntax node) { Complexity++; base.VisitForEachStatement(node); }
    public override void VisitWhileStatement(WhileStatementSyntax node) { Complexity++; base.VisitWhileStatement(node); }
    public override void VisitDoStatement(DoStatementSyntax node) { Complexity++; base.VisitDoStatement(node); }
    public override void VisitCasePatternSwitchLabel(CasePatternSwitchLabelSyntax node) { Complexity++; base.VisitCasePatternSwitchLabel(node); }
    public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node) { Complexity++; base.VisitCaseSwitchLabel(node); }
    public override void VisitConditionalExpression(ConditionalExpressionSyntax node) { Complexity++; base.VisitConditionalExpression(node); }
    public override void VisitCatchClause(CatchClauseSyntax node) { Complexity++; base.VisitCatchClause(node); }
    public override void VisitBinaryExpression(BinaryExpressionSyntax node) { if (node.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression) Complexity++; base.VisitBinaryExpression(node); }
    public override void VisitContinueStatement(ContinueStatementSyntax node) { Complexity++; base.VisitContinueStatement(node); }
    public override void VisitGotoStatement(GotoStatementSyntax node) { Complexity++; base.VisitGotoStatement(node); }
}

public class LoadedWorkspace { public required MSBuildWorkspace Workspace { get; init; } public required Solution Solution { get; set; } public DateTime CreatedAt { get; init; } = DateTime.UtcNow; }

public static class WorkspaceService
{
    private static readonly ConcurrentDictionary<string, LoadedWorkspace> Workspaces = new();

    private static MSBuildWorkspace CreateWorkspace()
    {
        var ws = MSBuildWorkspace.Create(new Dictionary<string, string>());
        ws.WorkspaceFailed += (_, e) => { if (e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure) Console.Error.WriteLine($"[RoslynMcp.Cs] Workspace failure: {e.Diagnostic.Message}"); };
        return ws;
    }

    public static async Task<LoadedWorkspace> LoadSolutionAsync(string solutionPath)
    {
        var path = RoslynHelpers.NormalizePath(solutionPath);
        if (Workspaces.TryGetValue(path, out var existing)) return existing;
        if (!File.Exists(path))
        {
            var alt = Path.ChangeExtension(path, ".sln");
            var lower = Path.ChangeExtension(path, ".slnx");
            if (File.Exists(alt)) return Workspaces.GetOrAdd(RoslynHelpers.NormalizePath(alt), _ => { var w = CreateWorkspace(); return new LoadedWorkspace { Workspace = w, Solution = w.CurrentSolution }; });
            if (File.Exists(lower)) return Workspaces.GetOrAdd(RoslynHelpers.NormalizePath(lower), _ => { var w = CreateWorkspace(); return new LoadedWorkspace { Workspace = w, Solution = w.CurrentSolution }; });
            throw new RoslynError(RoslynErrorKind.WorkspaceError, $"Solution file not found: {path}");
        }
        var wsNew = Workspaces.GetOrAdd(path, _ => { var w = CreateWorkspace(); return new LoadedWorkspace { Workspace = w, Solution = w.CurrentSolution }; });
        var solution = await wsNew.Workspace.OpenSolutionAsync(path);
        wsNew.Solution = solution;
        Workspaces[RoslynHelpers.NormalizePath(path)] = wsNew;
        return wsNew;
    }

    public static async Task<LoadedWorkspace> LoadProjectAsync(string projectPath)
    {
        var path = RoslynHelpers.NormalizePath(projectPath);
        if (!File.Exists(path)) throw new RoslynError(RoslynErrorKind.WorkspaceError, $"Project file not found: {path}");
        var ws = Workspaces.GetOrAdd(path, _ => { var w = CreateWorkspace(); return new LoadedWorkspace { Workspace = w, Solution = w.CurrentSolution }; });
        var project = await ws.Workspace.OpenProjectAsync(path);
        ws.Solution = project.Solution;
        return ws;
    }

    public static Task<LoadedWorkspace> LoadAsync(string path) => Path.GetExtension(path).ToLowerInvariant() == ".csproj" ? LoadProjectAsync(path) : LoadSolutionAsync(path);

    public static Document? GetDocument(Solution solution, string filePath)
    {
        var normalized = RoslynHelpers.NormalizePath(filePath);
        return solution.Projects.SelectMany(p => p.Documents).FirstOrDefault(d => { var dfp = RoslynHelpers.NormalizePath(d.FilePath); return dfp == normalized || dfp.EndsWith("/" + normalized) || dfp.EndsWith("\\" + normalized); });
    }

    public static ISymbol? FindSymbolByName(Compilation compilation, string symbolName)
    {
        var exact = new List<ISymbol>(); var candidates = new List<ISymbol>();
        void Visit(INamespaceOrTypeSymbol ns) { foreach (var m in ns.GetMembers()) { if (m.Name == symbolName) exact.Add(m); else if (m.Name.Contains(symbolName, StringComparison.OrdinalIgnoreCase)) candidates.Add(m); if (m is INamespaceOrTypeSymbol n) Visit(n); } }
        Visit(compilation.GlobalNamespace);
        return exact.Count > 0 ? exact[0] : candidates.Count > 0 ? candidates[0] : null;
    }

    public static async Task<SemanticModel?> GetSemanticModelAsync(Document document)
    {
        var sm = await document.GetSemanticModelAsync(); int i = 0;
        while (sm == null && i < 3) { await Task.Delay(50); sm = await document.GetSemanticModelAsync(); i++; }
        return sm;
    }

    public static async Task<ISymbol?> FindSymbolAtPositionAsync(Document document, int line, int column)
    {
        var sm = await GetSemanticModelAsync(document); if (sm == null) return null;
        var root = await document.GetSyntaxRootAsync(); if (root == null) return null;
        var sourceText = root.GetText(); var lines = sourceText.Lines; if (line < 0 || line >= lines.Count) return null;
        var pos = Math.Min(lines[line].Start + column, lines[line].End);
        var token = root.FindToken(pos); var node = root.FindNode(token.Span);
        return sm.GetSymbolInfo(node).Symbol ?? sm.GetDeclaredSymbol(node);
    }

    public static IEnumerable<Document> GetAllDocuments(Solution solution) => solution.Projects.SelectMany(p => p.Documents);

    public static async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document)
    {
        var sm = await GetSemanticModelAsync(document); if (sm == null) return Enumerable.Empty<Diagnostic>();
        var diags = sm.GetDiagnostics(); var root = await document.GetSyntaxRootAsync();
        return root != null ? diags.Concat(root.GetDiagnostics()) : diags;
    }

    public static void DisposeAll() { foreach (var lw in Workspaces.Values) try { lw.Workspace.Dispose(); } catch { } Workspaces.Clear(); }

    public static SolutionInfo GetSolutionInfo(LoadedWorkspace loaded)
    {
        var projects = loaded.Solution.Projects.Select(p => new ProjectInfo(p.Name, p.FilePath, p.Language, Enumerable.Count(p.Documents), Enumerable.Count(p.MetadataReferences), Enumerable.Count(p.ProjectReferences))).ToList();
        return new SolutionInfo(RoslynHelpers.NormalizePath(loaded.Solution.FilePath), loaded.Solution.Projects.Count(), loaded.Solution.Projects.Sum(p => p.Documents.Count()), projects);
    }
}

public static class AnalysisHelpers
{
    public static async Task<List<ReferenceResult>> FindReferencesAsync(Solution solution, ISymbol symbol, int? maxResults)
    {
        try
        {
            if (symbol == null) return new List<ReferenceResult>();
            var refs = await SymbolFinder.FindReferencesAsync(symbol, solution);
            return refs.SelectMany(r => r.Locations).Where(l => l.Location.IsInSource && l.Location.SourceTree != null).Select(l =>
            {
                var lp = l.Location.GetLineSpan(); var tree = l.Location.SourceTree;
                var snippet = tree.GetTextAsync().Result.Lines[Math.Max(0, lp.StartLinePosition.Line)].ToString().Trim();
                return new ReferenceResult(RoslynHelpers.NormalizePath(tree.FilePath), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character, l.Location.IsInSource, l.IsImplicit, snippet);
            }).Take(maxResults ?? 100).ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error finding references: {ex.Message}"); return new List<ReferenceResult>(); }
    }

    public static async Task<List<FileLocation>> FindImplementationsAsync(Solution solution, ISymbol symbol, int? maxResults)
    {
        try
        {
            if (symbol == null) return new List<FileLocation>();
            var impls = await SymbolFinder.FindImplementationsAsync(symbol, solution);
            return impls.SelectMany(impl => (impl is INamedTypeSymbol ? impl : (impl.ContainingType ?? impl)).Locations).Where(l => l.IsInSource && l.SourceTree != null).Select(l =>
            {
                var lp = l.GetLineSpan();
                return new FileLocation(RoslynHelpers.NormalizePath(l.SourceTree.FilePath), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character);
            }).Take(maxResults ?? 50).ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error finding implementations: {ex.Message}"); return new List<FileLocation>(); }
    }

    public static async Task<List<ReferenceResult>> FindCallersAsync(Solution solution, ISymbol symbol, int? maxResults)
    {
        try
        {
            if (symbol == null) return new List<ReferenceResult>();
            var callers = await SymbolFinder.FindCallersAsync(symbol, solution);
            return callers.SelectMany(c => c.Locations.Select(l => (loc: l, symbol: c.CallingSymbol))).Where(x => x.loc.IsInSource && x.loc.SourceTree != null).Select(x =>
            {
                var lp = x.loc.GetLineSpan();
                return new ReferenceResult(RoslynHelpers.NormalizePath(x.loc.SourceTree.FilePath), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character, false, false, $"Called by: {x.symbol.ToDisplayString()} in {x.symbol.ContainingSymbol.ToDisplayString()}");
            }).Take(maxResults ?? 100).ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error finding callers: {ex.Message}"); return new List<ReferenceResult>(); }
    }

    public static async Task<List<HierarchyItem>> GetTypeHierarchyAsync(Solution solution, INamedTypeSymbol symbol, string direction)
    {
        try
        {
            var results = new List<HierarchyItem>();
            HierarchyItem Make(INamedTypeSymbol s) { var loc = s.Locations.FirstOrDefault(); return new HierarchyItem(s.ToDisplayString(), RoslynHelpers.SymbolKindString(s), loc.IsInSource && loc.SourceTree != null ? loc.SourceTree.FilePath : null, loc.IsInSource ? loc.GetLineSpan().StartLinePosition.Line : null, loc.IsInSource ? loc.GetLineSpan().StartLinePosition.Character : null); }
            if (direction is "BaseTypes" or "Both") { var c = symbol.BaseType; while (c != null && c.SpecialType != SpecialType.System_Object) { results.Add(Make(c)); c = c.BaseType; } results.AddRange(symbol.AllInterfaces.Select(Make)); }
            if (direction is "DerivedTypes" or "Both") results.AddRange((await SymbolFinder.FindDerivedClassesAsync(symbol, solution)).Select(Make));
            return results;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error getting hierarchy: {ex.Message}"); return new List<HierarchyItem>(); }
    }

    public static async Task<List<SymbolInfo>> SearchSymbolsAsync(Solution solution, string query, string? kindFilter, int? maxResults)
    {
        try
        {
            var results = new List<SymbolInfo>();
            foreach (var project in solution.Projects)
            {
                var compilation = await project.GetCompilationAsync(); if (compilation == null) continue;
                var symbols = new List<ISymbol>();
                void Collect(INamespaceOrTypeSymbol ns) { foreach (var m in ns.GetMembers()) { if (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && (kindFilter == null || (kindFilter == "class" && m is INamedTypeSymbol { TypeKind: TypeKind.Class }) || (kindFilter == "interface" && m is INamedTypeSymbol { TypeKind: TypeKind.Interface }) || (kindFilter == "struct" && m is INamedTypeSymbol { TypeKind: TypeKind.Struct }) || (kindFilter == "method" && m is IMethodSymbol) || (kindFilter == "property" && m is IPropertySymbol) || (kindFilter == "field" && m is IFieldSymbol) || (kindFilter == "enum" && m is INamedTypeSymbol { TypeKind: TypeKind.Enum }))) symbols.Add(m); if (m is INamespaceOrTypeSymbol n) Collect(n); } }
                Collect(compilation.GlobalNamespace);
                foreach (var symbol in symbols.Take(maxResults ?? 200)) results.Add(new SymbolInfo(symbol.ToDisplayString(), RoslynHelpers.SymbolKindString(symbol), RoslynHelpers.SymbolAccessibilityString(symbol), RoslynHelpers.SymbolModifiersString(symbol), RoslynHelpers.GetContainingTypeName(symbol), RoslynHelpers.GetContainingNamespaceName(symbol), new List<string>(), new List<string>(), new List<string>(), new List<string>(), "", symbol.GetDocumentationCommentXml() is { Length: > 0 } d ? d : null, RoslynHelpers.SymbolToFileLocations(symbol)));
            }
            return results;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error searching symbols: {ex.Message}"); return new List<SymbolInfo>(); }
    }

    public static async Task<List<SymbolInfo>> GetDocumentOutlineAsync(Document document)
    {
        try
        {
            var sm = await WorkspaceService.GetSemanticModelAsync(document); if (sm == null) return new List<SymbolInfo>();
            var root = await document.GetSyntaxRootAsync(); if (root == null) return new List<SymbolInfo>();
            return root.DescendantNodes().Select(n => sm.GetDeclaredSymbol(n)).Where(s => s != null).Select(s => new SymbolInfo(s.Name, RoslynHelpers.SymbolKindString(s), RoslynHelpers.SymbolAccessibilityString(s), RoslynHelpers.SymbolModifiersString(s), RoslynHelpers.GetContainingTypeName(s), RoslynHelpers.GetContainingNamespaceName(s), new List<string>(), new List<string>(), new List<string>(), s is IMethodSymbol m ? m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList() : new List<string>(), s is IMethodSymbol mr && mr.ReturnType != null ? mr.ReturnType.ToDisplayString() : s is IPropertySymbol pr && pr.Type != null ? pr.Type.ToDisplayString() : "", s.GetDocumentationCommentXml() is { Length: > 0 } d ? d : null, new List<FileLocation>())).ToList();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error getting outline: {ex.Message}"); return new List<SymbolInfo>(); }
    }

    public static async Task<DataFlowResult?> AnalyzeDataFlowAsync(Document document, int startLine, int? startCol, int endLine, int? endCol)
    {
        try
        {
            var sm = await WorkspaceService.GetSemanticModelAsync(document); if (sm == null) return null;
            var root = await document.GetSyntaxRootAsync(); if (root == null) return null;
            var sourceText = await document.GetTextAsync();
            var span = RoslynHelpers.TextSpanFromLines(startLine, startCol, endLine, endCol, sourceText);
            var node = span.HasValue ? root.FindNode(span.Value) : root;
            var a = sm.AnalyzeDataFlow(node);
            List<string> N(IEnumerable<ISymbol> s) => s.Select(x => x.Name).ToList();
            return new DataFlowResult(N(a.AlwaysAssigned), N(a.Captured), N(a.DataFlowsIn), N(a.DataFlowsOut), N(a.VariablesDeclared), N(a.ReadInside), N(a.WrittenInside));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error analyzing data flow: {ex.Message}"); return null; }
    }

    public static async Task<ControlFlowResult?> AnalyzeControlFlowAsync(Document document, int startLine, int? startCol, int endLine, int? endCol)
    {
        try
        {
            var sm = await WorkspaceService.GetSemanticModelAsync(document); if (sm == null) return null;
            var root = await document.GetSyntaxRootAsync(); if (root == null) return null;
            var sourceText = await document.GetTextAsync();
            var span = RoslynHelpers.TextSpanFromLines(startLine, startCol, endLine, endCol, sourceText);
            var node = span.HasValue ? root.FindNode(span.Value) : root;
            var a = sm.AnalyzeControlFlow(node);
            FileLocation L(SyntaxNode n) { var lp = n.GetLocation().GetLineSpan(); return new FileLocation(RoslynHelpers.NormalizePath(document.FilePath), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character); }
            return new ControlFlowResult(a.StartPointIsReachable, a.EndPointIsReachable, a.ReturnStatements.Select(L).ToList(), a.ExitPoints.Select(L).ToList(), a.Succeeded);
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error analyzing control flow: {ex.Message}"); return null; }
    }

    public static async Task<List<CodeMetricInfo>> CalculateMetricsAsync(Document document, string? symbolName, int? line)
    {
        try
        {
            var sm = await WorkspaceService.GetSemanticModelAsync(document); if (sm == null) return new List<CodeMetricInfo>();
            var root = await document.GetSyntaxRootAsync(); if (root == null) return new List<CodeMetricInfo>();
            var results = new List<CodeMetricInfo>();

            CodeMetricInfo ForMethod(MethodDeclarationSyntax m)
            {
                var w = new CyclomaticComplexityWalker(); w.Visit(m);
                var types = new HashSet<string>(); foreach (var id in m.DescendantNodes().OfType<IdentifierNameSyntax>()) { var info = sm.GetSymbolInfo(id); if (info.Symbol?.ContainingType != null) types.Add(info.Symbol.ContainingType.ToDisplayString()); }
                return new CodeMetricInfo(m.Identifier.Text, w.Complexity, Math.Max(1, m.GetText().Lines.Count), null, types.Count, 0);
            }

            CodeMetricInfo ForClass(ClassDeclarationSyntax c)
            {
                var mets = c.DescendantNodes().OfType<MethodDeclarationSyntax>().Select(ForMethod).ToList();
                var avg = mets.Count == 0 ? 0.0 : mets.Average(x => (double)x.CyclomaticComplexity);
                var types = new HashSet<string>(); foreach (var id in c.DescendantNodes().OfType<IdentifierNameSyntax>()) { var info = sm.GetSymbolInfo(id); if (info.Symbol?.ContainingType != null) types.Add(info.Symbol.ContainingType.ToDisplayString()); }
                return new CodeMetricInfo(c.Identifier.Text, (int)avg, c.GetText().Lines.Count, null, types.Count, 0);
            }

            if (symbolName != null)
            {
                var target = root.DescendantNodes().FirstOrDefault(n => (n is MethodDeclarationSyntax md && md.Identifier.Text == symbolName) || (n is ClassDeclarationSyntax cd && cd.Identifier.Text == symbolName) || (n is PropertyDeclarationSyntax pd && pd.Identifier.Text == symbolName));
                if (target is MethodDeclarationSyntax meth) results.Add(ForMethod(meth));
                else if (target is PropertyDeclarationSyntax) results.Add(new CodeMetricInfo(symbolName, 1, target.GetText().Lines.Count, null, 0, 0));
                else foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == symbolName)) results.Add(ForClass(cls));
            }
            else foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>()) results.Add(ForClass(cls));
            return results;
        }
        catch (Exception ex) { Console.Error.WriteLine($"[RoslynMcp.Cs] Error calculating metrics: {ex.Message}"); return new List<CodeMetricInfo>(); }
    }
}

public static class PreviewService
{
    public static string SimpleDiff(string original, string modified)
    {
        var o = original.Split('\n'); var m = modified.Split('\n'); var sb = new StringBuilder();
        for (int i = 0; i < Math.Max(o.Length, m.Length); i++)
        {
            if (i < o.Length && i < m.Length) { if (o[i] != m[i]) { sb.AppendLine($"- {o[i].TrimEnd()}"); sb.AppendLine($"+ {m[i].TrimEnd()}"); } }
            else if (i < o.Length) sb.AppendLine($"- {o[i].TrimEnd()}");
            else if (i < m.Length) sb.AppendLine($"+ {m[i].TrimEnd()}");
        }
        return sb.ToString().TrimEnd();
    }

    public static async Task<List<ChangePreview>> ComputeChangesAsync(Solution original, Solution modified)
    {
        var changes = new List<ChangePreview>();
        foreach (var pc in modified.GetChanges(original).GetProjectChanges().Concat(original.GetChanges(modified).GetProjectChanges()))
        {
            foreach (var id in pc.GetChangedDocuments())
            {
                var od = original.GetDocument(id); var nd = modified.GetDocument(id);
                if (od != null && nd != null) { var ot = await od.GetTextAsync(); var nt = await nd.GetTextAsync(); changes.Add(new ChangePreview(RoslynHelpers.NormalizePath(od.FilePath), ot.ToString(), nt.ToString(), SimpleDiff(ot.ToString(), nt.ToString()))); }
            }
            foreach (var id in pc.GetAddedDocuments())
            {
                var nd = modified.GetDocument(id);
                if (nd != null) { var nt = await nd.GetTextAsync(); changes.Add(new ChangePreview(RoslynHelpers.NormalizePath(nd.FilePath), null, nt.ToString(), $"+ New file: {nd.FilePath}")); }
            }
        }
        return changes;
    }

    public static async Task<ApplyResult> ApplyToDiskAsync(Solution original, Solution modified)
    {
        var changes = await ComputeChangesAsync(original, modified);
        var backups = changes.Select(c => (path: Path.GetFullPath(c.FilePath), content: File.Exists(c.FilePath) ? File.ReadAllText(c.FilePath) : null)).ToList();
        try
        {
            foreach (var pc in modified.GetChanges(original).GetProjectChanges())
                foreach (var id in pc.GetChangedDocuments())
                {
                    var nd = modified.GetDocument(id);
                    if (nd != null) File.WriteAllText(RoslynHelpers.NormalizePath(nd.FilePath), (await nd.GetTextAsync()).ToString());
                }
            return new ApplyResult(changes.Select(c => c.FilePath).ToList(), changes);
        }
        catch (Exception ex) { foreach (var (p, c) in backups) { if (c != null) File.WriteAllText(p, c); else if (File.Exists(p)) File.Delete(p); } throw new RoslynError(RoslynErrorKind.IoError, $"Failed to write changes, rolled back. Error: {ex.Message}"); }
    }

    public static async Task<PreviewOutput> PreviewChangesAsync(Solution original, Solution modified)
    {
        var changes = await ComputeChangesAsync(original, modified);
        return new PreviewOutput(true, changes.Count == 0 ? "No changes detected" : $"Preview: {changes.Count} file(s) would be modified", changes, changes.Count);
    }
}

public static class JsonHelper
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

[McpServerToolType]
public static class NavigationTools
{
    [McpServerTool, Description("Navigate to the source definition of a symbol.")]
    public static async Task<string> GoToDefinition([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to the source file")] string sourceFile, [Description("Line number (0-based)")] int line = 0, [Description("Column number (0-based)")] int column = 0, [Description("Symbol name (alternative to line/column)")] string? symbolName = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath);
        var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc);
        if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = symbolName != null ? $"Symbol not found: {symbolName}" : "No symbol found at position" });
        return JsonHelper.Serialize(RoslynHelpers.SymbolToFileLocations(symbol));
    }

    [McpServerTool, Description("Find all references to a symbol across the entire solution.")]
    public static async Task<string> FindReferences([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to the source file")] string sourceFile, [Description("Line number (0-based)")] int line = 0, [Description("Column number (0-based)")] int column = 0, [Description("Symbol name (alternative to line/column)")] string? symbolName = null, [Description("Maximum number of results")] int? maxResults = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = symbolName != null ? $"Symbol not found: {symbolName}" : "No symbol found" });
        return JsonHelper.Serialize(await AnalysisHelpers.FindReferencesAsync(doc.Project.Solution, symbol, maxResults));
    }

    [McpServerTool, Description("Find all implementations of an interface or overrides of an abstract/virtual member.")]
    public static async Task<string> FindImplementations([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to the source file")] string sourceFile, [Description("Line number (0-based)")] int line = 0, [Description("Column number (0-based)")] int column = 0, [Description("Symbol name (alternative to line/column)")] string? symbolName = null, [Description("Maximum number of results")] int? maxResults = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = symbolName != null ? $"Symbol not found: {symbolName}" : "No symbol found" });
        return JsonHelper.Serialize(await AnalysisHelpers.FindImplementationsAsync(doc.Project.Solution, symbol, maxResults));
    }

    [McpServerTool, Description("Search for symbols by name pattern across the entire workspace.")]
    public static async Task<string> SearchSymbols([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Search query")] string query, [Description("Kind filter: class, interface, struct, method, property, field, enum")] string? kindFilter = null, [Description("Maximum number of results")] int? maxResults = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath);
        return JsonHelper.Serialize(await AnalysisHelpers.SearchSymbolsAsync(loaded.Solution, query, kindFilter, maxResults));
    }

    [McpServerTool, Description("Get detailed metadata for any symbol.")]
    public static async Task<string> GetSymbolInfo([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to the source file")] string sourceFile, [Description("Line number (0-based)")] int line = 0, [Description("Column number (0-based)")] int column = 0, [Description("Symbol name (alternative to line/column)")] string? symbolName = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = symbolName != null ? $"Symbol not found: {symbolName}" : "No symbol found" });
        var info = new SymbolInfo(symbol.ToDisplayString(), RoslynHelpers.SymbolKindString(symbol), RoslynHelpers.SymbolAccessibilityString(symbol), RoslynHelpers.SymbolModifiersString(symbol), RoslynHelpers.GetContainingTypeName(symbol), RoslynHelpers.GetContainingNamespaceName(symbol), symbol is INamedTypeSymbol nt ? RoslynHelpers.GetBaseTypes(nt) : new List<string>(), symbol is INamedTypeSymbol nt2 ? nt2.AllInterfaces.Select(i => i.ToDisplayString()).ToList() : new List<string>(), symbol is INamedTypeSymbol nt3 ? RoslynHelpers.GetTypeMembers(nt3) : new List<string>(), symbol is IMethodSymbol m ? m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList() : new List<string>(), symbol is IMethodSymbol mr && mr.ReturnType != null ? mr.ReturnType.ToDisplayString() : symbol is IPropertySymbol pr && pr.Type != null ? pr.Type.ToDisplayString() : "", symbol.GetDocumentationCommentXml() is { Length: > 0 } d ? d : null, RoslynHelpers.SymbolToFileLocations(symbol));
        return JsonHelper.Serialize(info);
    }

    [McpServerTool, Description("Get a hierarchical outline of all symbols in a file.")]
    public static async Task<string> GetDocumentOutline([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to the source file")] string sourceFile)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        return JsonHelper.Serialize(await AnalysisHelpers.GetDocumentOutlineAsync(doc));
    }

    [McpServerTool, Description("Find all callers of a method, property, or constructor.")]
    public static async Task<string> FindCallers([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to the source file")] string sourceFile, [Description("Line number (0-based)")] int line = 0, [Description("Column number (0-based)")] int column = 0, [Description("Symbol name (alternative to line/column)")] string? symbolName = null, [Description("Maximum number of results")] int? maxResults = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = symbolName != null ? $"Symbol not found: {symbolName}" : "No symbol found" });
        return JsonHelper.Serialize(await AnalysisHelpers.FindCallersAsync(doc.Project.Solution, symbol, maxResults));
    }
}

[McpServerToolType]
public static class AnalysisTools
{
    [McpServerTool, Description("Get compiler diagnostics (errors, warnings, info) for a file or entire solution.")]
    public static async Task<string> GetDiagnostics([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file (optional; omit for all)")] string? sourceFile = null, [Description("Severity filter: Error, Warning, Info, Hidden")] string? severityFilter = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath);
        List<Diagnostic> diags;
        if (sourceFile != null) { var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile); if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" }); diags = (await WorkspaceService.GetDocumentDiagnosticsAsync(doc)).ToList(); }
        else { var tasks = WorkspaceService.GetAllDocuments(loaded.Solution).Select(d => WorkspaceService.GetDocumentDiagnosticsAsync(d)); diags = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList(); }
        var filtered = diags.Where(d => severityFilter == null || d.Severity.ToString().Equals(severityFilter, StringComparison.OrdinalIgnoreCase)).Select(d => { var lp = d.Location.GetLineSpan(); return new DiagnosticInfo(d.Id, d.Severity.ToString(), d.GetMessage(), RoslynHelpers.NormalizePath(d.Location.SourceTree?.FilePath ?? ""), lp.StartLinePosition.Line, lp.StartLinePosition.Character, lp.EndLinePosition.Line, lp.EndLinePosition.Character); }).ToList();
        return JsonHelper.Serialize(filtered);
    }

    [McpServerTool, Description("Calculate code metrics (cyclomatic complexity, lines of code, class coupling) for a file or specific type.")]
    public static async Task<string> GetCodeMetrics([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Symbol name (optional)")] string? symbolName = null, [Description("Line number")] int? line = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        return JsonHelper.Serialize(await AnalysisHelpers.CalculateMetricsAsync(doc, symbolName, line));
    }

    [McpServerTool, Description("Analyze control flow for a code region.")]
    public static async Task<string> AnalyzeControlFlow([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Start line (0-based)")] int startLine, [Description("End line (0-based)")] int endLine, [Description("Start column")] int? startColumn = null, [Description("End column")] int? endColumn = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var result = await AnalysisHelpers.AnalyzeControlFlowAsync(doc, startLine, startColumn, endLine, endColumn);
        return result != null ? JsonHelper.Serialize(result) : JsonHelper.Serialize(new { error = "Could not analyze control flow" });
    }

    [McpServerTool, Description("Analyze data flow for a code region.")]
    public static async Task<string> AnalyzeDataFlow([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Start line (0-based)")] int startLine, [Description("End line (0-based)")] int endLine, [Description("Start column")] int? startColumn = null, [Description("End column")] int? endColumn = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var result = await AnalysisHelpers.AnalyzeDataFlowAsync(doc, startLine, startColumn, endLine, endColumn);
        return result != null ? JsonHelper.Serialize(result) : JsonHelper.Serialize(new { error = "Could not analyze data flow" });
    }

    [McpServerTool, Description("Get base types and derived types for a class or interface.")]
    public static async Task<string> GetTypeHierarchy([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Line number")] int line = 0, [Description("Column number")] int column = 0, [Description("Symbol name")] string? symbolName = null, [Description("Direction: BaseTypes, DerivedTypes, Both")] string? direction = null)
    {
        direction ??= "Both";
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol is INamedTypeSymbol nt) return JsonHelper.Serialize(await AnalysisHelpers.GetTypeHierarchyAsync(doc.Project.Solution, nt, direction));
        return JsonHelper.Serialize(new { error = symbol != null ? "Symbol found is not a named type" : (symbolName != null ? $"Symbol not found: {symbolName}" : "No symbol found at position") });
    }

    [McpServerTool, Description("Show references and callers to understand impact of changing a symbol.")]
    public static async Task<string> AnalyzeChangeImpact([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Line number")] int line = 0, [Description("Column number")] int column = 0, [Description("Symbol name")] string? symbolName = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = "No symbol found" });
        var refs = await AnalysisHelpers.FindReferencesAsync(doc.Project.Solution, symbol, null);
        var callers = await AnalysisHelpers.FindCallersAsync(doc.Project.Solution, symbol, null);
        return JsonHelper.Serialize(new { References = refs, Callers = callers, Symbol = symbol.ToDisplayString(), SymbolKind = RoslynHelpers.SymbolKindString(symbol) });
    }

    [McpServerTool, Description("Check workspace health and environment info.")]
    public static async Task<string> Diagnose([Description("Path to .sln or .csproj (optional)")] string? solutionPath = null, [Description("Include full solution details")] bool verbose = false)
    {
        var env = new { RuntimeVersion = RuntimeInformation.FrameworkDescription, OSVersion = Environment.OSVersion.ToString(), OSArchitecture = RuntimeInformation.OSArchitecture.ToString(), ProcessorCount = Environment.ProcessorCount, WorkingDirectory = Environment.CurrentDirectory };
        if (solutionPath == null) return JsonHelper.Serialize(new { Environment = env, Workspace = (object?)null });
        var loaded = await WorkspaceService.LoadAsync(solutionPath);
        var info = WorkspaceService.GetSolutionInfo(loaded);
        if (verbose) return JsonHelper.Serialize(new { Environment = env, Workspace = info });
        return JsonHelper.Serialize(new { Environment = env, SolutionPath = info.SolutionPath, ProjectCount = info.ProjectCount, DocumentCount = info.DocumentCount, Projects = info.Projects.Select(p => new { p.Name, p.Language, p.DocumentCount }).ToList() });
    }
}

[McpServerToolType]
public static class RefactoringTools
{
    [McpServerTool, Description("Safe rename of a symbol across the entire solution.")]
    public static async Task<string> RenameSymbol([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("New name")] string newName, [Description("Line number")] int line = 0, [Description("Column number")] int column = 0, [Description("Symbol name (alternative)")] string? symbolName = null, [Description("Rename overloads")] bool renameOverloads = false, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var symbol = await WorkspaceService.FindSymbolAtPositionAsync(doc, line, column);
        if (symbol == null && symbolName != null) { var compilation = await doc.Project.GetCompilationAsync(); symbol = WorkspaceService.FindSymbolByName(compilation, symbolName); }
        if (symbol == null) return JsonHelper.Serialize(new { error = "Symbol not found" });
        var newSolution = await Renamer.RenameSymbolAsync(doc.Project.Solution, symbol, new SymbolRenameOptions(RenameOverloads: renameOverloads), newName);
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Extract an interface from a class, copying selected member signatures.")]
    public static async Task<string> ExtractInterface([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Class name")] string typeName, [Description("Interface name")] string? interfaceName = null, [Description("Members to include")] string[]? members = null, [Description("Target file path")] string? targetFile = null, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        interfaceName ??= "I" + typeName;
        var memberFilter = members != null ? new HashSet<string>(members) : new HashSet<string>();
        var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text == typeName);
        if (classDecl == null) return JsonHelper.Serialize(new { error = $"Type not found: {typeName}" });
        bool Filter(MemberDeclarationSyntax m) => memberFilter.Count == 0 || (m is MethodDeclarationSyntax me && memberFilter.Contains(me.Identifier.Text)) || (m is PropertyDeclarationSyntax p && memberFilter.Contains(p.Identifier.Text)) || (m is EventDeclarationSyntax e && memberFilter.Contains(e.Identifier.Text));
        MemberDeclarationSyntax? Map(MemberDeclarationSyntax m) => m is MethodDeclarationSyntax meth ? SyntaxFactory.MethodDeclaration(meth.ReturnType.ToString() == "void" ? SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)) : meth.ReturnType.WithoutTrivia(), meth.Identifier.Text).WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(meth.ParameterList.Parameters.Select(p => p.WithoutTrivia())))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) : m is PropertyDeclarationSyntax prop ? SyntaxFactory.PropertyDeclaration(prop.Type.WithoutTrivia(), prop.Identifier.Text).AddAccessorListAccessors(SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken))) : null;
        var ifaceMembers = classDecl.Members.Where(Filter).Select(Map).Where(x => x != null).Cast<MemberDeclarationSyntax>().ToList();
        if (ifaceMembers.Count == 0) return JsonHelper.Serialize(new { error = "No matching members found" });
        var ifaceDecl = SyntaxFactory.InterfaceDeclaration(interfaceName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddMembers(ifaceMembers.ToArray());
        var nsName = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        var sb = new StringBuilder(); foreach (var u in usings) sb.AppendLine(u.ToString()); sb.AppendLine();
        if (nsName != null) { sb.AppendLine($"namespace {nsName};"); sb.AppendLine(); }
        sb.AppendLine(ifaceDecl.NormalizeWhitespace().ToString());
        var text = sb.ToString();
        if (targetFile != null)
        {
            var fullPath = Path.IsPathRooted(targetFile) ? targetFile : Path.Combine(Path.GetDirectoryName(doc.FilePath) ?? "", targetFile);
            var newDoc = doc.Project.AddDocument(fullPath, text);
            if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newDoc.Project.Solution));
            return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newDoc.Project.Solution));
        }
        else
        {
            var withTrivia = ifaceDecl.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
            var newRoot = root.InsertNodesAfter(classDecl, new SyntaxNode[] { withTrivia });
            var newSolution = doc.Project.Solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
            if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
            return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
        }
    }

    [McpServerTool, Description("Encapsulate a public field by generating a property with getter/setter.")]
    public static async Task<string> EncapsulateField([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Field name")] string fieldName, [Description("Property name")] string? propertyName = null, [Description("Read-only property")] bool readOnly = false, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var fieldDecl = root.DescendantNodes().OfType<FieldDeclarationSyntax>().FirstOrDefault(fd => fd.Declaration.Variables.Any(v => v.Identifier.Text == fieldName));
        if (fieldDecl == null) return JsonHelper.Serialize(new { error = $"Field not found: {fieldName}" });
        propertyName ??= char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
        var getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.IdentifierName(fieldName))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
        var prop = readOnly
            ? SyntaxFactory.PropertyDeclaration(fieldDecl.Declaration.Type.WithoutTrivia(), propertyName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddAccessorListAccessors(getter)
            : SyntaxFactory.PropertyDeclaration(fieldDecl.Declaration.Type.WithoutTrivia(), propertyName).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).AddAccessorListAccessors(getter, SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(fieldName), SyntaxFactory.IdentifierName("value")))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)));
        var p = prop.WithLeadingTrivia(SyntaxFactory.CarriageReturnLineFeed, SyntaxFactory.CarriageReturnLineFeed).WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);
        var newRoot = root.InsertNodesAfter(fieldDecl, new SyntaxNode[] { p });
        var newSolution = doc.Project.Solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Move a type to a separate file.")]
    public static async Task<string> MoveTypeToFile([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Type name")] string symbolName, [Description("Target file path")] string targetFile, [Description("Create target file")] bool createTargetFile = true, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var typeNode = root.DescendantNodes().FirstOrDefault(n => (n is ClassDeclarationSyntax c && c.Identifier.Text == symbolName) || (n is StructDeclarationSyntax s && s.Identifier.Text == symbolName) || (n is InterfaceDeclarationSyntax i && i.Identifier.Text == symbolName) || (n is EnumDeclarationSyntax e && e.Identifier.Text == symbolName) || (n is RecordDeclarationSyntax r && r.Identifier.Text == symbolName));
        if (typeNode == null) return JsonHelper.Serialize(new { error = $"Type not found: {symbolName}" });
        var nsName = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        var typeText = typeNode.NormalizeWhitespace().ToString();
        var sb = new StringBuilder(); foreach (var u in usings) sb.AppendLine(u.ToString()); sb.AppendLine();
        if (nsName != null) { sb.AppendLine($"namespace {nsName};"); sb.AppendLine(); }
        sb.AppendLine(typeText);
        var content = sb.ToString();
        var newRoot = root.RemoveNode(typeNode, SyntaxRemoveOptions.KeepNoTrivia);
        if (newRoot == null) return JsonHelper.Serialize(new { error = "Failed to remove type from source file." });
        var solutionAfter = doc.Project.Solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
        if (createTargetFile)
        {
            var fullPath = Path.IsPathRooted(targetFile) ? targetFile : Path.Combine(Path.GetDirectoryName(doc.FilePath) ?? "", targetFile);
            var finalDoc = doc.Project.AddDocument(fullPath, content);
            if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(solutionAfter, finalDoc.Project.Solution));
            return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(solutionAfter, finalDoc.Project.Solution));
        }
        return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, solutionAfter));
    }

    [McpServerTool, Description("Move a type to a different namespace.")]
    public static async Task<string> MoveTypeToNamespace([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Target namespace")] string targetNamespace, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var nsNode = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (nsNode == null) return JsonHelper.Serialize(new { error = "No namespace declaration found" });
        var newName = SyntaxFactory.ParseName(targetNamespace);
        var newNsNode = nsNode is NamespaceDeclarationSyntax bns ? (SyntaxNode)bns.WithName(newName) : ((FileScopedNamespaceDeclarationSyntax)nsNode).WithName(newName);
        var newRoot = root.ReplaceNode(nsNode, newNsNode);
        var newSolution = doc.Project.Solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Extract method refactoring — not yet implemented.")]
    public static Task<string> ExtractMethod() => Task.FromResult(JsonHelper.Serialize(new { error = "Not yet implemented. Use Visual Studio or Rider." }));

    [McpServerTool, Description("Extract variable refactoring — not yet implemented.")]
    public static Task<string> ExtractVariable() => Task.FromResult(JsonHelper.Serialize(new { error = "Not yet implemented. Use Visual Studio or Rider." }));

    [McpServerTool, Description("Extract constant refactoring — not yet implemented.")]
    public static Task<string> ExtractConstant() => Task.FromResult(JsonHelper.Serialize(new { error = "Not yet implemented. Use Visual Studio or Rider." }));

    [McpServerTool, Description("Extract base class refactoring — not yet implemented.")]
    public static Task<string> ExtractBaseClass() => Task.FromResult(JsonHelper.Serialize(new { error = "Not yet implemented. Use Visual Studio or Rider." }));

    [McpServerTool, Description("Inline variable refactoring — not yet implemented.")]
    public static Task<string> InlineVariable() => Task.FromResult(JsonHelper.Serialize(new { error = "Not yet implemented. Use Visual Studio or Rider." }));

    [McpServerTool, Description("Change signature refactoring — not yet implemented.")]
    public static Task<string> ChangeSignature() => Task.FromResult(JsonHelper.Serialize(new { error = "Not yet implemented. This is the most complex refactoring." }));
}

[McpServerToolType]
public static class GenerationTools
{
    static TypeDeclarationSyntax? FindType(SyntaxNode root, string name) => root.DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(t => t.Identifier.Text == name);
    static string ToCamel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLower(s[0]) + s.Substring(1);

    [McpServerTool, Description("Generate a constructor that initializes specified fields/properties.")]
    public static async Task<string> GenerateConstructor([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Type name")] string typeName, [Description("Member names")] string[] members, [Description("Add null checks")] bool addNullChecks = false, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var typeDecl = FindType(root, typeName); if (typeDecl == null) return JsonHelper.Serialize(new { error = $"Type not found: {typeName}" });

        var memberInfos = members.Select(mn =>
        {
            var sym = typeDecl.Members.SelectMany(m => m.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Identifier.Text == mn)).Select(v => sm.GetDeclaredSymbol(v)).OfType<IFieldSymbol>().FirstOrDefault();
            if (sym == null)
            {
                var prop = typeDecl.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == mn);
                if (prop != null) return (name: mn, typeStr: prop.Type.ToString(), isRefType: false);
                return (name: (string?)null, typeStr: (string?)null, isRefType: false);
            }
            return (name: mn, typeStr: sym.Type.ToDisplayString(), isRefType: sym.Type.IsReferenceType);
        }).Where(x => x.name != null).DistinctBy(x => x.name).ToList();
        if (memberInfos.Count == 0) return JsonHelper.Serialize(new { error = "No matching members found" });

        var parms = SyntaxFactory.SeparatedList(memberInfos.Select(m => SyntaxFactory.Parameter(SyntaxFactory.Identifier(ToCamel(m.name))).WithType(SyntaxFactory.ParseTypeName(m.typeStr))));
        var stmts = memberInfos.Select(m =>
        {
            var pn = ToCamel(m.name);
            var left = SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName(m.name));
            ExpressionSyntax right;
            if (addNullChecks && m.isRefType)
                right = SyntaxFactory.BinaryExpression(SyntaxKind.CoalesceExpression, SyntaxFactory.IdentifierName(pn),
                    SyntaxFactory.ThrowExpression(SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.ArgumentNullException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("nameof"))
                                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(pn)))))))))));
            else right = SyntaxFactory.IdentifierName(pn);
            return SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, left, right));
        }).ToArray();

        var ctor = SyntaxFactory.ConstructorDeclaration(SyntaxFactory.Identifier(typeName)).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .WithParameterList(SyntaxFactory.ParameterList(parms)).WithBody(SyntaxFactory.Block(stmts));
        var newRoot = root.ReplaceNode(typeDecl, typeDecl.AddMembers(ctor));
        var newSolution = doc.WithSyntaxRoot(newRoot).Project.Solution;
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Generate override methods for base class virtual or abstract members.")]
    public static async Task<string> GenerateOverrides([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Type name")] string typeName, [Description("Member names")] string[] members, [Description("Include base.Method() calls")] bool callBase = false, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var typeDecl = FindType(root, typeName); if (typeDecl == null) return JsonHelper.Serialize(new { error = $"Type not found: {typeName}" });
        if (sm.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol classSymbol) return JsonHelper.Serialize(new { error = "Cannot resolve type symbol" });

        var memberSet = new HashSet<string>(members);
        var newMembers = new List<MemberDeclarationSyntax>();
        if (classSymbol.BaseType != null)
        {
            foreach (var bm in classSymbol.BaseType.GetMembers())
            {
                if (!bm.IsVirtual && !bm.IsAbstract || !memberSet.Contains(bm.Name)) continue;
                if (bm is IMethodSymbol meth && meth.MethodKind == MethodKind.Ordinary)
                {
                    var parms = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(meth.Parameters.Select(p => SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name)).WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString())))));
                    var bodyStmts = new List<StatementSyntax>();
                    if (callBase) bodyStmts.Add(SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.BaseExpression(), SyntaxFactory.IdentifierName(meth.Name))).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(meth.Parameters.Select(p => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(p.Name))))))));
                    if (!meth.ReturnsVoid) bodyStmts.Add(SyntaxFactory.ReturnStatement(SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(meth.ReturnType.ToDisplayString()))));
                    newMembers.Add(SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(meth.ReturnType.ToDisplayString()), meth.Name).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)).WithParameterList(parms).WithBody(SyntaxFactory.Block(bodyStmts)));
                }
                else if (bm is IPropertySymbol prop)
                    newMembers.Add(SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(prop.Type.ToDisplayString()), prop.Name).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) }))));
            }
        }
        if (newMembers.Count == 0) return JsonHelper.Serialize(new { error = "No matching overridable members found" });
        var updated = newMembers.Aggregate(typeDecl, (t, m) => t.AddMembers(m));
        var newSolution = doc.WithSyntaxRoot(root.ReplaceNode(typeDecl, updated)).Project.Solution;
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Generate interface member implementations.")]
    public static async Task<string> ImplementInterface([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Type name")] string typeName, [Description("Interface name")] string interfaceName, [Description("Specific members")] string[]? members = null, [Description("Explicit implementation")] bool explicitImplementation = false, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var typeDecl = FindType(root, typeName); if (typeDecl == null) return JsonHelper.Serialize(new { error = $"Type not found: {typeName}" });
        if (sm.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol classSymbol) return JsonHelper.Serialize(new { error = "Cannot resolve type symbol" });
        var iface = classSymbol.AllInterfaces.FirstOrDefault(i => i.Name == interfaceName || i.ToDisplayString() == interfaceName);
        if (iface == null) return JsonHelper.Serialize(new { error = $"Interface not found: {interfaceName}" });

        var filter = members != null ? new HashSet<string>(members) : null;
        var newMembers = new List<MemberDeclarationSyntax>();
        foreach (var im in iface.GetMembers())
        {
            if (filter != null && !filter.Contains(im.Name)) continue;
            switch (im)
            {
                case IMethodSymbol m when m.MethodKind == MethodKind.Ordinary:
                    var mn = explicitImplementation ? $"{interfaceName}.{m.Name}" : m.Name;
                    var stmts = m.ReturnsVoid ? new StatementSyntax[] { SyntaxFactory.EmptyStatement() } : new StatementSyntax[] { SyntaxFactory.ReturnStatement(SyntaxFactory.DefaultExpression(SyntaxFactory.ParseTypeName(m.ReturnType.ToDisplayString()))) };
                    newMembers.Add(SyntaxFactory.MethodDeclaration(SyntaxFactory.ParseTypeName(m.ReturnType.ToDisplayString()), mn).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).WithParameterList(SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(m.Parameters.Select(p => SyntaxFactory.Parameter(SyntaxFactory.Identifier(p.Name)).WithType(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString())))))).WithBody(SyntaxFactory.Block(stmts)));
                    break;
                case IPropertySymbol p:
                    var pn = explicitImplementation ? $"{interfaceName}.{p.Name}" : p.Name;
                    newMembers.Add(SyntaxFactory.PropertyDeclaration(SyntaxFactory.ParseTypeName(p.Type.ToDisplayString()), pn).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) }))));
                    break;
                case IEventSymbol e:
                    var en = explicitImplementation ? $"{interfaceName}.{e.Name}" : e.Name;
                    newMembers.Add(SyntaxFactory.EventFieldDeclaration(SyntaxFactory.VariableDeclaration(SyntaxFactory.ParseTypeName(e.Type.ToDisplayString())).AddVariables(SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(en)))).AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword)));
                    break;
            }
        }
        if (newMembers.Count == 0) return JsonHelper.Serialize(new { error = "No interface members to implement" });
        var updated = newMembers.Aggregate(typeDecl, (t, m) => t.AddMembers(m));
        var newSolution = doc.WithSyntaxRoot(root.ReplaceNode(typeDecl, updated)).Project.Solution;
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Generate Equals() and GetHashCode() overrides.")]
    public static async Task<string> GenerateEqualsHashCode([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Type name")] string typeName, [Description("Fields to include")] string[] fields, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var typeDecl = FindType(root, typeName); if (typeDecl == null) return JsonHelper.Serialize(new { error = $"Type not found: {typeName}" });

        var eqCond = fields.Select(f => (ExpressionSyntax)SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.GenericName(SyntaxFactory.Identifier("EqualityComparer")).WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.OmittedTypeArgument()))),
                    SyntaxFactory.IdentifierName("Default")), SyntaxFactory.IdentifierName("Equals")))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] {
                    SyntaxFactory.Argument(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.ThisExpression(), SyntaxFactory.IdentifierName(f))),
                    SyntaxFactory.Argument(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("other"), SyntaxFactory.IdentifierName(f)))
                })))).Aggregate((a, e) => SyntaxFactory.BinaryExpression(SyntaxKind.LogicalAndExpression, a, e));

        var eqBody = SyntaxFactory.Block(SyntaxFactory.IfStatement(
            SyntaxFactory.BinaryExpression(SyntaxKind.IsExpression, SyntaxFactory.IdentifierName("obj"), SyntaxFactory.IdentifierName(typeName)),
            SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(eqCond))),
            SyntaxFactory.ElseClause(SyntaxFactory.ReturnStatement(SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)))));

        var eqMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.BoolKeyword)), "Equals")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .AddParameterListParameters(SyntaxFactory.Parameter(SyntaxFactory.Identifier("obj")).WithType(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword))))
            .WithBody(eqBody);

        var hcArgs = SyntaxFactory.SeparatedList(fields.Select(f => SyntaxFactory.Argument(SyntaxFactory.IdentifierName(f))));
        var hcBody = SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(
            SyntaxFactory.ReturnStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("HashCode"), SyntaxFactory.IdentifierName("Combine"))).WithArgumentList(SyntaxFactory.ArgumentList(hcArgs)))));
        var hcMethod = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)), "GetHashCode")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword)).WithBody(hcBody);

        var updated = typeDecl.AddMembers(eqMethod).AddMembers(hcMethod);
        var newSolution = doc.WithSyntaxRoot(root.ReplaceNode(typeDecl, updated)).Project.Solution;
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Generate a ToString() override that includes specified fields.")]
    public static async Task<string> GenerateToString([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Type name")] string typeName, [Description("Fields to include")] string[] fields, [Description("Preview only")] bool preview = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var typeDecl = FindType(root, typeName); if (typeDecl == null) return JsonHelper.Serialize(new { error = $"Type not found: {typeName}" });

        var parts = new List<ArgumentSyntax>();
        foreach (var f in fields) { parts.Add(SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal($"{f}: ")))); parts.Add(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(f))); }
        var concat = SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)), SyntaxFactory.IdentifierName("Concat"))).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(parts)));
        var toString = SyntaxFactory.MethodDeclaration(SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.StringKeyword)), "ToString")
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword), SyntaxFactory.Token(SyntaxKind.OverrideKeyword))
            .WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(concat))));
        var newSolution = doc.WithSyntaxRoot(root.ReplaceNode(typeDecl, typeDecl.AddMembers(toString))).Project.Solution;
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }

    [McpServerTool, Description("Format a C# file using Roslyn's code formatter.")]
    public static async Task<string> FormatDocument([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var formatted = await Formatter.FormatAsync(doc);
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, formatted.Project.Solution));
    }

    [McpServerTool, Description("Add null-check statements for method parameters.")]
    public static async Task<string> AddNullChecks([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Method name")] string methodName, [Description("Style: throwIfNull or guard")] string? style = null, [Description("Line number (optional)")] int? line = null, [Description("Preview only")] bool preview = false)
    {
        style ??= "throwIfNull";
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });

        var method = line.HasValue ? root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => { var s = m.GetLocation().GetLineSpan(); return m.Identifier.Text == methodName && s.StartLinePosition.Line <= line.Value && s.EndLinePosition.Line >= line.Value; }) : root.DescendantNodes().OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.Text == methodName);
        if (method == null) return JsonHelper.Serialize(new { error = $"Method not found: {methodName}" });

        var refParams = method.ParameterList.Parameters.Where(p =>
        {
            var ps = sm.GetDeclaredSymbol(p) as IParameterSymbol;
            return ps?.Type.IsReferenceType == true;
        }).ToList();
        if (refParams.Count == 0) return JsonHelper.Serialize(new { error = "No reference-type parameters found" });

        var checks = refParams.Select<ParameterSyntax, StatementSyntax>(p =>
        {
            var n = p.Identifier.Text;
            if (style == "guard")
                return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("ArgumentNullException"), SyntaxFactory.IdentifierName("ThrowIfNull")))
                    .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(SyntaxFactory.IdentifierName(n))))));
            var nullCheck = SyntaxFactory.BinaryExpression(SyntaxKind.IsExpression, SyntaxFactory.IdentifierName(n), SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
            var argNullEx = SyntaxFactory.ObjectCreationExpression(SyntaxFactory.ParseTypeName("System.ArgumentNullException"))
                .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.InvocationExpression(SyntaxFactory.IdentifierName("nameof"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(n)))))))));
            return SyntaxFactory.IfStatement(nullCheck, SyntaxFactory.ThrowStatement(argNullEx));
        }).ToArray();

        var existing = method.Body?.Statements.ToList() ?? new List<StatementSyntax>();
        var newBody = SyntaxFactory.Block(checks.Concat(existing).ToArray());
        var newSolution = doc.WithSyntaxRoot(root.ReplaceNode(method, method.WithBody(newBody))).Project.Solution;
        if (preview) return JsonHelper.Serialize(await PreviewService.PreviewChangesAsync(doc.Project.Solution, newSolution));
        return JsonHelper.Serialize(await PreviewService.ApplyToDiskAsync(doc.Project.Solution, newSolution));
    }
}

public class ExpressionBodyRewriter(string direction) : CSharpSyntaxRewriter
{
    MethodDeclarationSyntax ToExpr(MethodDeclarationSyntax n)
    {
        if (n.ExpressionBody != null || n.Body == null) return n;
        var expr = n.Body.Statements.OfType<ReturnStatementSyntax>().LastOrDefault()?.Expression ?? n.Body.Statements.OfType<ExpressionStatementSyntax>().LastOrDefault()?.Expression;
        return expr == null ? n : n.WithBody(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr.WithoutTrivia())).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }
    MethodDeclarationSyntax ToBlock(MethodDeclarationSyntax n)
    {
        if (n.ExpressionBody == null) return n;
        StatementSyntax stmt = n.ReturnType.ToString().Trim().StartsWith("void", StringComparison.OrdinalIgnoreCase) ? SyntaxFactory.ExpressionStatement(n.ExpressionBody.Expression) : SyntaxFactory.ReturnStatement(n.ExpressionBody.Expression);
        return n.WithExpressionBody(null).WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList(stmt))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }
    PropertyDeclarationSyntax ToExprProp(PropertyDeclarationSyntax n)
    {
        if (n.ExpressionBody != null || n.AccessorList == null) return n;
        var g = n.AccessorList.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        if (g == null) return n;
        var expr = g.ExpressionBody?.Expression ?? g.Body?.Statements.OfType<ReturnStatementSyntax>().LastOrDefault()?.Expression;
        return expr == null ? n : n.WithAccessorList(null).WithExpressionBody(SyntaxFactory.ArrowExpressionClause(expr)).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }
    PropertyDeclarationSyntax ToBlockProp(PropertyDeclarationSyntax n)
    {
        if (n.ExpressionBody == null) return n;
        var g = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(n.ExpressionBody.Expression))));
        return n.WithExpressionBody(null).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(g))).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
    }
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax n) => base.VisitMethodDeclaration(direction == "ToExpression" ? ToExpr(n) : ToBlock(n));
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax n) => base.VisitPropertyDeclaration(direction == "ToExpression" ? ToExprProp(n) : ToBlockProp(n));
}

public class PropertyConversionRewriter(string? propertyName, int? line, string direction) : CSharpSyntaxRewriter
{
    bool Match(PropertyDeclarationSyntax n)
    {
        if (line.HasValue) { var s = n.GetLocation().GetLineSpan(); return s.StartLinePosition.Line <= line.Value && s.EndLinePosition.Line >= line.Value; }
        return propertyName != null && n.Identifier.Text == propertyName;
    }
    PropertyDeclarationSyntax ToAuto(PropertyDeclarationSyntax n) => SyntaxFactory.PropertyDeclaration(n.Type, n.Identifier).WithModifiers(n.Modifiers).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) }))).WithTriviaFrom(n);
    PropertyDeclarationSyntax ToFull(PropertyDeclarationSyntax n)
    {
        if (n.AccessorList == null || !n.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null)) return n;
        var c = n.Identifier.Text.Length <= 1 ? n.Identifier.Text.ToLowerInvariant() : char.ToLowerInvariant(n.Identifier.Text[0]) + n.Identifier.Text.Substring(1);
        var bf = "_" + c;
        return n.WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] {
            SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ReturnStatement(SyntaxFactory.IdentifierName(bf))))),
            SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithBody(SyntaxFactory.Block(SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ExpressionStatement(SyntaxFactory.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SyntaxFactory.IdentifierName(bf), SyntaxFactory.IdentifierName("value")))))) })));
    }
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax n) => Match(n) ? base.VisitPropertyDeclaration(direction == "ToAuto" ? ToAuto(n) : ToFull(n)) : base.VisitPropertyDeclaration(n);
}

public class AsyncConversionRewriter(string? methodName, int? line, bool renameToAsync) : CSharpSyntaxRewriter
{
    int _depth;
    bool Match(MethodDeclarationSyntax n)
    {
        if (line.HasValue) { var s = n.GetLocation().GetLineSpan(); return s.StartLinePosition.Line <= line.Value && s.EndLinePosition.Line >= line.Value; }
        return methodName != null && n.Identifier.Text == methodName;
    }
    TypeSyntax Wrap(TypeSyntax t) => t.ToString().Trim() == "void" ? SyntaxFactory.IdentifierName("Task") : SyntaxFactory.GenericName(SyntaxFactory.Identifier("Task"), SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(t)));
    ExpressionSyntax WrapResult(ExpressionSyntax e) => SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName("Task"), SyntaxFactory.IdentifierName("FromResult")), SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(e))));
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax n)
    {
        if (!Match(n)) return base.VisitMethodDeclaration(n);
        _depth++;
        var hasAsync = n.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));
        return base.VisitMethodDeclaration(n.WithModifiers(hasAsync ? n.Modifiers : n.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithLeadingTrivia(SyntaxFactory.Space))).WithReturnType(Wrap(n.ReturnType)).WithIdentifier(renameToAsync ? SyntaxFactory.Identifier(n.Identifier.Text + "Async") : n.Identifier));
    }
    public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax n) => _depth > 0 && n.Expression != null ? base.VisitReturnStatement(SyntaxFactory.ReturnStatement(WrapResult(n.Expression)).WithTriviaFrom(n)) : base.VisitReturnStatement(n);
    public override SyntaxNode? VisitArrowExpressionClause(ArrowExpressionClauseSyntax n) => _depth > 0 ? base.VisitArrowExpressionClause(SyntaxFactory.ArrowExpressionClause(WrapResult(n.Expression)).WithTriviaFrom(n)) : base.VisitArrowExpressionClause(n);
}

public class InterpolatedStringRewriter(int? line) : CSharpSyntaxRewriter
{
    bool IsTarget(SyntaxNode n) { if (!line.HasValue) return true; var s = n.GetLocation().GetLineSpan(); return s.StartLinePosition.Line <= line.Value && s.EndLinePosition.Line >= line.Value; }
    ExpressionSyntax Build(string format, ExpressionSyntax[] args)
    {
        var cont = new List<InterpolatedStringContentSyntax>(); var sb = new StringBuilder(); int i = 0;
        while (i < format.Length)
        {
            if (format[i] == '{' && i + 1 < format.Length && format[i + 1] == '{') { sb.Append('{'); i += 2; }
            else if (format[i] == '}' && i + 1 < format.Length && format[i + 1] == '}') { sb.Append('}'); i += 2; }
            else if (format[i] == '{')
            {
                if (sb.Length > 0) { cont.Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken, sb.ToString(), sb.ToString(), SyntaxTriviaList.Empty))); sb.Clear(); }
                var j = format.IndexOf('}', i + 1);
                if (j > i) { var idx = format.Substring(i + 1, j - i - 1); if (int.TryParse(idx, out int k) && k >= 0 && k < args.Length) cont.Add(SyntaxFactory.Interpolation(args[k])); else sb.Append(format.Substring(i, j - i + 1)); i = j + 1; }
                else { sb.Append('{'); i++; }
            }
            else { sb.Append(format[i]); i++; }
        }
        if (sb.Length > 0) cont.Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken, sb.ToString(), sb.ToString(), SyntaxTriviaList.Empty)));
        return SyntaxFactory.InterpolatedStringExpression(SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken), SyntaxFactory.List(cont));
    }
    ExpressionSyntax? ConvertFormat(InvocationExpressionSyntax n)
    {
        var a = n.ArgumentList.Arguments; if (a.Count == 0) return null;
        if (a[0].Expression is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) return Build(lit.Token.ValueText, a.Skip(1).Select(x => x.Expression).ToArray());
        var all = a.Select(x => x.Expression).ToArray(); return all.Length <= 1 ? all.FirstOrDefault() : Build("", all);
    }
    ExpressionSyntax? ConvertConcat(BinaryExpressionSyntax n)
    {
        var parts = new List<ExpressionSyntax>();
        void Collect(ExpressionSyntax e) { if (e is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression)) { Collect(b.Left); Collect(b.Right); } else parts.Add(e); }
        Collect(n);
        if (parts.Count <= 1) return null;
        var cont = new List<InterpolatedStringContentSyntax>();
        foreach (var p in parts) if (p is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)) { var t = lit.Token.ValueText; if (t.Length > 0) cont.Add(SyntaxFactory.InterpolatedStringText(SyntaxFactory.Token(SyntaxTriviaList.Empty, SyntaxKind.InterpolatedStringTextToken, t, t, SyntaxTriviaList.Empty))); } else cont.Add(SyntaxFactory.Interpolation(p));
        return SyntaxFactory.InterpolatedStringExpression(SyntaxFactory.Token(SyntaxKind.InterpolatedStringStartToken), SyntaxFactory.List(cont));
    }
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax n) { if (IsTarget(n) && n.Expression is MemberAccessExpressionSyntax m && m.Name.Identifier.Text == "Format") { var c = ConvertFormat(n); if (c != null) return c; } return base.VisitInvocationExpression(n); }
    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax n) { if (IsTarget(n) && n.IsKind(SyntaxKind.AddExpression)) { var c = ConvertConcat(n); if (c != null) return c; } return base.VisitBinaryExpression(n); }
}

public class ForeachToLinqRewriter(int targetLine) : CSharpSyntaxRewriter
{
    static bool AtLine(SyntaxNode n, int l) { var s = n.GetLocation().GetLineSpan(); return s.StartLinePosition.Line <= l && s.EndLinePosition.Line >= l; }
    StatementSyntax? Convert(ForEachStatementSyntax n)
    {
        var stmts = n.Statement is BlockSyntax b ? b.Statements.ToList() : new List<StatementSyntax> { n.Statement };
        if (stmts.Count != 1 || stmts[0] is not ExpressionStatementSyntax es || es.Expression is not InvocationExpressionSyntax inv || inv.Expression is not MemberAccessExpressionSyntax maes || maes.Expression is not IdentifierNameSyntax target || target.Identifier.Text != n.Identifier.Text) return null;
        var lambda = SyntaxFactory.SimpleLambdaExpression(SyntaxFactory.Parameter(SyntaxFactory.Identifier(n.Identifier.Text)), inv.ArgumentList);
        return SyntaxFactory.ExpressionStatement(SyntaxFactory.InvocationExpression(SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, n.Expression, SyntaxFactory.IdentifierName(maes.Name.Identifier.Text))).WithArgumentList(SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(lambda)))));
    }
    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax n) { if (AtLine(n, targetLine)) { var c = Convert(n); if (c != null) return c; } return base.VisitForEachStatement(n); }
}

[McpServerToolType]
public static class ConversionTools
{
    [McpServerTool, Description("Convert a synchronous method to async.")]
    public static async Task<string> ConvertToAsync([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Method name")] string? methodName = null, [Description("Line number")] int? line = null, [Description("Append 'Async' suffix")] bool renameToAsync = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var n = new AsyncConversionRewriter(methodName, line, renameToAsync).Visit(root);
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(n).Project.Solution) });
    }

    [McpServerTool, Description("Toggle between expression body (=>) and block body ({}).")]
    public static async Task<string> ConvertExpressionBody([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("ToExpression or ToBlock")] string direction = "ToExpression", [Description("Member name")] string? memberName = null, [Description("Line number")] int? line = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var n = new ExpressionBodyRewriter(direction).Visit(root);
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(n).Project.Solution) });
    }

    [McpServerTool, Description("Toggle between auto-property and full property with backing field.")]
    public static async Task<string> ConvertProperty([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("ToAuto or ToFull")] string direction = "ToAuto", [Description("Property name")] string? propertyName = null, [Description("Line number")] int? line = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var n = new PropertyConversionRewriter(propertyName, line, direction).Visit(root);
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(n).Project.Solution) });
    }

    [McpServerTool, Description("Convert a foreach loop to a LINQ method chain.")]
    public static async Task<string> ConvertForeachLinq([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Line number of the foreach")] int line)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var n = new ForeachToLinqRewriter(line).Visit(root);
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(n).Project.Solution) });
    }

    [McpServerTool, Description("Convert if/else chains with 'is' type checks into C# switch expressions.")]
    public static async Task<string> ConvertToPatternMatching([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Line number of the first if")] int? line = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });

        var ifs = root.DescendantNodes().OfType<IfStatementSyntax>().Where(i => i.Condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.IsExpression } or IsPatternExpressionSyntax).ToList();
        var topIf = line.HasValue ? ifs.FirstOrDefault(i => { var s = i.GetLocation().GetLineSpan(); return s.StartLinePosition.Line <= line.Value && s.EndLinePosition.Line >= line.Value; }) : ifs.FirstOrDefault();
        if (topIf == null) return JsonHelper.Serialize(new { error = "No suitable if/is chains found." });

        var arms = new List<SwitchExpressionArmSyntax>();
        IfStatementSyntax? current = topIf;
        while (current != null)
        {
            ExpressionSyntax? subject = null;
            if (current.Condition is BinaryExpressionSyntax bes && bes.IsKind(SyntaxKind.IsExpression))
            {
                subject = bes.Left;
                if (bes.Right is TypeSyntax type)
                {
                    var pat = SyntaxFactory.DeclarationPattern(type, SyntaxFactory.SingleVariableDesignation(SyntaxFactory.Identifier("x")));
                    var stmts = current.Statement is BlockSyntax b ? b.Statements : new SyntaxList<StatementSyntax>(current.Statement);
                    var ret = stmts.OfType<ReturnStatementSyntax>().FirstOrDefault();
                    if (ret?.Expression != null) arms.Add(SyntaxFactory.SwitchExpressionArm(pat, ret.Expression));
                }
            }
            else if (current.Condition is IsPatternExpressionSyntax ip)
            {
                subject = ip.Expression;
                var stmts = current.Statement is BlockSyntax b ? b.Statements : new SyntaxList<StatementSyntax>(current.Statement);
                var ret = stmts.OfType<ReturnStatementSyntax>().FirstOrDefault();
                if (ret?.Expression != null) arms.Add(SyntaxFactory.SwitchExpressionArm(ip.Pattern, ret.Expression));
            }
            current = current.Else?.Statement is IfStatementSyntax elif ? elif : null;
            if (current == null && topIf.Else?.Statement is BlockSyntax eb)
            {
                var lastRet = eb.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault();
                if (lastRet?.Expression != null) arms.Add(SyntaxFactory.SwitchExpressionArm(SyntaxFactory.DiscardPattern(), lastRet.Expression));
            }
        }
        if (arms.Count == 0) return JsonHelper.Serialize(new { error = "Could not extract pattern arms." });
        var sw = SyntaxFactory.SwitchExpression(topIf.Condition is IsPatternExpressionSyntax ip2 ? ip2.Expression : topIf.Condition is BinaryExpressionSyntax bes2 ? bes2.Left : SyntaxFactory.IdentifierName("value"), SyntaxFactory.SeparatedList(arms));
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(root.ReplaceNode(topIf, sw)).Project.Solution) });
    }

    [McpServerTool, Description("Convert string.Format() or concatenation to interpolated string.")]
    public static async Task<string> ConvertToInterpolatedString([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Line number")] int? line = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var n = new InterpolatedStringRewriter(line).Visit(root);
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(n).Project.Solution) });
    }

    [McpServerTool, Description("Promote a local variable to a method parameter.")]
    public static async Task<string> IntroduceParameter([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Path to source file")] string sourceFile, [Description("Variable name")] string? variableName = null, [Description("Line number")] int? line = null)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });

        var localInfo = root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>().SelectMany(lds => lds.Declaration.Variables.Select(v => (lds, v))).FirstOrDefault(x => variableName != null ? x.v.Identifier.Text == variableName : line.HasValue && (x.lds.GetLocation().GetLineSpan().StartLinePosition.Line <= line.Value && x.lds.GetLocation().GetLineSpan().EndLinePosition.Line >= line.Value));
        if (localInfo.lds == null) return JsonHelper.Serialize(new { error = "Local variable not found." });

        var container = localInfo.lds.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() ?? localInfo.lds.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() as SyntaxNode;
        if (container == null) return JsonHelper.Serialize(new { error = "Could not find containing method." });

        var paramName = localInfo.v.Identifier.Text;
        var paramType = localInfo.lds.Declaration.Type;
        var init = localInfo.v.Initializer;
        var param = SyntaxFactory.Parameter(SyntaxFactory.Identifier(paramName)).WithType(paramType)
            .WithDefault(init ?? SyntaxFactory.EqualsValueClause(SyntaxFactory.DefaultExpression(paramType))).WithTrailingTrivia(SyntaxFactory.Space);

        var newContainer = container switch
        {
            MethodDeclarationSyntax m => m.WithParameterList(m.ParameterList.AddParameters(param)),
            LocalFunctionStatementSyntax l => l.WithParameterList(l.ParameterList.AddParameters(param)),
            _ => container
        };
        var r1 = root.ReplaceNode(container, newContainer);
        var toRemove = r1.FindNode(localInfo.lds.Span);
        var r2 = r1.RemoveNode(toRemove, SyntaxRemoveOptions.KeepNoTrivia) ?? r1;
        return JsonHelper.Serialize(new { Success = true, Changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(r2).Project.Solution) });
    }
}

[McpServerToolType]
public static class UsingTools
{
    [McpServerTool, Description("Detect and add missing using directives.")]
    public static async Task<string> AddMissingUsings([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file")] string? sourceFile = null, [Description("Process all files")] bool allFiles = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath);
        var docs = allFiles ? WorkspaceService.GetAllDocuments(loaded.Solution).ToList() : sourceFile != null ? new[] { WorkspaceService.GetDocument(loaded.Solution, sourceFile) }.Where(d => d != null).Cast<Document>().ToList() : new List<Document>();
        if (docs.Count == 0) return JsonHelper.Serialize(new { Success = true, Message = "No files to process", Changes = new List<ChangePreview>() });

        var solution = loaded.Solution;
        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(); if (root is not CompilationUnitSyntax cu) continue;
            var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) continue;

            var existing = cu.Usings.Select(u => u.Name.ToString()).ToHashSet();
            var missing = new HashSet<string>();
            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var info = sm.GetSymbolInfo(id);
                if (info.Symbol == null) continue;
                var ns = info.Symbol.ContainingNamespace;
                if (ns != null && !ns.IsGlobalNamespace && !ns.ToDisplayString().StartsWith("System"))
                {
                    var nsName = ns.ToDisplayString();
                    if (!existing.Contains(nsName)) missing.Add(nsName);
                }
            }
            if (missing.Count == 0) continue;
            var newDirectives = missing.OrderBy(x => x).Select(ns => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns)).WithTrailingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine))).ToList();
            var allUsings = cu.Usings.Concat(newDirectives).OrderBy(u => u.Name.ToString());
            solution = solution.WithDocumentSyntaxRoot(doc.Id, cu.WithUsings(SyntaxFactory.List(allUsings)));
        }

        var changes = await PreviewService.ComputeChangesAsync(loaded.Solution, solution);
        return JsonHelper.Serialize(new { Success = true, Message = $"Processed {docs.Count} file(s)", Changes = changes });
    }

    [McpServerTool, Description("Remove using directives that are not referenced.")]
    public static async Task<string> RemoveUnusedUsings([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file")] string? sourceFile = null, [Description("Process all files")] bool allFiles = false)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath);
        var docs = allFiles ? WorkspaceService.GetAllDocuments(loaded.Solution).ToList() : sourceFile != null ? new[] { WorkspaceService.GetDocument(loaded.Solution, sourceFile) }.Where(d => d != null).Cast<Document>().ToList() : new List<Document>();
        if (docs.Count == 0) return JsonHelper.Serialize(new { Success = true, Message = "No files to process", Changes = new List<ChangePreview>() });

        var solution = loaded.Solution;
        foreach (var doc in docs)
        {
            var root = await doc.GetSyntaxRootAsync(); if (root is not CompilationUnitSyntax cu) continue;
            var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) continue;

            var used = new HashSet<string>();
            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                var info = sm.GetSymbolInfo(id);
                if (info.Symbol?.ContainingNamespace != null) used.Add(info.Symbol.ContainingNamespace.ToDisplayString());
            }

            var unused = cu.Usings.Where(u => !used.Contains(u.Name.ToString()) && u.Alias == null).ToList();
            if (unused.Count == 0) continue;
            var newRoot = unused.Aggregate((SyntaxNode)cu, (r, u) => r.RemoveNode(u, SyntaxRemoveOptions.KeepNoTrivia) ?? r);
            solution = solution.WithDocumentSyntaxRoot(doc.Id, newRoot);
        }

        var changes = await PreviewService.ComputeChangesAsync(loaded.Solution, solution);
        return JsonHelper.Serialize(new { Success = true, Message = $"Processed {docs.Count} file(s)", Changes = changes });
    }

    [McpServerTool, Description("Sort using directives alphabetically.")]
    public static async Task<string> SortUsings([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file")] string sourceFile)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var root = await doc.GetSyntaxRootAsync(); if (root is not CompilationUnitSyntax cu) return JsonHelper.Serialize(new { error = "Could not get syntax root" });
        var sorted = SyntaxFactory.List(cu.Usings.OrderBy(u => u.Name.ToString().StartsWith("System") ? 0 : u.Name.ToString().StartsWith("Microsoft") ? 1 : 2).ThenBy(u => u.Name.ToString()));
        if (cu.Usings.Select(u => u.Name.ToString()).SequenceEqual(sorted.Select(u => u.Name.ToString()))) return JsonHelper.Serialize(new { Success = true, Message = "Already sorted", Changes = new List<ChangePreview>() });
        var changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(cu.WithUsings(sorted)).Project.Solution);
        return JsonHelper.Serialize(new { Success = true, Message = "Sorted using directives", Changes = changes });
    }
}

[McpServerToolType]
public static class CodeActionTools
{
    static readonly Dictionary<string, string[]> KnownFixes = new()
    {
        ["CS0169"] = new[] { "Remove unused field", "Suppress with pragma" },
        ["CS0219"] = new[] { "Remove unused variable" },
        ["CS0103"] = new[] { "Add missing using directive", "Use fully qualified name" },
        ["CS0246"] = new[] { "Add missing using directive", "Use fully qualified name" },
        ["CS0618"] = new[] { "Use suggested alternative member" },
        ["CS1591"] = new[] { "Add XML documentation comment" },
        ["IDE0005"] = new[] { "Remove unnecessary using" },
        ["IDE0007"] = new[] { "Use 'var' instead of explicit type" },
        ["IDE0008"] = new[] { "Use explicit type instead of 'var'" },
        ["IDE0016"] = new[] { "Use throw expression" },
        ["IDE0017"] = new[] { "Use object initializer" },
        ["IDE0021"] = new[] { "Use expression body for method" },
        ["IDE0022"] = new[] { "Use expression body for method" },
        ["IDE0025"] = new[] { "Use expression body for property" },
        ["IDE0028"] = new[] { "Use collection initializer" },
        ["IDE0029"] = new[] { "Use coalesce expression" },
        ["IDE0031"] = new[] { "Use null propagation" },
        ["IDE0032"] = new[] { "Use auto property" },
        ["IDE0034"] = new[] { "Simplify default expression" },
        ["IDE0040"] = new[] { "Add accessibility modifier" },
        ["IDE0041"] = new[] { "Use 'is null' check" },
        ["IDE0044"] = new[] { "Add readonly modifier" },
        ["IDE0045"] = new[] { "Use conditional expression" },
        ["IDE0047"] = new[] { "Remove unnecessary parentheses" },
        ["IDE0055"] = new[] { "Fix indentation" },
        ["IDE0057"] = new[] { "Use range operator" },
        ["IDE0058"] = new[] { "Remove unnecessary expression value" },
        ["IDE0059"] = new[] { "Remove unused assignment" },
        ["IDE0060"] = new[] { "Remove unused parameter" },
        ["IDE0061"] = new[] { "Use expression body for local function" },
        ["IDE0063"] = new[] { "Use simple using statement" },
        ["IDE0064"] = new[] { "Make struct readonly" },
        ["IDE0065"] = new[] { "Move using directive inside namespace" },
        ["IDE0066"] = new[] { "Convert to switch expression" },
        ["IDE0070"] = new[] { "Use System.HashCode" },
        ["IDE0074"] = new[] { "Use coalesce compound assignment" },
        ["IDE0090"] = new[] { "Simplify new expression" },
        ["IDE0160"] = new[] { "Convert to block-scoped namespace" },
        ["IDE0161"] = new[] { "Convert to file-scoped namespace" },
    };

    static TextSpan? GetSpan(SourceText text, int line, int column)
    {
        if (line < 0 || line >= text.Lines.Count) return null;
        var ls = text.Lines[line].Span;
        return new TextSpan(Math.Min(ls.Start + column, ls.End), 1);
    }

    static async Task<Document?> ApplySimpleFix(Document doc, Diagnostic diag, string title)
    {
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return null;
        var sm = await WorkspaceService.GetSemanticModelAsync(doc);

        switch (title)
        {
            case "Remove unnecessary using" or "Remove unused using":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var us = node.AncestorsAndSelf().OfType<UsingDirectiveSyntax>().FirstOrDefault();
                if (us == null) return null;
                var nr = root.RemoveNode(us, SyntaxRemoveOptions.KeepNoTrivia);
                return nr != null ? doc.WithSyntaxRoot(nr) : null;
            }
            case "Use 'var' instead of explicit type":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var lds = node.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault();
                if (lds == null || lds.Declaration.Type.IsVar) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(lds, lds.WithDeclaration(lds.Declaration.WithType(SyntaxFactory.IdentifierName("var")))));
            }
            case "Use explicit type instead of 'var'":
            {
                if (sm == null) return null;
                var node = root.FindNode(diag.Location.SourceSpan);
                var lds = node.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault(l => l.Declaration.Type.IsVar);
                if (lds == null) return null;
                var vs = sm.GetDeclaredSymbol(lds.Declaration.Variables[0]);
                if (vs is not ILocalSymbol ls) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(lds, lds.WithDeclaration(lds.Declaration.WithType(SyntaxFactory.ParseTypeName(ls.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))))));
            }
            case "Remove unused variable" or "Remove unused field":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var decl = node.AncestorsAndSelf().OfType<LocalDeclarationStatementSyntax>().FirstOrDefault() ?? node.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault() as SyntaxNode;
                if (decl == null) return null;
                var nr = root.RemoveNode(decl, SyntaxRemoveOptions.KeepNoTrivia);
                return nr != null ? doc.WithSyntaxRoot(nr) : null;
            }
            case "Simplify name" or "Simplify member access":
            {
                if (sm == null) return null;
                var node = root.FindNode(diag.Location.SourceSpan);
                var info = sm.GetSymbolInfo(node);
                if (info.Symbol == null) return null;
                var simplified = info.Symbol.ToMinimalDisplayString(sm, node.SpanStart);
                return doc.WithSyntaxRoot(root.ReplaceNode(node, SyntaxFactory.ParseName(simplified).WithTriviaFrom(node)));
            }
            case "Remove 'this' qualification":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var ma = node.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault(m => m.Expression is ThisExpressionSyntax);
                if (ma == null) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(ma, ma.Name.WithTriviaFrom(ma)));
            }
            case "Remove unnecessary cast":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var cast = node.AncestorsAndSelf().OfType<CastExpressionSyntax>().FirstOrDefault();
                if (cast == null) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(cast, cast.Expression.WithTriviaFrom(cast)));
            }
            case "Add accessibility modifier":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var m = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().FirstOrDefault(m => !m.Modifiers.Any(t => t.IsKind(SyntaxKind.PublicKeyword) || t.IsKind(SyntaxKind.PrivateKeyword) || t.IsKind(SyntaxKind.ProtectedKeyword) || t.IsKind(SyntaxKind.InternalKeyword)));
                if (m == null) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(m, m.WithModifiers(m.Modifiers.Insert(0, SyntaxFactory.Token(SyntaxKind.PrivateKeyword).WithLeadingTrivia(SyntaxFactory.Space)))));
            }
            case "Add readonly modifier":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var f = node.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault(f => !f.Modifiers.Any(t => t.IsKind(SyntaxKind.ReadOnlyKeyword)));
                if (f == null) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(f, f.WithModifiers(f.Modifiers.Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithLeadingTrivia(SyntaxFactory.Space)))));
            }
            case "Use 'is null' check":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var bes = node.AncestorsAndSelf().OfType<BinaryExpressionSyntax>().FirstOrDefault(b => b.IsKind(SyntaxKind.EqualsExpression) && b.Right is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.NullLiteralExpression));
                if (bes == null) return null;
                var isNull = SyntaxFactory.IsPatternExpression(bes.Left, SyntaxFactory.ConstantPattern(SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression)));
                return doc.WithSyntaxRoot(root.ReplaceNode(bes, isNull.WithTriviaFrom(bes)));
            }
            case "Remove unnecessary parentheses":
            {
                var node = root.FindNode(diag.Location.SourceSpan);
                var paren = node.AncestorsAndSelf().OfType<ParenthesizedExpressionSyntax>().FirstOrDefault();
                if (paren == null) return null;
                return doc.WithSyntaxRoot(root.ReplaceNode(paren, paren.Expression.WithTriviaFrom(paren)));
            }
            default: return null;
        }
    }

    [McpServerTool, Description("Get available code actions and refactorings at a position.")]
    public static async Task<string> GetCodeActions([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file")] string sourceFile, [Description("Line")] int line = 0, [Description("Column")] int column = 0)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });

        var span = GetSpan(root.GetText(), line, column);
        var diags = (span.HasValue ? sm.GetDiagnostics(span.Value) : sm.GetDiagnostics());
        var actions = diags.SelectMany(d => KnownFixes.TryGetValue(d.Id, out var fixes) ? fixes.Select(f => new { Title = f, DiagnosticId = d.Id, Severity = d.Severity.ToString(), DiagnosticMessage = d.GetMessage(), Category = "CodeFix" }) : Enumerable.Empty<object>()).ToList();

        var node = span.HasValue ? root.FindNode(span.Value) : null;
        if (node is MethodDeclarationSyntax) { actions.Add(new { Title = "Convert to expression body", DiagnosticId = "", Severity = "Hidden", DiagnosticMessage = "", Category = "Refactoring" }); }
        if (node is PropertyDeclarationSyntax) { actions.Add(new { Title = "Convert to auto property", DiagnosticId = "", Severity = "Hidden", DiagnosticMessage = "", Category = "Refactoring" }); }

        var msg = actions.Count == 0 ? "No code actions available. Full Roslyn analyzer integration requires MEF-based analyzer loading." : $"{actions.Count} action(s) found. Use apply_code_action with the exact title.";
        return JsonHelper.Serialize(new { Success = true, Message = msg, Actions = actions });
    }

    [McpServerTool, Description("Get available code fixes for diagnostics at a position.")]
    public static async Task<string> GetCodeFixes([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file")] string sourceFile, [Description("Line")] int line = 0, [Description("Column")] int column = 0)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });

        var span = GetSpan(root.GetText(), line, column);
        var diags = (span.HasValue ? sm.GetDiagnostics(span.Value).Concat(root.GetDiagnostics().Where(d => d.Location.SourceSpan.IntersectsWith(span.Value))) : sm.GetDiagnostics().Concat(root.GetDiagnostics())).DistinctBy(d => (d.Id, d.GetMessage()));
        var fixes = diags.SelectMany(d =>
        {
            var lp = d.Location.GetLineSpan();
            return (KnownFixes.TryGetValue(d.Id, out var titles) ? titles : Array.Empty<string>()).Select(t => new { Title = t, DiagnosticId = d.Id, DiagnosticMessage = d.GetMessage(), Severity = d.Severity.ToString(), LineSpan = new { StartLine = lp.StartLinePosition.Line, StartColumn = lp.StartLinePosition.Character, EndLine = lp.EndLinePosition.Line, EndColumn = lp.EndLinePosition.Character } });
        }).ToList();

        var msg = fixes.Count == 0 ? "No code fixes available at this position. Apply using apply_code_action." : $"{fixes.Count} fix(es) available.";
        return JsonHelper.Serialize(new { Success = true, Message = msg, Fixes = fixes });
    }

    [McpServerTool, Description("Apply a specific code action or fix by its title.")]
    public static async Task<string> ApplyCodeAction([Description("Path to the .sln or .csproj")] string solutionPath, [Description("Source file")] string sourceFile, [Description("Action title")] string actionTitle, [Description("Line")] int line = 0, [Description("Column")] int column = 0)
    {
        var loaded = await WorkspaceService.LoadAsync(solutionPath); var doc = WorkspaceService.GetDocument(loaded.Solution, sourceFile);
        if (doc == null) return JsonHelper.Serialize(new { error = $"Document not found: {sourceFile}" });
        var sm = await WorkspaceService.GetSemanticModelAsync(doc); if (sm == null) return JsonHelper.Serialize(new { error = "Could not get semantic model" });
        var root = await doc.GetSyntaxRootAsync(); if (root == null) return JsonHelper.Serialize(new { error = "Could not get syntax root" });

        var span = GetSpan(root.GetText(), line, column);
        var diags = (span.HasValue ? sm.GetDiagnostics(span.Value).Concat(root.GetDiagnostics().Where(d => d.Location.SourceSpan.IntersectsWith(span.Value))) : sm.GetDiagnostics().Concat(root.GetDiagnostics())).DistinctBy(d => (d.Id, d.GetMessage())).ToList();

        // Try refactoring actions first
        if (actionTitle == "Convert to expression body" || actionTitle == "Convert to block body")
        {
            var dir = actionTitle == "Convert to expression body" ? "ToExpression" : "ToBlock";
            var n = new ExpressionBodyRewriter(dir).Visit(root);
            var changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(n).Project.Solution);
            return JsonHelper.Serialize(new { Success = true, Message = $"Applied: {actionTitle}", Changes = changes });
        }
        if (actionTitle == "Convert to auto property")
        {
            var prop = root.DescendantNodes().OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => { var s = p.GetLocation().GetLineSpan(); return s.StartLinePosition.Line <= line && s.EndLinePosition.Line >= line; });
            if (prop == null) return JsonHelper.Serialize(new { error = "No property found at position" });
            var autoP = SyntaxFactory.PropertyDeclaration(prop.Type, prop.Identifier).WithModifiers(prop.Modifiers).WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.List(new[] { SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)), SyntaxFactory.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration).WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)) })));
            var changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, doc.WithSyntaxRoot(root.ReplaceNode(prop, autoP)).Project.Solution);
            return JsonHelper.Serialize(new { Success = true, Message = $"Applied: {actionTitle}", Changes = changes });
        }

        // Try diagnostic-based fixes
        if (diags.Count > 0)
        {
            var result = await ApplySimpleFix(doc, diags[0], actionTitle);
            if (result != null)
            {
                var changes = await PreviewService.ComputeChangesAsync(doc.Project.Solution, result.Project.Solution);
                return JsonHelper.Serialize(new { Success = true, Message = $"Applied: {actionTitle}", Changes = changes });
            }
        }

        return JsonHelper.Serialize(new { error = $"Could not apply '{actionTitle}'. The fix may require full Roslyn analyzer infrastructure (MEF-based CodeFixProvider). Use the dedicated conversion and refactoring tools." });
    }
}

public class Program
{
    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => WorkspaceService.DisposeAll();
        Console.Error.WriteLine("[RoslynMcp.Cs] Starting MCP server over stdio...");

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.AddConsole(opts => opts.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
        await builder.Build().RunAsync();
    }
}
