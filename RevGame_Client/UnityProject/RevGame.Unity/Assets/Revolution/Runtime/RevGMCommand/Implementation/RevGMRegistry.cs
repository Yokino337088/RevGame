// RevGMRegistry.cs —— 注册表（框架内部：一张扁平表 + 一张"末段名"索引）
//
// 【两张表各干什么】
//   · 扁平表（完整名 → 命令）：执行时 O(1) 查找（王者也是"扁平仓库 + 树形分组"双存储，`03:159-198`）
//   · 末段名索引（`加金币` → 命令们）：支持"只写末段也能执行"；同名多条时**明确报歧义**而不是随便挑一个
//     （王者是"后注册者静默覆盖先注册者"，`03:202-221` —— 这里改成注册即报错，见 Add）
// 【分组树不在这里】分组只影响展示，面板自己按 `/` 现场长树就行（少一份状态就少一处不一致）。

using System;
using System.Collections.Generic;

namespace Revolution
{
    /// <summary>一次查找的结果。</summary>
    internal enum RevGMResolve
    {
        /// <summary>完整名命中</summary>
        FoundFull,

        /// <summary>只写了末段，且唯一</summary>
        FoundBase,

        /// <summary>只写了末段，但有多条同名命令</summary>
        Ambiguous,

        /// <summary>没有这条命令</summary>
        NotFound,
    }

    internal sealed class RevGMRegistry
    {
        private readonly Dictionary<string, RevGMCommand> _byName = new Dictionary<string, RevGMCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<RevGMCommand>> _byBase = new Dictionary<string, List<RevGMCommand>>(StringComparer.OrdinalIgnoreCase);
        private readonly List<RevGMCommand> _ordered = new List<RevGMCommand>(64);

        internal int Count => _ordered.Count;

        internal IReadOnlyList<RevGMCommand> All => _ordered;

        internal void Add(RevGMCommand command)
        {
            if (_byName.ContainsKey(command.Name))
                throw new InvalidOperationException(
                    $"GM 命令「{command.Name}」已经注册过了 —— 请把重名的两条合并（同名只允许一条，框架不做静默覆盖）。");

            _byName[command.Name] = command;
            _ordered.Add(command);

            if (!_byBase.TryGetValue(command.BaseName, out List<RevGMCommand> list))
            {
                list = new List<RevGMCommand>(2);
                _byBase[command.BaseName] = list;
            }

            list.Add(command);
        }

        internal bool Remove(string name)
        {
            if (!_byName.TryGetValue(name, out RevGMCommand command)) return false;

            _byName.Remove(name);
            _ordered.Remove(command);

            if (_byBase.TryGetValue(command.BaseName, out List<RevGMCommand> list))
            {
                list.Remove(command);
                if (list.Count == 0) _byBase.Remove(command.BaseName);
            }

            return true;
        }

        internal void Clear()
        {
            _byName.Clear();
            _byBase.Clear();
            _ordered.Clear();
        }

        /// <summary>
        /// 查一条命令：先按完整名，再按末段名。
        /// <para><paramref name="candidates"/> 只在"末段名有多条"时给出，用来提示用户写完整名。</para>
        /// </summary>
        internal RevGMResolve Resolve(string name, out RevGMCommand command, out List<RevGMCommand> candidates)
        {
            command = null;
            candidates = null;

            if (string.IsNullOrEmpty(name)) return RevGMResolve.NotFound;

            if (_byName.TryGetValue(name, out command)) return RevGMResolve.FoundFull;

            if (!_byBase.TryGetValue(name, out List<RevGMCommand> list)) return RevGMResolve.NotFound;

            if (list.Count == 1)
            {
                command = list[0];
                return RevGMResolve.FoundBase;
            }

            candidates = list;
            return RevGMResolve.Ambiguous;
        }
    }
}
