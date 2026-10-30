using System;
using System.Collections.Generic;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    /// <summary>
    /// 一次过场专用的资源租约清单，不替代框架加载器。只列出这款游戏即将使用的资源，逐项走RevResManager。
    /// 创建者GameFlow在finally中Dispose：预载引用会配对释放，面板/对象池自己的引用继续保留。
    /// 预载资源与实例预热分开：RevScene默认会清池，所以必须等新场景载入后才创建池实例。
    /// </summary>
    internal sealed class SceneResourceWarmup : IDisposable
    {
        /// <summary>本次Load调用的账本。先登记再请求，缓存命中同步回调、失败或中途退出也不会漏Release。</summary>
        private readonly List<Resource> _leases = new List<Resource>(12);
        /// <summary>预热时先同时持有实例，避免“取一个立刻还一个”实际上只生成了同一个对象。</summary>
        private readonly List<Component> _instances = new List<Component>(24 + BoardView.MaxFloatingTexts + BoardView.MaxProjectiles + BoardView.MaxMeleeEffects + 2);
        private bool _disposed;

        private readonly struct Resource
        {
            /// <summary>框架生成的逻辑目录和资源名；它们同时用于Load与Release，不能换成磁盘路径。</summary>
            public readonly string Root;
            public readonly string Name;
            /// <summary>分组以第一次加载为准，所以预载必须沿用正式消费者的组，不能一律塞进Scene。</summary>
            public RevResGroup Group
            {
                get
                {
                    return Root == RevResPath.UI_Panel ? RevResGroup.UI : RevResGroup.Battle;
                }
            }
            public Resource(string root, string name)
            {
                Root = root;
                Name = name;
            }
        }
        /// <summary>按屏幕寿命选择资源；每次异步完成后Yield，即使全部命中缓存也不会一次集中处理所有对象。</summary>
        public async RevTask LoadAsync(bool battle, Action<float> progress)
        {
            Resource[] resources = battle ? new[]
            {
                new Resource(RevResPath.Board_Tile, "BoardCell"),
                new Resource(RevResPath.Pieces_Unit, "SlimePiece"),
                new Resource(RevResPath.Effects_Combat, "BattleEffect"),
                new Resource(RevResPath.Effects_Combat, "ScoreSpark"),
                // Boss 技能特效：进战斗时一并预载，避免 Boss 第一次放技能时同步读 AB 造成卡顿。
                new Resource(RevResPath.Effects_Combat, "BossSkillFx"),
                new Resource(RevResPath.UI_Item_Notice, "BattleEventNotice"),
                new Resource(RevResPath.UI_Panel, "BattleHudPanel"),
                new Resource(RevResPath.UI_Panel, "PreparePanel"),
                new Resource(RevResPath.UI_Panel, "SettlePanel"),
                new Resource(RevResPath.UI_Panel, "RewardPanel")
            } : new[] { new Resource(RevResPath.UI_Panel, "HomePanel"), new Resource(RevResPath.UI_Panel, "LevelSelectPanel") };

            for (int i = 0; i < resources.Length; i++)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException("预热已取消");
                }
                Resource resource = resources[i];
                _leases.Add(resource);
                var completion = new RevTaskCompletionSource<RevResHandle>();
                RevResManager.LoadAsync(resource.Root, resource.Name, typeof(GameObject), h => completion.SetResult(h), resource.Group);
                RevResHandle handle = await completion.Task;

                if (!handle.IsLoaded)
                {
                    throw new InvalidOperationException(resource.Name + "预载失败：" + handle.ErrorReason);
                }
                progress?.Invoke((i + 1f) / resources.Length);
                await RevTask.Yield();
            }
        }
        /// <summary>
        /// 预创建24颗棋子、71个反馈实例（35弹道+20文字+16近战）、2条事件通知。它们仍由RevPool管理，第一次交火不再集中Instantiate。
        /// 每帧最多2个，且单批累计超过3毫秒就主动让帧；单个Unity实例化不可拆分，不能承诺绝对无卡帧。
        /// </summary>
        public async RevTask WarmBattlePoolsAsync(Action<float> progress)
        {
            const int feedbackEnd = 24 + BoardView.MaxFloatingTexts + BoardView.MaxProjectiles + BoardView.MaxMeleeEffects;
            const int regularEnd = feedbackEnd + UI.BattleHudPanel.MaxScoreSparks + 2;
            // 8组Boss预算加炸弹预警/落地的2组余量；资源预加载不等于实例已创建，必须同时持有全部租约。
            const int count = regularEnd + BoardView.BossFxWarmCount;
            float sliceStart = Time.realtimeSinceStartup;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (_disposed)
                    {
                        throw new OperationCanceledException("对象池预热已取消");
                    }
                    Component instance;

                    if (i < 24)
                    {
                        instance = await GetAsync<PieceView>();
                    }
                    else

                        if (i < feedbackEnd)
                        {
                            instance = await GetAsync<BattleEffectView>();
                        }
                        else

                            if (i < feedbackEnd + UI.BattleHudPanel.MaxScoreSparks)
                            {
                                instance = await GetAsync<ScoreSparkView>();
                            }
                            else if (i < regularEnd)
                            {
                                instance = await GetAsync<BattleEventNoticeView>();
                            }
                            else
                            {
                                instance = await GetAsync<BossSkillFxView>();
                            }
                    // 退出发生在await期间时，不能再把迟到实例记入已经清空的清单。
                    if (_disposed)
                    {
                        if (instance != null)
                        {
                            RevPool.Return(instance, 0);
                        }
                        throw new OperationCanceledException("预热过程中流程已退出");
                    }
                    if (instance == null)
                    {
                        throw new InvalidOperationException("对象池预热失败，序号" + i);
                    }
                    _instances.Add(instance);
                    if (instance is BossSkillFxView skill)
                    {
                        // 先登记租约再await：取消/异常会由finally成对归还，不能在await后才记账。
                        await skill.WarmSlotsAsync(() => _disposed);
                    }
                    progress?.Invoke((i + 1f) / count);

                    if (i % 2 == 1 || Time.realtimeSinceStartup - sliceStart >= 0.003f)
                    {
                        await RevTask.Yield();
                        sliceStart = Time.realtimeSinceStartup;
                    }
                }
            }
            finally { ReturnInstances(); }
        }
        private static RevTask<T> GetAsync<T>() where T : Component
        {
            var completion = new RevTaskCompletionSource<T>();
            GamePoolRequests.GetAsync<T>(value => completion.SetResult(value));
            return completion.Task;
        }
        private void ReturnInstances()
        {
            foreach (Component instance in _instances)
            {
                if (instance != null)
                {
                    RevPool.Return(instance, 0);
                }
            }
            _instances.Clear();
        }
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReturnInstances();
            foreach (Resource resource in _leases)
            {
                RevResManager.Release(resource.Root, resource.Name);
            }
            _leases.Clear();
        }
    }
}
