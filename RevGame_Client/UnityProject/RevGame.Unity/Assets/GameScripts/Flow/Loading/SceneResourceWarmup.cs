using System;
using System.Collections.Generic;
using RevGame.View;
using Revolution;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
using UnityEngine.Rendering;
#endif

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
        /// <summary>按屏幕预热资源。网页有限并发获取独立资源，每批结清后让帧，保留原来的交接和失败边界。</summary>
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

#if UNITY_WEBGL && !UNITY_EDITOR
            const int batchSize = 3;
#else
            const int batchSize = 1;
#endif
            for (int i = 0; i < resources.Length; i += batchSize)
            {
                if (_disposed)
                {
                    throw new OperationCanceledException("预热已取消");
                }
                int count = Math.Min(batchSize, resources.Length - i);
                if (count == 1)
                {
                    await LoadResourceAsync(resources[i], trace);
                }
                else
                {
                    // 每项沿用交接的租约和共享 AB 请求。WhenAll 等整批收尾后才向外报告失败。
                    var batch = new RevTask[count];
                    for (int j = 0; j < count; j++)
                    {
                        batch[j] = LoadResourceAsync(resources[i + j], trace);
                    }
                    await RevTask.WhenAll(batch);
                }
                progress?.Invoke((i + count) / (float)resources.Length);
                await RevTask.Yield();
            }
        }

        /// <summary>预热一项并登记在本次交接。失败原因仍归属原逻辑资源，租约由交接统一收尾。</summary>
        private static async RevTask LoadResourceAsync(Resource resource, RevSceneHandoffResources trace)
        {
            RevResHandle handle = await trace.PreloadAsync<GameObject>(resource.Root, resource.Name, nameof(SceneResourceWarmup),
                blocksFirstFrame: true, group: resource.Group);
            if (!handle.IsLoaded)
            {
                throw new InvalidOperationException(resource.Name + "预载失败：" + handle.ErrorReason);
            }
        }
        /// <summary>
        /// 预创建24颗棋子、按本平台表现预算准备弹道/飘字/近战实例，并预建2条事件通知。它们仍由RevPool管理，第一次交火不再集中Instantiate。
        /// 每帧最多2个，且单批累计超过3毫秒就主动让帧。浏览器还在过场中准备首次交火会用的着色程序。
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
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
                    if (i == 24 && instance is BattleEffectView feedback)
                    {
                        // 这两种程序在第一波交火就要用，各等一帧再准备，保持过场可刷新。
                        await WarmBrowserVariantAsync(feedback.BrowserRangedMaterial);
                        await WarmBrowserVariantAsync(feedback.BrowserMeleeMaterial);
                        await WarmBrowserVariantAsync(feedback.BrowserDamageTextMaterial);
                        // 使用正在借用的同一字体。分字号和少量字符准备，读条取消时随原有租约收尾。
                        await feedback.WarmBrowserTextGlyphsAsync(() => _disposed);
                    }
                    if (i == feedbackEnd && instance is ScoreSparkView spark)
                    {
                        await WarmBrowserVariantAsync(spark.BrowserSparkMaterial);
                    }
#endif
                    if (instance is BossSkillFxView skill)
                    {
                        // 先登记租约再await：取消/异常会由finally成对归还，不能在await后才记账。
                        await skill.WarmSlotsAsync(() => _disposed);
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
                        if (i == regularEnd)
                        {
                            // 炸弹和 Boss 共用同一份程序。从首个已登记租约取材质，本次交接只准备一次。
                            await WarmBrowserVariantAsync(skill.BrowserSkillMaterial);
                        }
#endif
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
#if UNITY_WEBGL && !UNITY_EDITOR && !UNITY_WEIXINMINIGAME && !UNITY_BYTEDANCE_MINIGAME && !UNITY_BYTEDANCEMINIGAME
        /// <summary>实际棋盘就绪后准备本章地面裂隙和帮手短效。只借一件帮手攻击池实例，结束或取消都归还。</summary>
        public async RevTask WarmBrowserBoardEffectsAsync(BoardView board)
        {
            if (board == null)
            {
                return;
            }

            // 棋盘就绪已包含帮手材质及格面。取本章真正持有的引用，其他章节的弹幕不提前加载。
            await WarmBrowserVariantAsync(board.BrowserHazardMaterial);
            await WarmBrowserVariantAsync(board.BrowserHelperProjectileMaterial);
            await WarmBrowserVariantAsync(board.BrowserHelperBeamMaterial);
            if (_disposed)
            {
                throw new OperationCanceledException("帮手预热已取消");
            }

            HelperAttackView helper = await GetAsync<HelperAttackView>();
            if (helper == null)
            {
                return;
            }

            try
            {
                // 飞行符文和激光落点各有自己的材质。原对象保持池借用状态，整个预热过程不播放攻击。
                await WarmBrowserVariantAsync(helper.BrowserRuneMaterial);
                await WarmBrowserVariantAsync(helper.BrowserImpactMaterial);
            }
            finally
            {
                if (helper != null)
                {
                    RevPool.Return(helper, 0);
                }
            }
        }

        /// <summary>在过场里准备实际材质使用的普通 Pass。让读条先绘制一次，取消时由原预热仍持有的实例清单收尾。</summary>
        private async RevTask WarmBrowserVariantAsync(Material material)
        {
            await RevTask.Yield();
            if (_disposed)
            {
                throw new OperationCanceledException("着色程序预热已取消");
            }

            Shader shader = material == null ? null : material.shader;
            if (shader == null || !shader.isSupported)
            {
                return;
            }

            // 装入 AB 和创建 Renderer 都只准备了资源。这里让 WebGL 在真正交火前完成驱动链接。
            // 只用现有材质的关键字，预热普通 Pass，保留原 Shader、材质和全部技能画面。
            var variants = new ShaderVariantCollection();
            try
            {
                var variant = new ShaderVariantCollection.ShaderVariant(shader, PassType.Normal, material.shaderKeywords);
                if (variants.Add(variant))
                {
                    variants.WarmUp();
                }
            }
            catch (ArgumentException error)
            {
                RevLog.Warn("着色程序预热跳过：" + error.Message, "RevGame");
            }
            finally
            {
                UnityEngine.Object.Destroy(variants);
            }
        }
#endif
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
