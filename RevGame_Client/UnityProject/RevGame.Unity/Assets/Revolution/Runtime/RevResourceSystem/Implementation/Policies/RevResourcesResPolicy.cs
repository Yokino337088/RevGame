// ============================================================
// RevResourcesResPolicy.cs —— Resources 策略（链尾兜底）
//
// 位置：Runtime\资源加载\Sub\
//
// 负责两类资源：
//   ① 特殊资源：业务显式以 "Res/" 开头指定的；
//   ② 兜底资源：AB 里加载不到的（没打进包 / 包坏了），
//      RevABResPolicy.AllowFallback = true 会自动把请求转到这里再试一次。
//
// 【为什么 Match 恒为 true？】
//   它是链尾。请求能走到它面前，说明前面的策略要么不管、要么失败了，
//   此时"再试一次"总比直接失败好。能否加载出来由 Resources.Load 决定。
// ============================================================
namespace Revolution
{
    public class RevResourcesResPolicy : IRevResPolicy
    {
        /// <summary>约定前缀：带这个前缀的路径明确要求走 Resources</summary>
        public const string PREFIX = "Res/";

        private readonly RevResourcesLoader _loader = new RevResourcesLoader();

        // 链尾：前面都没接住就由我接
        public bool Match(string standardPath) => true;

        // 映射规则：带 "Res/" 前缀去掉前缀；不带前缀（兜底进来的）原样使用
        public string MapPath(string standardPath, System.Type contentType)
            => standardPath.StartsWith(PREFIX) ? standardPath.Substring(PREFIX.Length) : standardPath;

        public IRevResLoader CreateLoader() => _loader;

        public bool AllowFallback => false;   // 链尾，无需再兜底
    }
}
