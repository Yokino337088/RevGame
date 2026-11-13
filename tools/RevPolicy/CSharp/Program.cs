using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

// 输入使用 Git 已选定的旧版和新版文本，检查器不需要 Unity 项目生成的 csproj。
internal sealed record Request(FileChange[] Files, string[] Bootstraps, string[] RuntimeDirectories,
    string[] PanelDirectories, string[] InternalTypes);

// RelativePath 以 Assets 为基准，用于区分正式启动入口和 UI 面板目录。
internal sealed record FileChange(string Path, string RelativePath, string Before, string After);

// Key 保存违规语法的结构。同一条旧违规移行后仍可识别，新出现的同类违规会报出。
internal sealed record Finding(string Code, string Key, int Line, string Message);

// 输出只包含文件、行号和修复提示，Python 调用方负责显示和退出码。
internal sealed record Diagnostic(string Path, int Line, string Code, string Message);

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

    // 旧问题按语法和出现次数消耗，允许渐进启用，无需一次重排存量脚本。
    private static void Main()
    {
        var request = JsonSerializer.Deserialize<Request>(Console.In.ReadToEnd())
            ?? throw new InvalidOperationException("缺少 C# 检查请求。");
        var diagnostics = new List<Diagnostic>();
        foreach (FileChange file in request.Files)
        {
            var old = Inspect(file.Before, file.RelativePath, request)
                .GroupBy(item => (item.Code, item.Key))
                .ToDictionary(group => group.Key, group => group.Count());
            foreach (Finding item in Inspect(file.After, file.RelativePath, request))
            {
                var key = (item.Code, item.Key);
                if (old.TryGetValue(key, out int count) && count > 0)
                {
                    old[key] = count - 1;
                    continue;
                }
                diagnostics.Add(new Diagnostic(file.Path, item.Line, item.Code, item.Message));
            }
        }
        Console.Write(JsonSerializer.Serialize(diagnostics));
    }

    // 每套符号读取该配置的真实语法树。相同行只报告一次，注释和字符串不会误报成 C#。
    private static IEnumerable<Finding> Inspect(string source, string path, Request request)
    {
        var seen = new HashSet<(string Code, int Line, string Key)>();
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
                    if (seen.Add((finding.Code, finding.Line, finding.Key)))
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
                    Finding finding = At("REV204", duplicate.GetFirstToken(), "duplicate-init", "同一启动入口只初始化一次。", text);
                    if (seen.Add((finding.Code, finding.Line, finding.Key)))
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
        if (node is IfStatementSyntax ifStatement && !Expanded(ifStatement.Statement, text))
        {
            yield return At("REV200", ifStatement.IfKeyword, ifStatement.Condition.ToString(),
                "if 或 else if 需要大括号，左右大括号各占一行。", text);
        }
        if (node is ElseClauseSyntax clause &&
            (!OwnLine(clause.ElseKeyword, text, allowIf: clause.Statement is IfStatementSyntax) ||
             (clause.Statement is not IfStatementSyntax && !Expanded(clause.Statement, text))))
        {
            yield return At("REV200", clause.ElseKeyword, clause.Statement.Kind().ToString(),
                "else 或 else if 需要独立起行，else 的大括号各占一行。", text);
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
                yield return At("REV204", call.GetFirstToken(), api, "框架初始化集中在项目启动入口。", text);
            }
            if (IsApi(api, "SceneManager.LoadScene") || IsApi(api, "SceneManager.LoadSceneAsync") ||
                IsApi(api, "SceneManager.UnloadSceneAsync"))
            {
                yield return At("REV205", call.GetFirstToken(), api, "业务通过 GameFlow 和 RevScene 交接场景。", text);
            }
            if (panel && (IsApi(api, "Destroy") || IsApi(api, "DestroyImmediate")) &&
                call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } target &&
                Spelling(target) is "gameObject" or "this.gameObject")
            {
                yield return At("REV206", call.GetFirstToken(), api, "面板关闭自身时调用 CloseSelf()。", text);
            }
        }
    }

    // 格式检查保留行内注释，并要求括号前后没有其他代码 token。
    private static bool Expanded(StatementSyntax statement, SourceText text)
    {
        return statement is BlockSyntax block && OwnLine(block.OpenBraceToken, text) && OwnLine(block.CloseBraceToken, text);
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

    // 每条诊断定位到实际 token，便于终端和 CI 直接跳到对应行。
    private static Finding At(string code, SyntaxToken token, string key, string message, SourceText text)
    {
        return new Finding(code, key, text.Lines.GetLineFromPosition(token.SpanStart).LineNumber + 1, message);
    }
}
