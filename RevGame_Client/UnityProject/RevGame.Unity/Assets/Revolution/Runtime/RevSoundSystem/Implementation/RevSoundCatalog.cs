// RevSoundCatalog.cs —— 音效表：逻辑名 → 实际资源位置 + 默认参数（纯 C#：可脱离引擎单测）
//
// 【它解决什么】"代码里写的名字"和"磁盘上的目录结构"解耦：
//       RevSound.Register("ui_click", "UI/Button/click", kind: RevSoundKind.Ui, volume: 0.8f);
//       RevSound.Play("ui_click");        // 代码只认这个逻辑名，磁盘怎么整理都不用改调用点
//
// 【对标王者的哪条哲学】"元数据驱动：代码不认识任何具体的音效名，只认识 ID 和表"。
//   王者的表是二进制（SoundBankInfoSet.bytes，由工具生成）；这里是一张**代码注册的小表** ——
//   代价是"表与代码不同步"换成了"表就是代码"（可 diff、可 Code Review、无加载/解析成本）。
//
// 【解析规则（就三条）】
//   ① 没注册的名字：原样当路径用（= 以前的写法，子目录照样能用）；
//   ② 注册过的名字：用表里的路径，并用表里的"分类 / 音量 / 循环"补齐**调用点没写**的参数；
//   ③ 调用点显式传的参数**永远优先**于表里的默认值。
//
// 【本文件不引用 UnityEngine】所以能被工程外的断言直接链接编译。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>音效表：登记"逻辑名 → 资源路径（相对根目录，可带子目录）+ 默认分类/音量/循环"。</summary>
    internal sealed class RevSoundCatalog
    {
        /// <summary>一条登记项。</summary>
        internal struct Entry
        {
            /// <summary>资源路径（相对根目录；含子目录，如 "UI/Button/click"）</summary>
            internal string Path;

            /// <summary>分类（决定加载时用哪个根目录、以及是否同帧去重）</summary>
            internal RevSoundKind Kind;

            /// <summary>默认音量</summary>
            internal float Volume;

            /// <summary>是否循环</summary>
            internal bool Loop;
        }

        private readonly Dictionary<string, Entry> _map = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>已登记条数</summary>
        internal int Count => _map.Count;

        /// <summary>登记项只读视图（预加载"全部"时用）</summary>
        internal IReadOnlyDictionary<string, Entry> Entries => _map;

        /// <summary>
        /// 登记一条音效。同名重复登记 = **覆盖**（后登记的生效），返回 true 表示登记成功。
        /// 名字或路径为空（含只有空格/斜杠）→ 返回 false，不做任何改动。
        /// </summary>
        internal bool Register(string name, string path, RevSoundKind kind, float volume, bool loop)
        {
            name = RevResPathUtil.NormalizeResName(name);
            path = RevResPathUtil.NormalizeResName(path);

            if (name.Length == 0 || path.Length == 0) return false;

            _map[name] = new Entry
            {
                Path = path,
                Kind = kind,
                Volume = volume > 0f ? volume : 1f,
                Loop = loop,
            };

            return true;
        }

        /// <summary>注销一条音效（没登记过返回 false）。</summary>
        internal bool Unregister(string name) => _map.Remove(RevResPathUtil.NormalizeResName(name));

        /// <summary>这个名字登记过吗。</summary>
        internal bool IsRegistered(string name) => _map.ContainsKey(RevResPathUtil.NormalizeResName(name));

        /// <summary>清空整张表。</summary>
        internal void Clear() => _map.Clear();

        /// <summary>
        /// 解析一次播放请求（核心就这一段逻辑，且它是纯函数 —— 便于单测）：
        ///   · 没登记的名字 → 原样返回（当路径用），三个参数都不动；
        ///   · 登记过的名字 → 返回表里的路径，并用表里的值补齐**没写**的参数（<c>null</c> = 没写）；
        ///   · 调用点显式传的参数（非 null）保持不动 = 优先。
        /// </summary>
        internal string ResolvePath(string name, ref RevSoundKind? kind, ref float? volume, ref bool? loop)
        {
            if (!_map.TryGetValue(RevResPathUtil.NormalizeResName(name), out Entry entry)) return name;

            kind ??= entry.Kind;
            volume ??= entry.Volume;
            loop ??= entry.Loop;
            return entry.Path;
        }
    }
}
