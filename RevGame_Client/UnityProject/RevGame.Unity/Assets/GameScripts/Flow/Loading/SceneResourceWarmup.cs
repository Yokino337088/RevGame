using System;
using System.Collections.Generic;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Flow
{
    /// <summary>
    /// 声明本游戏的预热清单与数量。交接对象自动管理资源引用，池实例在目标域打开后才预建。
    /// GameFlow 结束或取消流程时调用 Dispose，归还仍在批量预热中暂借的实例。
    /// </summary>
    internal sealed class SceneResourceWarmup : IDisposable
    {
        /// <summary>预热时先同时持有实例，避免“取一个立刻还一个”实际上只生成了同一个对象。</summary>
        private readonly List<Component> _instances = new List<Component>(24 + BoardView.MaxFloatingTexts + BoardView.MaxProjectiles + BoardView.MaxMeleeEffects + 2);
        /// <summary>过场结束或取消后不再继续预建实例。</summary>
        private bool _disposed;

        /// <summary>本游戏首屏或后续反馈要提前读取的预制体。</summary>
        private readonly struct Resource
        {
            /// <summary>框架生成的逻辑目录。</summary>
            public readonly string Root;
            /// <summary>预制体逻辑名。</summary>
            public readonly string Name;
            /// <summary>分组以第一次加载为准，所以预载必须沿用正式消费者的组，不能一律塞进Scene。</summary>
            public RevResGroup Group
            {
                get
                {
                    return Root == RevResPath.UI_Panel ? RevResGroup.UI : RevResGroup.Battle;
                }
            }
            /// <summary>登记清单的一条逻辑路径。</summary>
            public Resource(string root, string name)
            {
                Root = root;
                Name = name;
            }
        }
        /// <summary>按屏幕预热资源，并将每个真实等待点登记到本次交接。即使命中缓存也逐项让帧。</summary>
        public async RevTask LoadAsync(bool battle, RevSceneHandoffResources trace, Action<float> progress)
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
                // 当前流程逐项等待完整清单，包含为后续反馈提前加载的 prefab，这些等待都会推迟首屏。
                RevResHandle handle = await trace.PreloadAsync<GameObject>(resource.Root, resource.Name, nameof(SceneResourceWarmup),
                    blocksFirstFrame: true, group: resource.Group);

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
        /// <summary>从已进入的目标域借实例，冷读返回失败时由上层交接处理。</summary>
        private static RevTask<T> GetAsync<T>() where T : Component
        {
            var completion = new RevTaskCompletionSource<T>();
            GamePoolRequests.GetAsync<T>(value => completion.SetResult(value));
            return completion.Task;
        }
        /// <summary>成批归还本次暂借实例，空闲池仍归交接后的业务域。</summary>
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
        /// <summary>停止批量预热，归还暂借实例。预制体预热引用由场景交接自动归还。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            ReturnInstances();
        }
    }
}
