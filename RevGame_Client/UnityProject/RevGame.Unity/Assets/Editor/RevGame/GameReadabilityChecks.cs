// ============================================================
// GameReadabilityChecks.cs —— 在真实Play中注入密集反馈，检查全场预算、合并、遮挡和阶段清理。
// 人工检查很难稳定触发同帧上百次命中；本夹具只广播表现快照，不伪造或修改模型伤害。
// ============================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using RevGame.Core;
using RevGame.Flow;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.Editor
{
    public static class GameReadabilityChecks
    {
        public static string LastResult { get; private set; } = "未运行";
        private static int _checks;
        // 当前检查步骤，仅作诊断。不是模型阶段，测试工具不能通过改这个字符串强行使业务通过。
        public static string Stage { get; private set; } = "未开始";
        // 只读结果/阶段/已完成断言数。断言数随渲染帧数可能略变，不代表这么多个独立玩法场景。
        public static string State()
        {
            return JsonUtility.ToJson(new Status { result = LastResult, stage = Stage, checks = _checks });
        }
        [Serializable]
        private sealed class Status
        {
            public string result, stage; public int checks;
        }
        // 阶段记录只是诊断摘要，不在性能采样窗口运行，不能把生成状态文本算成玩法成本。
        // 阶段文本在性能采样窗口外写忽略Temp目录，帮助定位超时；不是每帧生产日志。
        private static void Mark(string stage)
        {
            Stage = stage;
            System.IO.File.WriteAllText("Temp/ReadabilityVerification/Stage.txt", stage);
        }
        /// <summary>必须在Play运行；finally返回大厅，让测试冻结的动画与时钟随原战斗域一起回收。</summary>
        public static async void Begin()
        {
            if (!Application.isPlaying || LastResult == "运行中")
            {
                return;
            }
            LastResult = "运行中";
            _checks = 0;
            string outcome = "失败：验收未完成";
            System.IO.Directory.CreateDirectory("Temp/ReadabilityVerification");
            GameFlow flow = RevServices.GetRequired<GameFlow>();
            IDisposable scorePreview = null;
            try
            {
                var service = (BattleService)RevServices.GetRequired<IBattleService>();

                // 隔离启动本身拥有积分预览租约；这里仅在尚未隔离时取得自己的租约，finally只还自己的。
                // 隔离启动可能已有ScorePreview租约，此处只在没有保护时取得自己的。
                // finally只Dispose本方法创建的租约，不能把启动流程的隔离开关提前解除。
                if (!service.IsScorePreviewActive)
                {
                    scorePreview = service.BeginScorePreview();
                }
                Mark("回首页");
                await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                Mark("进入战斗");
                Require(flow.OpenLevels() && flow.EnterBattle(1), "进入真实战斗域");
                await GameSceneTransitionChecks.WaitForFlowAsync(flow);
                float storyDeadline = Time.realtimeSinceStartup + 45f;
                while (flow.IsGameplayBlocked)
                {
                    if (Time.realtimeSinceStartup > storyDeadline)
                    {
                        throw new TimeoutException("反馈检查等待剧情/过场超时");
                    }
                    if (flow.IsStoryPlaying && !flow.IsTransitioning)
                    {
                        flow.AdvanceStory();
                    }
                    await RevTask.Yield();
                }
                await RevTask.Delay(650);
                BattleService battle = (BattleService)RevServices.GetRequired<IBattleService>();
                Mark("建立反馈场景");
                Require(battle.LoadDevelopmentScenario("events"), "载入真实事件场景");
                VerifyHud(battle);
                // 暂停模型自动小步，仅由本夹具发出确定输入，避免随机攻击混入合计数字的断言。
                RevTimer.CancelAllOf(RevLogic.Get<BattleLogic>());
                battle.Tick(3f);
                Require(battle.StartBattle(), "正常经过开战入口");
                VerifyHud(battle);
                await RevTask.Delay(100);
                BoardView board = UnityEngine.Object.FindObjectOfType<BoardView>();
                Pieces.Piece player = battle.PlayerPieces[0];
                BoardPos old = player.Pos;
                board.OnCellClicked(old);
                // 敌阵由固定种子生成，不能把某个写死坐标当成永远空闲；按真实完整占格校验寻找目标。
                BoardPos empty = new BoardPos(-1, -1);
                for (int y = 0; y < 9 && !empty.IsInside(9); y++)
                {
                    for (int x = 0; x < 9 && !empty.IsInside(9); x++)
                    {
                        var candidate = new BoardPos(x, y);

                        if (candidate != old && battle.Board.GetPiece(candidate) == null && battle.CanPlaySelectedAt(candidate))
                        {
                            empty = candidate;
                        }
                    }
                }
                Require(battle.CanPlaySelectedAt(empty), "实际棋盘点击选中并高亮战斗换位");
                board.OnCellClicked(empty);
                Require(player.Pos == empty && battle.Board.GetPiece(old) == null, "实际点击入口搬移模型与旧占格");

                Pieces.Piece target = battle.EnemyPieces[0];
                int hp = target.Hp;
                // AB冷启动需要磁盘读取，60ms断言验证的是齐射调度，而不是磁盘速度。
                // 先通过真实对象池预热一个对象；冷加载取消/回收由资源与跨回合测试另外覆盖。
                var warm = new RevTaskCompletionSource<BattleEffectView>();
                Mark("预热反馈对象");
                RevPool.GetAsync<BattleEffectView>(effect => warm.SetResult(effect));
                BattleEffectView warmed = await warm.Task;
                Require(warmed != null, "实际AB弹道预制体预热成功");
                Mark("注入同目标合并");
                RevPool.Return(warmed, 0);
                for (int i = 0; i < 120; i++)
                {
                    RevEvent.DispatchEvent<PieceAttackInfo>(Events.PieceAttacked, new PieceAttackInfo(player.Id, player.Def.Kind, player.Pos, player.Size, player.Side));
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(target.Id, target.Pos, target.Size, target.Side, 3, true, player.Pos, player.Id, false, PieceKind.Archer, player.Size, true));
                }
                Require(board.PendingDamageTextCount == 1, "同目标120次伤害仅保留一条待播数字");
                IDictionary pending = GetField<IDictionary>(board, "_pendingDamage");
                int sum = 0;
                foreach (object item in pending.Values)
                {
                    sum += GetField<int>(item, "Damage");
                }
                Require(sum == 360 && target.Hp == hp, "合计显示360，表现层不再额外扣血");
                Require(board.PendingProjectileCount > 1 && board.PendingProjectileCount <= 70, "同帧齐射保留多发真实弹道，同时限制待播长度");
                await RevTask.Delay(60);
                Require(board.ActiveProjectileCount > 1 && board.ActiveProjectileCount <= BoardView.MaxProjectiles && board.ActiveFloatingTextCount <= 3, "同帧攻击已分批形成多发齐射，仍受弹道与口号预算限制");
                int checkedProjectiles = 0;
                bool projectileSizeValid = true;
                bool projectileLayersValid = true;
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                {
                    SpriteRenderer sprite = GetField<SpriteRenderer>(effect, "spriteRenderer");

                    if (sprite.enabled && sprite.sprite != null)
                    {
                        // bounds是旋转后的轴对齐包围盒；既检查确实放大，也防止又变成横贯棋盘的线条。
                        float size = sprite.bounds.size.magnitude;
                        checkedProjectiles++;
                        projectileSizeValid &= sprite.drawMode == SpriteDrawMode.Simple && size > 0.65f && size < 1.3f;
                        projectileLayersValid &= GetField<SpriteRenderer>(effect, "projectileOutline").enabled && GetField<SpriteRenderer>(effect, "projectileTrail").enabled;
                    }
                }
                // 按整组检查，不能因本机这一帧恰好显示几发不同，导致断言数量随帧率改变。
                Require(checkedProjectiles > 1 && projectileSizeValid, "多发弹头已明显放大，全部尺寸仍受控");
                Require(checkedProjectiles > 1 && projectileLayersValid, "所有活跃弹头均带可见轮廓与短拖尾");
                bool shown = false;
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                {
                    string displayed = GameTextConfig.DisplayDamage(360).ToString();

                    if (effect.IsFloatingText && !effect.IsAttackText && GetField<TextMesh>(effect, "damageText").text.Contains(displayed))
                    {
                        shown = true;
                    }
                }
                Require(shown, "60ms内实际显示同帧合计360，不再等0.22秒");

                for (int i = 0; i < 100; i++)
                {
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(10000 + i, new BoardPos(4, 4), 1, GameSide.Enemy, i + 1, true, new BoardPos(0, 0), 5000 + i, false, PieceKind.Archer, 1, true));
                }
                Require(board.PendingDamageTextCount <= 81, "不同目标风暴仍保持有界待播队列");
                Mark("密集数字与避让");
                int peak = 0;
                int projectilePeak = 0;
                for (int frame = 0; frame < 12; frame++)
                {
                    await RevTask.Delay(45);
                    peak = Math.Max(peak, board.ActiveFloatingTextCount);
                    projectilePeak = Math.Max(projectilePeak, board.ActiveProjectileCount);
                    Require(board.ActiveFloatingTextCount <= BoardView.MaxFloatingTexts && board.ActiveProjectileCount <= BoardView.MaxProjectiles, "动画播放中持续满足全场数量上限");
                }
                Require(peak >= 15 && peak <= 20 && projectilePeak > 6, "密集输入实际显示15~20条文字，齐射仍保留硬上限");
                List<BattleEffectView> texts = new List<BattleEffectView>();
                foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                {
                    if (effect.IsFloatingText)
                    {
                        texts.Add(effect);
                    }
                }
                texts.Sort((a, b) => a.TextOrder.CompareTo(b.TextOrder));
                Require(texts.Count >= 2, "存在可检查遮挡的两条活跃文字");
                BattleEffectView lower = texts[0];
                BattleEffectView upper = texts[texts.Count - 1];
                foreach (BattleEffectView effect in texts)
                {
                    GetField<Tween>(effect, "_animation").Pause();
                }
                // 先检查自然避让后的实际包围盒，再人为制造不可避让的相交，验证半透明兜底真正落屏。
                int overlaps = 0;
                for (int i = 0; i < texts.Count; i++)
                {
                    for (int j = i + 1; j < texts.Count; j++)
                    {
                        if (texts[i].TextScreenRect.Overlaps(texts[j].TextScreenRect))
                        {
                            overlaps++;
                        }
                    }
                }
                Require(overlaps < texts.Count, "同一命中点的数字已分散到不同可见位置");
                System.IO.Directory.CreateDirectory("Temp/ReadabilityVerification");
                ScreenCapture.CaptureScreenshot("Temp/ReadabilityVerification/dense-feedback.png");
                upper.transform.position = lower.transform.position;
                upper.transform.localScale = lower.transform.localScale;
                await RevTask.Delay(80);
                Require(GetField<TextMesh>(lower, "damageText").color.a <= 0.351f, "无法避让时下层文字实际半透明");

                // 同一真实事件入口注入固定长轨迹；只控制表现输入，绝不调用TakeDamage。
                // 不暂停弹道Tween以虚增峰值；连续供给才能证明调度率真的支持35条活跃飞行。
                await RevTask.Delay(1200);
                var kinds = new[] { PieceKind.Archer, PieceKind.Crossbow, PieceKind.MageWater, PieceKind.MageFire, PieceKind.Priest };
                var seen = new HashSet<PieceKind>();
                var materialByKind = new Dictionary<PieceKind, Material>();
                int densePeak = 0;
                float worstDispatchMs = 0f;
                var timer = new System.Diagnostics.Stopwatch();
                for (int frame = 0; frame < 80; frame++)
                {
                    timer.Restart();
                    for (int shot = 0; shot < 10; shot++)
                    {
                        int kind = shot % 5;
                        RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(30000 + kind, new BoardPos(8, kind + 2), 1,
                            GameSide.Enemy, 1, true, new BoardPos(0, kind + 2), 8000 + shot, false, kinds[kind], 1, true));
                    }
                    timer.Stop();
                    worstDispatchMs = Mathf.Max(worstDispatchMs, (float)timer.Elapsed.TotalMilliseconds);
                    await RevTask.Delay(25);
                    densePeak = Math.Max(densePeak, board.ActiveProjectileCount);
                    Require(board.ActiveProjectileCount <= 35 && board.PendingProjectileCount <= 70 && board.ActiveFloatingTextCount <= 20, "持续齐射的活动、待播、文字三项硬预算");

                    if (frame < 40)
                    {
                        continue;
                    }
                    foreach (BattleEffectView effect in UnityEngine.Object.FindObjectsOfType<BattleEffectView>())
                    {
                        SpriteRenderer body = GetField<SpriteRenderer>(effect, "spriteRenderer");

                        if (!body.enabled)
                        {
                            continue;
                        }
                        seen.Add(effect.ProjectileKind);
                        Require(body.sprite != null && body.sprite.name.IndexOf("UI_", StringComparison.Ordinal) < 0, "弹头使用下载的职业原画");
                        Require(body.sharedMaterial.shader.name == "RevGame/Combat/RangedProjectile" && body.sharedMaterial.shader.isSupported, "职业Shader已真正绑定并支持当前GPU");

                        if (RevResBootstrap.UseABInEditor)
                        {
                            Require(body.sprite.packed, "真实AB中的职业图片已绑定图集");
                        }
                        Vector4 uv = UnityEngine.Sprites.DataUtility.GetOuterUV(body.sprite);
                        Require(body.sharedMaterial.GetVector("_SpriteUV") == uv, "共享职业材质UV与真实图集绑定相同");
                        // 参数块存在与否不是正确性标准：必须证明本体、轮廓与拖尾真正绑定当前图集页。
                        // 清空SpriteRenderer参数块曾使Shader回退白图；只查sprite.packed会漏掉这个回归。
                        var binding = new MaterialPropertyBlock();
                        foreach (SpriteRenderer layer in new[] { body, GetField<SpriteRenderer>(effect, "projectileOutline"), GetField<SpriteRenderer>(effect, "projectileTrail") })
                        {
                            binding.Clear();
                            layer.GetPropertyBlock(binding);
                            Require(binding.GetTexture("_MainTex") == layer.sprite.texture, "弹道三层参数块保留真实图集纹理，禁止白图回退");
                            Require(binding.GetVector("_SpriteUV") == uv, "弹道三层UV与图集页绑定一致");
                        }

                        if (materialByKind.TryGetValue(effect.ProjectileKind, out Material shared))
                        {
                            Require(shared == body.sharedMaterial, "同职业弹道共用材质，没有隐式克隆");
                        }
                        else
                        {
                            materialByKind.Add(effect.ProjectileKind, body.sharedMaterial);
                        }
                    }
                    if (frame % 2 == 0)
                    {
                        ScreenCapture.CaptureScreenshot("Temp/ReadabilityVerification/Frame-" + ((frame - 40) / 2).ToString("00") + ".png");
                    }
                }
                Require(densePeak >= 30 && densePeak <= 35, "持续实飞峰值达到30至35，不能只提高常量；实际=" + densePeak);
                Require(seen.Count == 5 && materialByKind.Count == 5, "五职业箭矢、弩矢、水球、熔岩、圣光均实际出现");
                await RevTask.Delay(800);
                Require(board.ActiveProjectileCount == 0 && board.PendingProjectileCount == 0, "停止输入后弹道寿命和队列完整回收");
                int beforeMelee = board.PendingProjectileCount;
                RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(target.Id, target.Pos, 1, target.Side, 2, true, player.Pos, player.Id));
                RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(target.Id, target.Pos, 1, target.Side, 2, true, player.Pos, player.Id, true, PieceKind.MageFire, 1, true));
                Require(board.PendingProjectileCount == beforeMelee, "近战和自带技能特效不再发射通用占位弹道");
                System.IO.File.WriteAllText("Temp/ReadabilityVerification/Metrics.txt", "初始密集输入文字峰值=" + peak + "，弹道峰值=" + projectilePeak +
                    "；持续五职业齐射峰值=" + densePeak + "；每批10次事件分发最大毫秒=" + worstDispatchMs.ToString("F3") +
                    "；环境=" + SystemInfo.graphicsDeviceName + "/" + SystemInfo.graphicsDeviceType + "/" + Screen.width + "x" + Screen.height +
                    "。事件分发计时不含GPU，截图与Editor开销不代表手机帧率；未宣称零GC。");

                Require(battle.TriggerBattleEvent(Data.BattleEventKind.Reinforcement), "触发真实天降援兵事件");
                await RevTask.Delay(80);
                BattleEventNoticeView notice = UnityEngine.Object.FindObjectOfType<BattleEventNoticeView>();
                Require(notice != null, "真实事件面板已显示");
                Text title = GetField<Text>(notice, "titleText");
                Text detail = GetField<Text>(notice, "detailText");
                Require(title.alignment == TextAnchor.MiddleCenter && detail.alignment == TextAnchor.MiddleCenter, "事件标题与说明真正居中对齐");
                Require(Mathf.Abs(title.rectTransform.anchoredPosition.x) < 0.01f && Mathf.Abs(detail.rectTransform.anchoredPosition.x) < 0.01f, "标题说明的文字框也与底板几何中心对齐");
                // 故意在切轮前重新塞满两个队列，避免“队列早已自然播完”的空检查掩盖清理缺陷。
                for (int i = 0; i < 40; i++)
                {
                    RevEvent.DispatchEvent<BattleHitInfo>(Events.BattleHit, new BattleHitInfo(20000 + i, new BoardPos(4, 4), 1, GameSide.Enemy, 1, true, player.Pos, 6000 + i, false, PieceKind.Archer, player.Size, true));
                }
                Require(board.PendingProjectileCount > 0 && board.PendingDamageTextCount > 0, "切轮前两种显示队列确实都有待播内容");
                Require(battle.StartRound(1, 2), "密集动画尚未结束时切换回合");
                Mark("切回合清理");
                Require(board.ActiveEffectCount == 0 && board.PendingDamageTextCount == 0 && board.ActiveFloatingTextCount == 0 && board.ActiveProjectileCount == 0 && board.PendingProjectileCount == 0, "切阶段清空冻结文字、弹道、两个待播队列和预算");
                await RevTask.Delay(450);
                Require(board.ActiveEffectCount == 0, "旧异步请求没有在新准备期补播");
                VerifyHud(battle);
                Require(battle.LoadDevelopmentScenario("boss"), "HUD缓存验收建立真实主将回合");
                await RevTask.Delay(150);
                VerifyHud(battle);
                Pieces.Piece king = null;
                foreach (var enemy in battle.EnemyPieces)
                {
                    if (enemy.IsKing)
                    {
                        king = enemy;
                        break;
                    }
                }
                Require(king != null, "主将HUD存在真实数据源");
                king.TakeDamage(3);
                VerifyHud(battle);
                outcome = "通过 " + _checks + " 条Play断言：120次合并、100目标控频、职业原画/即时伤害/35条连续齐射、避让/透明、居中、战斗换位与阶段清理；峰值飘字=" + peak + "，弹道=" + projectilePeak;
            }
            catch (Exception error)
            {
                outcome = "失败：" + error.Message;
                RevLog.Exception(error, "战斗可读性验收失败", "RevGame");
            }
            finally
            {
                Mark("回首页收尾");
                try
                {
                    await GameSceneTransitionChecks.ReturnHomeAsync(flow);
                }
                finally { scorePreview?.Dispose(); }
                LastResult = outcome;
                Stage = "完成";
                System.IO.File.WriteAllText("Temp/ReadabilityVerification/Result.txt", LastResult);
            }
        }
        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(target);
        }
        /// <summary>读取真实面板，检查静态缓存命中与阶段/回合/主将掉血后的失效；不伪造UI文本。</summary>
        // 对真实面板Refresh后核对真实服务阶段、章回与HP；再刷相同输入检查缓存没有造成文案消失。
        // 直接改UI成预期文本只会制造假通过，故这里只读已经落屏结果。
        private static void VerifyHud(BattleService battle)
        {
            var camera = Camera.main;

            if (camera != null)
            {
                // 投影实现属于运行时程序集且保持internal；开发验收只反射固定类型，不扩大生产API。
                Type projectionType = typeof(BattleEffectView).Assembly.GetType("RevGame.View.FeedbackProjection", true);
                object projection = Activator.CreateInstance(projectionType, true);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                projectionType.GetMethod("Capture", flags).Invoke(projection, new object[] { camera });
                Vector3 point = new Vector3(1.25f, -.75f, 0f);
                Vector3 screen = (Vector3)projectionType.GetMethod("WorldToScreen", flags).Invoke(projection, new object[] { point });
                Require(Vector3.Distance(camera.WorldToScreenPoint(point), screen) < .01f, "共享投影与Unity原生屏幕坐标一致");
            }
            var hud = UnityEngine.Object.FindObjectOfType<RevGame.UI.BattleHudPanel>();
            Require(hud != null, "真实HUD已打开");
            hud.RefreshView();
            string phase = battle.Phase == GamePhase.Battle ? "战斗" : battle.Phase == GamePhase.Settle ? "结算" : "准备";
            Require(GetField<Text>(hud, "_phaseLabel").text == phase, "HUD阶段缓存随真实阶段更新");
            string round = GetField<Text>(hud, "_roundText").text;
            Require(round.Contains("第 " + battle.Chapter + " / 6 章") && round.Contains("第 " + battle.Round + " 回合"), "HUD章节回合缓存实时一致");
            string counts = GetField<Text>(hud, "_countText").text;
            hud.RefreshView();
            Require(GetField<Text>(hud, "_countText").text == counts, "相同输入重复刷新保持文字");
            foreach (var enemy in battle.EnemyPieces)
            {
                if (!enemy.IsKing)
                {
                    continue;
                }
                Text text = GetField<Text>(hud, "_bossText");
                Require(text.gameObject.activeSelf && text.text.Contains(enemy.Hp + " / " + enemy.MaxHp), "主将血量变化使HUD缓存失效");
            }
        }
        private static void Require(bool success, string reason)
        {
            if (!success)
            {
                throw new InvalidOperationException(reason);
            }
            _checks++;
        }
    }
}
