using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// 输入使用 Git 已选定的旧版和新版文本，检查器不需要 Unity 项目生成的 csproj。
internal sealed record Request(FileChange[] Files, string[] Bootstraps, string[] RuntimeDirectories,
    string[] PanelDirectories, string[] InternalTypes);

// RelativePath 以 Assets 为基准。Unchanged 记录 Git 快照间保留的文本行。
internal sealed record FileChange(string Path, string RelativePath, string Before, string After, LineMatch[] Unchanged);

// 比对旧、新文件时保留对应行号，优先消耗仍在原文中的诊断。
internal sealed record LineMatch(int BeforeStart, int AfterStart, int Count);

// Key 保存违规语法的结构。同一条旧违规移行后仍可识别，新出现的同类违规会报出。
internal sealed record Finding(string Code, string Key, int Line, int Column, string Message);

// 行列为文件中的物理位置，从 1 起算。Python 调用方负责显示和退出码。
internal sealed record Diagnostic(string Path, int Line, int Column, string Code, string Message);

internal static class Program
{
    // Unity 常用编译组合覆盖编辑器、开发版 Player 和 Android/iOS/WebGL 玩家路径。
    private static readonly string[][] Symbols =
    {
        Array.Empty<string>(),
        new[] { "UNITY_EDITOR", "UNITY_STANDALONE_WIN", "DEVELOPMENT_BUILD", "REV_AUTOMATION" },
        new[] { "UNITY_ANDROID", "DEVELOPMENT_BUILD", "REV_AUTOMATION" },
        new[] { "UNITY_ANDROID" },
        new[] { "UNITY_IOS" },
        new[] { "UNITY_WEBGL" },
        new[] { "REVGAME_OFFLINE_TESTS" },
    };

    // 从调用方接收 Git 快照，输出其中新增的 C# 违规。
    private static void Main()
    {
        var request = JsonSerializer.Deserialize<Request>(Console.In.ReadToEnd())
            ?? throw new InvalidOperationException("缺少 C# 检查请求。");
        var diagnostics = new List<Diagnostic>();
        foreach (FileChange file in request.Files)
        {
            foreach (Finding item in NewFindings(file, request))
            {
                diagnostics.Add(new Diagnostic(file.Path, item.Line, item.Column, item.Code, item.Message));
            }
        }
        Console.Write(JsonSerializer.Serialize(diagnostics));
    }

    // 先匹配未改动的物理行，再用语法和出现次数消耗剩余旧诊断。
    // 这样插在旧违规前面的同类违规会标在新位置，移动的旧规则仍可按原契约匹配。
    private static IEnumerable<Finding> NewFindings(FileChange file, Request request)
    {
        var old = Inspect(file.Before, file.RelativePath, request).ToHashSet();
        var current = Inspect(file.After, file.RelativePath, request).ToList();
        var byPosition = old.ToLookup(item => (item.Line, item.Column, item.Code, item.Key, item.Message));
        var remaining = new List<Finding>();
        foreach (Finding item in current)
        {
            LineMatch? match = file.Unchanged.FirstOrDefault(range =>
                item.Line >= range.AfterStart && item.Line - range.AfterStart < range.Count);
            int oldLine = match == null ? 0 : match.BeforeStart + item.Line - match.AfterStart;
            Finding? original = byPosition[(oldLine, item.Column, item.Code, item.Key, item.Message)].FirstOrDefault();
            if (original == null || !old.Remove(original))
            {
                remaining.Add(item);
            }
        }

        var counts = old.GroupBy(item => (item.Code, item.Key))
            .ToDictionary(group => group.Key, group => group.Count());
        foreach (Finding item in remaining)
        {
            var key = (item.Code, item.Key);
            if (counts.TryGetValue(key, out int count) && count > 0)
            {
                counts[key] = count - 1;
                continue;
            }
            yield return item;
        }
    }

    // 每套符号读取该配置的真实语法树。同一位置跨配置去重，同行不同位置分别报告。
    private static IEnumerable<Finding> Inspect(string source, string path, Request request)
    {
        var seen = new HashSet<Finding>();
        bool runtime = request.RuntimeDirectories.Any(directory => path.StartsWith(directory + "/", StringComparison.Ordinal));
        bool panel = request.PanelDirectories.Any(directory => path.StartsWith(directory + "/", StringComparison.Ordinal));
        bool boot = request.Bootstraps.Contains(path, StringComparer.Ordinal);
        var internalTypes = request.InternalTypes.ToHashSet(StringComparer.Ordinal);
        foreach (string[] symbols in Symbols)
        {
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols));
            SourceText text = tree.GetText();
            SyntaxNode[] nodes = tree.GetRoot().DescendantNodes().ToArray();
            foreach (SyntaxNode node in nodes)
            {
                foreach (Finding finding in InspectNode(node, text, runtime, panel, boot, internalTypes))
                {
                    if (seen.Add(finding))
                    {
                        yield return finding;
                    }
                }
            }
            if (boot)
            {
                var initializers = nodes.OfType<InvocationExpressionSyntax>()
                    .Where(call => IsApi(Spelling(call.Expression), "RevInitialize.Initialize"));
                foreach (InvocationExpressionSyntax duplicate in initializers.Skip(1))
                {
                    Finding finding = At("REV204", CallToken(duplicate), "duplicate-init", "同一启动入口只初始化一次。", text);
                    if (seen.Add(finding))
                    {
                        yield return finding;
                    }
                }
            }
        }
    }

    // 检查有明确语法证据的约定。领域职责、资源所有者和注释质量由业务审查确认。
    private static IEnumerable<Finding> InspectNode(SyntaxNode node, SourceText text, bool runtime, bool panel, bool boot, HashSet<string> internalTypes)
    {
        if (node is IfStatementSyntax branch && !OwnLine(branch.IfKeyword, text, allowElse: branch.Parent is ElseClauseSyntax))
        {
            yield return At("REV200", branch.IfKeyword, branch.Condition.ToString(), "if 或 else if 的条件应单独起行。", text);
        }
        if (node is IfStatementSyntax ifStatement && BranchBodyIssue(ifStatement.Statement, text) is { } ifToken)
        {
            yield return At("REV200", ifToken, ifStatement.Condition.ToString(),
                "if 或 else if 需要大括号，左右大括号各占一行。", text);
        }
        if (node is ElseClauseSyntax clause)
        {
            bool chained = clause.Statement is IfStatementSyntax;
            SyntaxToken? issue = !OwnLine(clause.ElseKeyword, text, allowIf: chained) ? clause.ElseKeyword
                : chained ? null : BranchBodyIssue(clause.Statement, text);
            if (issue is { } elseToken)
            {
                yield return At("REV200", elseToken, clause.Statement.Kind().ToString(),
                    "else 或 else if 需要独立起行，else 的大括号各占一行。", text);
            }
        }
        if (!runtime)
        {
            yield break;
        }
        if (node is UsingDirectiveSyntax usingDirective && usingDirective.Name is { } usingName &&
            IsTaskNamespace(Spelling(usingName)))
        {
            yield return At("REV201", usingDirective.UsingKeyword, Spelling(usingName), "业务异步使用 RevTask。", text);
        }
        if (node is QualifiedNameSyntax or AliasQualifiedNameSyntax &&
            node.Parent is not (QualifiedNameSyntax or AliasQualifiedNameSyntax or UsingDirectiveSyntax) &&
            IsTaskNamespace(Spelling(node)))
        {
            yield return At("REV201", node.GetFirstToken(), Spelling(node), "业务异步使用 RevTask。", text);
        }
        if (node is SimpleNameSyntax name && internalTypes.Contains(name.Identifier.ValueText))
        {
            yield return At("REV202", name.Identifier, name.Identifier.ValueText, "业务通过 Revolution 公开门面调用框架。", text);
        }
        if (node is MemberAccessExpressionSyntax member)
        {
            string api = Spelling(member);
            if (IsApi(api, "Application.streamingAssetsPath") || IsApi(api, "Application.dataPath") ||
                IsApi(api, "Resources.Load") || IsApi(api, "Resources.LoadAll") || IsApi(api, "Resources.LoadAsync"))
            {
                yield return At("REV203", member.Name.Identifier, api, "业务资源使用 RevResManager 的逻辑路径。", text);
            }
            if (IsTaskNamespace(api) && member.Parent is not MemberAccessExpressionSyntax)
            {
                yield return At("REV201", member.Name.Identifier, api, "业务异步使用 RevTask。", text);
            }
        }
        if (node is InvocationExpressionSyntax call)
        {
            string api = Spelling(call.Expression);
            if ((!boot && IsApi(api, "RevInitialize.Initialize")) || IsApi(api, "RevResBootstrap.Instance.Init"))
            {
                yield return At("REV204", CallToken(call), api, "框架初始化集中在项目启动入口。", text);
            }
            if (IsApi(api, "SceneManager.LoadScene") || IsApi(api, "SceneManager.LoadSceneAsync") ||
                IsApi(api, "SceneManager.UnloadSceneAsync"))
            {
                yield return At("REV205", CallToken(call), api, "业务通过 GameFlow 和 RevScene 交接场景。", text);
            }
            if (panel && (IsApi(api, "Destroy") || IsApi(api, "DestroyImmediate")) &&
                call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } target &&
                Spelling(target) is "gameObject" or "this.gameObject")
            {
                yield return At("REV206", CallToken(call), api, "面板关闭自身时调用 CloseSelf()。", text);
            }
        }
    }

    // 无大括号时指向分支语句，格式错误时指向第一个需要移到独立行的大括号。
    private static SyntaxToken? BranchBodyIssue(StatementSyntax statement, SourceText text)
    {
        if (statement is not BlockSyntax block)
        {
            return statement.GetFirstToken(includeZeroWidth: true);
        }
        if (!OwnLine(block.OpenBraceToken, text))
        {
            return block.OpenBraceToken;
        }
        return OwnLine(block.CloseBraceToken, text) ? null : block.CloseBraceToken;
    }

    // else if 的两个关键字可以共行。行内注释不参与格式判断。
    private static bool OwnLine(SyntaxToken token, SourceText text, bool allowElse = false, bool allowIf = false)
    {
        if (token.IsMissing)
        {
            return false;
        }
        TextLine line = text.Lines.GetLineFromPosition(token.SpanStart);
        SyntaxToken previous = token.GetPreviousToken();
        SyntaxToken next = token.GetNextToken();
        bool after = previous.RawKind == 0 || previous.Span.End <= line.Start ||
                     (allowElse && previous.IsKind(SyntaxKind.ElseKeyword));
        if (token.IsKind(SyntaxKind.IfKeyword))
        {
            return after;
        }
        bool before = next.RawKind == 0 || next.SpanStart >= line.End ||
                      (allowIf && next.IsKind(SyntaxKind.IfKeyword));
        return after && before;
    }

    // 去除注释、空白和 global 前缀，让相同限定名使用相同判断。
    private static string Spelling(SyntaxNode node)
    {
        return string.Concat(node.DescendantTokens().Select(token => token.Text)).Replace("global::", "");
    }

    // 支持完全限定名。规则针对团队正常调用形式，Unity 编译仍负责最终符号解析。
    private static bool IsApi(string actual, string expected)
    {
        string name = actual.Split('<', 2)[0];
        return name == expected || name.EndsWith("." + expected, StringComparison.Ordinal);
    }

    // Task 别名声明、全限定类型和静态调用落在同一个命名空间约束。
    private static bool IsTaskNamespace(string name)
    {
        return name == "System.Threading.Tasks" || name.StartsWith("System.Threading.Tasks.", StringComparison.Ordinal);
    }

    // 带限定名的调用指向方法本身，即使接收者与方法分成了多行。
    private static SyntaxToken CallToken(InvocationExpressionSyntax call)
    {
        return call.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier : call.Expression.GetFirstToken();
    }

    // 每条诊断定位到实际 token，便于终端和 CI 跳到对应行列。
    private static Finding At(string code, SyntaxToken token, string key, string message, SourceText text)
    {
        LinePosition position = text.Lines.GetLinePosition(token.SpanStart);
        return new Finding(code, key, position.Line + 1, position.Character + 1, message);
    }
}
