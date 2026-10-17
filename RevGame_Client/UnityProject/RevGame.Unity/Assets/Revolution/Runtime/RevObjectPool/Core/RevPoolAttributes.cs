// ============================================================
// RevPoolAttributes.cs —— 池化预制体的声明式特性（与 UI 面板的 [RevUIPanel] 同一思路）
//
// 位置：Runtime\RevObjectPool\Core\
//
// 【为什么要把路径写在脚本身上】
//   旧写法每次取对象都要手填"根目录 + 资源名"两个字符串：
//       RevPool.GetAsync("Battle/Bullet", "Bullet_Normal", go => { ... });
//   字符串写错只有运行时才发现，而且同一个预制体在多处取用时要抄多遍。
//   现在把"这个物体的预制体在哪"**写在它自己的脚本上**（一处声明、处处使用）：
//
//       [RevPoolPrefab("Battle/Bullet", "Bullet_Normal")]     // 名字省略 = 用类名
//       public class Bullet : MonoBehaviour { ... }
//
//       RevPool.GetAsync<Bullet>(b => b.transform.position = muzzle.position);   // 取用处一个路径都不用写
//       RevPool.Return(bullet);
//
// 【和 [RevUIPanel] 的对应关系】
//   root  ↔ 资源根目录段（支持多级，结尾带不带 '/' 都行，也可以用生成的 RevResPath 常量）
//   name  ↔ 预制体资源名（不带扩展名），省略时取**脚本类名**
//
// 【本文件不引用 UnityEngine】可以被工程外的断言直接链接编译。
// ============================================================
using System;

namespace Revolution
{
    /// <summary>
    /// 池化预制体声明：写在预制体根节点上的 MonoBehaviour 类上，告诉对象池"我的预制体在哪"。
    ///
    /// 配合 <c>RevPool.Get&lt;T&gt;()</c> / <c>RevPool.GetAsync&lt;T&gt;(callback)</c> 使用：
    /// 业务取对象时只写类型，不再手填路径。
    ///
    /// ★ 只写在**预制体根节点上的那个脚本**；一个预制体上有多个脚本时，挑一个当"主脚本"声明即可。
    /// ★ 同一个预制体只能被一个类声明（否则两个类会各取各的池，造成同一个预制体两条池）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class RevPoolPrefabAttribute : Attribute
    {
        /// <summary>预制体所在的资源根目录段（必填），如 "Battle/Bullet"</summary>
        public string Root;

        /// <summary>预制体资源名（不带扩展名）；留空 = 用脚本类名</summary>
        public string Name;

        /// <summary>
        /// 资源分组（默认 Unknown）：决定 prefab 归属哪个业务域，退出该域前调用 <c>RevPool.DestroyGroup</c>。
        /// 例：子弹写 <c>Group = RevResGroup.Battle</c>。
        /// </summary>
        public RevResGroup Group = RevResGroup.Unknown;

        public RevPoolPrefabAttribute(string root, string name = null)
        {
            Root = root;
            Name = name;
        }
    }

    /// <summary>
    /// 某个类型 <typeparamref name="T"/> 上的 <see cref="RevPoolPrefabAttribute"/> 解析结果（每个类型只反射一次，之后直接读静态字段，零分配）。
    /// </summary>
    internal static class RevPoolPrefabInfo<T>
    {
        /// <summary>类型上有没有声明（没有就取不了，报错提示加特性）</summary>
        internal static readonly bool Declared;

        /// <summary>资源根目录</summary>
        internal static readonly string Root;

        /// <summary>资源名（已处理"留空用类名"）</summary>
        internal static readonly string ResName;

        /// <summary>资源分组</summary>
        internal static readonly RevResGroup Group;

        static RevPoolPrefabInfo()
        {
            object[] attrs = typeof(T).GetCustomAttributes(typeof(RevPoolPrefabAttribute), false);
            if (attrs.Length == 0) return;

            var attr = (RevPoolPrefabAttribute)attrs[0];
            Declared = true;
            Root = attr.Root ?? string.Empty;
            ResName = string.IsNullOrEmpty(attr.Name) ? typeof(T).Name : attr.Name;
            Group = attr.Group;
        }
    }
}
