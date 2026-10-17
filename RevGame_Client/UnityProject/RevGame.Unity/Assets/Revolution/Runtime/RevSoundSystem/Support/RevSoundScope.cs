// RevSoundScope.cs —— 声音作用域：一块边界，退出时把这块播的声音全停掉
//
// 【典型用法】切界面、进一局战斗、放一段剧情：
//   using (var s = RevSound.OpenScope())
//   {
//       s.Play("ui_open_big");                          // 2D 弹窗音
//       s.PlayOn("npc_talk", npc.gameObject);           // 3D 挂在 NPC 身上
//       s.PlayBgm("battle_prepare", 0.5f);              // 这一段的 BGM
//   }   // ← 出块：上面播的（含 BGM）全部停掉
//
// 【对标王者的哪条哲学】"生命周期即作用域"：王者用 14 个 Bank 域 + 一行批量回收，
//   解决的是"几百个资源什么时候该收"；这里解决同一件事的小号版本 —— "这一块播的声音什么时候该收"。
//   它把你旧框架里那句"切场景前记得 ClearSound()"（还写了三遍）变成了类型层面的边界：忘了写 using 才是例外。
//
// 【它不管资源】作用域只负责"停声音"，不负责卸载 clip —— 因为两个作用域可能用同一个片段，
//   谁也别替谁释放（资源由 RevSound.Preload / Unload / UnloadAll 显式管理）。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 声音作用域：块内播放的声音在 <see cref="Dispose"/> 时统一停掉（可重复 Dispose）。
    /// </summary>
    public sealed class RevSoundScope : IDisposable
    {
        private readonly RevSoundCore _core;
        private readonly List<RevSoundHandle> _handles;
        private bool _disposed;

        internal RevSoundScope(RevSoundCore core)
        {
            _core = core;
            _handles = new List<RevSoundHandle>(8);
        }

        /// <summary>块内播放一个 2D 音效（参数同 <see cref="RevSound.Play(string, float?, RevSoundKind?)"/>）</summary>
        public RevSoundHandle Play(string name, float? volume = null, RevSoundKind? kind = null)
            => Track(_core.Play(name, kind, volume, null, null, is3D: false, loop: null));

        /// <summary>块内播放一个 3D 音效并挂到物体上（参数同 <see cref="RevSound.PlayOn(string, GameObject, float?, bool?, RevSoundKind?)"/>）</summary>
        public RevSoundHandle PlayOn(string name, GameObject target, float? volume = null, bool? loop = null,
                                     RevSoundKind? kind = null)
            => PlayOn(name, target != null ? target.transform : null, volume, loop, kind);

        /// <inheritdoc cref="PlayOn(string, GameObject, float?, bool?, RevSoundKind?)"/>
        public RevSoundHandle PlayOn(string name, Transform target, float? volume = null, bool? loop = null,
                                     RevSoundKind? kind = null)
            => Track(_core.Play(name, kind, volume, null, target, is3D: true, loop: loop));

        /// <summary>块内播放一个 3D 音效并挂到世界坐标点（参数同 <see cref="RevSound.PlayAt"/>）</summary>
        public RevSoundHandle PlayAt(string name, Vector3 position, float? volume = null, RevSoundKind? kind = null)
            => Track(_core.Play(name, kind, volume, position, null, is3D: true, loop: null));

        /// <summary>块内播放背景音乐（出块时会被停掉）</summary>
        public RevSoundHandle PlayBgm(string name, float fadeSeconds = 0f, float? volume = null)
            => Track(_core.PlayBgm(name, volume, fadeSeconds));

        /// <summary>停掉这块播过的所有声音（作用域仍然可用，可以接着播）</summary>
        public void StopAll()
        {
            for (int i = 0; i < _handles.Count; i++) _handles[i].Stop();
            _handles.Clear();
        }

        /// <summary>出块：停掉这块播过的所有声音（重复调用安全）</summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            StopAll();
        }

        private RevSoundHandle Track(RevSoundHandle handle)
        {
            if (!handle.IsEmpty) _handles.Add(handle);
            return handle;
        }
    }
}
