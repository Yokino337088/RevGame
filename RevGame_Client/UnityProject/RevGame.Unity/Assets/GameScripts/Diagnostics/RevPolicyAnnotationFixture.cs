// 仅用于临时 PR，验证规范注解行列。
internal static class RevPolicyAnnotationFixture
{
    // 这里故意让 if 分支缺少大括号。
    private static void Check(bool ready)
    {
        if (ready) return;
        _ = UnityEngine.Resources
            .Load<UnityEngine.Object>("RevPolicyFixture");
    }
}
