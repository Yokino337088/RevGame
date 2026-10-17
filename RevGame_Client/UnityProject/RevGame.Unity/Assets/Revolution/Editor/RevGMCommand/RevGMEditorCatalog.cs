// RevGMEditorCatalog.cs —— 编辑器侧的命令目录（面板的数据来源）
//
// 【两个来源，永不混用】
//   · Play 模式：直接读运行期注册表（RevGM.Commands）—— 这是"真的"，游戏里注册了什么就是什么
//   · 编辑模式：用 Unity 的 TypeCache 找到所有 [RevGMEntry] 方法 → 调用它们（它们只做注册）→
//              把命令清单**快照**下来 → 立刻把运行期注册表清空
//   （快照完就清空，是为了让编辑模式不干扰运行期：进 Play 后注册表是干净的，游戏自己的注册不会撞重名）
//
// 【为什么不用反射扫全程序集】TypeCache 是 Unity 官方为此提供的加速索引（编辑器专用、零运行期成本）；
//   运行期一行注册则完全不依赖反射（IL2CPP/AOT 安全）。

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Revolution.Editor
{
    /// <summary>GM 命令目录（编辑器侧）。</summary>
    internal static class RevGMEditorCatalog
    {
        /// <summary>一个 [RevGMEntry] 注册入口</summary>
        internal sealed class EntryInfo
        {
            internal MethodInfo Method;
            internal RevGMEntryAttribute Attribute;
            internal string Error;              // 调用失败的原因（面板会标红显示）

            internal string Display => (Method == null ? "?" : Method.DeclaringType?.Name + "." + Method.Name)
                                       + (string.IsNullOrEmpty(Attribute?.Description) ? string.Empty : " —— " + Attribute.Description);
        }

        private static RevGMCommand[] _snapshot;              // 编辑模式的命令快照
        private static EntryInfo[] _entries;                  // 注册入口清单
        private static bool _snapshotValid;
        private static bool _snapshotWasPlaying;

        static RevGMEditorCatalog()
        {
            AssemblyReloadEvents.afterAssemblyReload += Invalidate;      // 脚本重编译 → 重新扫描
            EditorApplication.playModeStateChanged += _ => Invalidate();  // 进出 Play → 数据来源切换
        }

        /// <summary>是否正在运行（Play 模式）</summary>
        internal static bool IsPlaying => EditorApplication.isPlaying;

        /// <summary>当前可用的命令（Play = 运行期注册表；编辑 = 编辑期快照）</summary>
        internal static IReadOnlyList<RevGMCommand> Commands
        {
            get
            {
                EnsureSnapshot();
                return IsPlaying ? RevGM.Commands : (IReadOnlyList<RevGMCommand>)_snapshot;
            }
        }

        /// <summary>注册入口清单（面板会展示"这批命令是从哪来的"）</summary>
        internal static IReadOnlyList<EntryInfo> Entries
        {
            get { EnsureSnapshot(); return _entries; }
        }

        /// <summary>让下一次访问重新扫描（面板的"刷新"按钮用它）</summary>
        internal static void Invalidate()
        {
            _snapshotValid = false;
            _snapshot = null;
        }

        /// <summary>执行一行命令（编辑模式不执行，只返回一句说明）</summary>
        internal static RevGMResult Execute(string commandLine)
        {
            if (!IsPlaying)
                return RevGMResult.Fail("还没进入 Play：编辑模式只能查看与联想（命令体常常要碰运行时对象，所以不在这里执行）");

            return RevGM.Execute(commandLine);
        }

        /// <summary>联想（编辑期用快照，Play 期用运行期数据）</summary>
        internal static IReadOnlyList<RevGMCommand> Suggest(string input, int max)
        {
            EnsureSnapshot();

            if (IsPlaying) return RevGM.Suggest(input, max);

            // 编辑模式：拿快照自己过滤（逻辑与运行期完全一致，避免两套匹配规则）
            List<RevGMCommand> result = new List<RevGMCommand>(max);
            string query = input?.Trim();

            if (string.IsNullOrEmpty(query))
            {
                for (int i = 0; i < _snapshot.Length && result.Count < max; i++)
                    if (!_snapshot[i].IsHidden) result.Add(_snapshot[i]);

                return result;
            }

            List<KeyValuePair<int, RevGMCommand>> scored = new List<KeyValuePair<int, RevGMCommand>>(16);

            for (int i = 0; i < _snapshot.Length; i++)
            {
                if (_snapshot[i].IsHidden) continue;

                int score = RevGMMatcher.Score(_snapshot[i], query);
                if (score > 0) scored.Add(new KeyValuePair<int, RevGMCommand>(score, _snapshot[i]));
            }

            scored.Sort((a, b) =>
            {
                int byScore = b.Key.CompareTo(a.Key);
                if (byScore != 0) return byScore;
                return a.Value.Name.Length.CompareTo(b.Value.Name.Length);
            });

            for (int i = 0; i < scored.Count && result.Count < max; i++) result.Add(scored[i].Value);

            return result;
        }

        // ============================================================
        // 内部
        // ============================================================

        private static void EnsureSnapshot()
        {
            if (_snapshotValid && _snapshotWasPlaying == IsPlaying) return;

            _snapshotWasPlaying = IsPlaying;

            if (IsPlaying)
            {
                // Play 模式：只列入口（命令清单直接读运行期注册表，不做快照）
                _entries = ScanEntries();
                _snapshot = Array.Empty<RevGMCommand>();
                _snapshotValid = true;
                return;
            }

            // 编辑模式：调用入口方法（它们只做注册）→ 快照 → 清空注册表（不干扰运行期）
            _entries = ScanEntries();

            RevGM.Clear();

            for (int i = 0; i < _entries.Length; i++)
            {
                EntryInfo entry = _entries[i];

                if (entry.Method == null) continue;

                if (!entry.Method.IsStatic)
                {
                    entry.Error = "[RevGMEntry] 方法必须是 static（编辑期没法 new 出你的实例）";
                    continue;
                }

                try
                {
                    entry.Method.Invoke(null, null);
                }
                catch (TargetInvocationException e)
                {
                    entry.Error = (e.InnerException ?? e).Message;
                }
                catch (Exception e)
                {
                    entry.Error = e.Message;
                }
            }

            List<RevGMCommand> snapshot = new List<RevGMCommand>(RevGM.Commands);
            RevGM.Clear();

            _snapshot = snapshot.ToArray();
            _snapshotValid = true;
        }

        private static EntryInfo[] ScanEntries()
        {
            List<EntryInfo> found = new List<EntryInfo>(4);

            foreach (MethodInfo method in TypeCache.GetMethodsWithAttribute<RevGMEntryAttribute>())
            {
                found.Add(new EntryInfo
                {
                    Method = method,
                    Attribute = method.GetCustomAttribute<RevGMEntryAttribute>(),
                });
            }

            found.Sort((a, b) => string.CompareOrdinal(a.Display, b.Display));
            return found.ToArray();
        }
    }
}
