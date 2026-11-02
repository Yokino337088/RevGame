// ============================================================
// EndlessArena.CombatFx.cs —— 无尽战斗反馈，全部复用六章的资源（新手先读这段）
//
// 【职责】
// EndlessService 只算伤害，并把“谁打了谁、打掉多少血、谁死了”写进三个环形回执缓冲：
//   Impacts（攻击/范围）、DamageReceipts（伤害数字）、DeathReceipts（死亡）。
// 这个文件每帧读取新回执，用六章同一套池对象播出来：
//   - 弹道 / 近战斩击 / 火水圣光范围 / 伤害数字 / 攻击口号 → BattleEffectView（Effects/Combat/BattleEffect）；
//   - 出生燃烧 / 升星雷暴 / 死亡溶解 → PieceView 自带的 ArcanePiece Shader 效果；
//   - 文案、字号、颜色 → BattleText 表（Excel），和六章完全一致。
// 没有任何自制 Shader、贴图或材质。
//
// 【为什么要预算】
// 无尽一屏可能有上百只敌人，每次命中都播特效会把手机拖垮。这里沿用六章 BoardView 的上限：
// 弹道 35、近战 16、文字 20（口号最多 7）、每帧最多新出 8 个数字。超出时只是少画一个，
// 伤害早已在服务里结算完毕，少画不会少扣血，也不会重复扣血。
//
// 【尺寸为什么能对上】
// BattleEffectView 按六章格宽 Consts.CellSize（1.15）设计尺寸，无尽棋盘每格 1。
// 所以特效统一挂在一个缩放为 1/1.15 的子节点 _fxRoot 下：位置仍写世界坐标，尺寸自动缩到无尽格子大小。
//
// 【生命周期】
// 所有异步取池都带 _fxGeneration 版本号；退出无尽时版本号 +1，迟到的回调发现版本不对就把对象还回池。
// ============================================================
using System.Collections.Generic;
using DG.Tweening;
using RevGame.Core;
using RevGame.Data;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessArena
    {
        // 以下是渲染预算，不是战斗数值，所以不进表。无尽要“满屏弹幕”，在六章（弹道35/近战16/文字20）基础上适度放宽；
        // 仍然有上限并且全部走对象池，超出只少画、不少算伤害。改动后需重新测帧时间（见 EndlessArena.Metrics）。
        private const int MaxProjectiles = 48;
        private const int MaxMeleeEffects = 24;
        /// <summary>同时播放的范围特效（火环/圣光/水波/重剑）上限。</summary>
        private const int MaxPatterns = 16;
        private const int MaxFloatingTexts = 24;
        private const int MaxAttackTexts = 7;
        /// <summary>每帧最多新出几个伤害数字；齐射时分几帧出完，不会一帧爆几十个字。</summary>
        private const int MaxTextsPerFrame = 8;
        /// <summary>待显示数字的上限（81 = 盘内格数，外加外围同目标会合并），满了就丢掉新的，不无限排队。</summary>
        private const int MaxPendingTexts = 81;
        /// <summary>同时溶解的尸体上限（含盘内棋子和外围杂兵）。</summary>
        private const int MaxCorpses = 24;
        /// <summary>同时播放出生燃烧的外围杂兵上限。</summary>
        private const int MaxHordeBirths = 16;
        /// <summary>杂兵出生燃烧跟随时长，略长于 PieceView 的 1.35 秒燃烧动画，让火完整烧完再换回批量网格。</summary>
        private const float HordeBirthSeconds = 1.4f;
        /// <summary>同一棋子两次口号的最短间隔、全场口号的最短间隔、待显示数字的过期时间（秒，纯表现节奏）。</summary>
        private const float AttackTextCooldown = .65f, AttackTextInterval = .12f, PendingTextLife = 1.2f;

        /// <summary>BattleEffectView 的五种用法；下标用于 _pendingFx / _activeFx。</summary>
        private enum FxKind
        {
            Projectile, Melee, Pattern, Damage, Attack
        }
        /// <summary>一次特效请求的值类型快照；异步加载期间不持有任何棋子对象。</summary>
        private struct FxRequest
        {
            public FxKind Kind;
            public Vector3 From, To;
            public PieceKind Piece;
            public GameSide Side;
            public int Radius, Damage;
            public BattleDamageKind DamageKind;
            public BattleText Text;
        }
        /// <summary>同目标同类伤害在同一小段时间内合并成一个数字（与六章一致），只存快照值。</summary>
        private struct PendingText
        {
            public long Key;
            public int Damage;
            public Vector3 Position;
            public GameSide Side;
            public BattleDamageKind Kind;
            public float CreatedAt;
        }

        /// <summary>特效挂点，缩放 1/1.15，见文件头说明。随竞技场一起复用，只创建一次。</summary>
        private Transform _fxRoot;
        /// <summary>异步回调版本号：退出时 +1，旧回调只能还池。</summary>
        private int _fxGeneration;
        /// <summary>本次租用是否已经对齐过回执序号；回池时清零，下次进入重新对齐。</summary>
        private bool _fxBegun;
        /// <summary>三种回执各自读到的序号。</summary>
        private int _impactRead, _damageRead, _deathRead;
        /// <summary>正在播放的特效，按用途分组计数；退出时全部还池。</summary>
        private readonly HashSet<BattleEffectView>[] _activeFx =
        {
            new HashSet<BattleEffectView>(), new HashSet<BattleEffectView>(), new HashSet<BattleEffectView>(),
            new HashSet<BattleEffectView>(), new HashSet<BattleEffectView>()
        };
        /// <summary>已申请但池对象还没到手的数量；异步未完成也要占预算，否则加载完的那帧会突然冒出一大批。</summary>
        private readonly int[] _pendingFx = new int[5];
        /// <summary>范围特效需要每帧推进关键帧（六章由 BoardView 推进，这里由竞技场推进）。</summary>
        private readonly List<BattleEffectView> _patterns = new List<BattleEffectView>(MaxPatterns);
        private readonly Dictionary<long, PendingText> _pendingTexts = new Dictionary<long, PendingText>(MaxPendingTexts);
        private readonly List<long> _expiredTexts = new List<long>(MaxPendingTexts);
        /// <summary>本帧选出的最老 8 条数字，固定数组，不每帧 new。</summary>
        private readonly PendingText[] _textBatch = new PendingText[MaxTextsPerFrame];
        /// <summary>每个出手棋子上次喊口号的时间，防同一棋子刷屏。</summary>
        private readonly Dictionary<int, float> _attackTextAt = new Dictionary<int, float>(81);
        /// <summary>九种棋子的口号配置，首次用到时从 BattleText 表取并缓存，避免每次拼字符串查表。</summary>
        private readonly BattleText[] _attackTexts = new BattleText[9];
        private readonly bool[] _attackTextLoaded = new bool[9];
        private float _nextAttackTextAt;
        /// <summary>文字前后顺序，越新越靠前（与六章 SetTextOrder 一致）。</summary>
        private int _textOrder;
        /// <summary>本帧死亡的盘内棋子编号；SyncPieces 看到它们消失时播溶解，而不是直接回池。</summary>
        private readonly HashSet<int> _dyingIds = new HashSet<int>();
        /// <summary>正在溶解的棋子视图（盘内死亡 + 外围杂兵尸体）。</summary>
        private readonly HashSet<PieceView> _dissolving = new HashSet<PieceView>();
        private int _pendingCorpses;
        /// <summary>尸体视图用的临时编号（负数，不会和真实棋子编号撞车），每次递减，保证复用的视图一定重新绑定外观。</summary>
        private int _corpseId;
        /// <summary>每个外围槽位上次看到的敌人编号；编号变了说明这个槽刷出了新敌人。</summary>
        private int[] _hordeSeen = new int[0];
        /// <summary>正在播出生燃烧的杂兵视图及开始时间，下标与 Run.horde 一致；有视图时批量网格里对应的方块隐藏。</summary>
        private PieceView[] _hordeBirth = new PieceView[0];
        private float[] _hordeBirthAt = new float[0];
        /// <summary>出生视图“由大渐小”的缩放补间，下标同上；视图回池前 Kill(false)，不留悬空回调。</summary>
        private Tweener[] _hordeBirthTween = new Tweener[0];
        /// <summary>批量网格里普通杂兵方块的半宽（格），必须和 UpdateHorde 里的 .38 一致（纯表现尺寸）。</summary>
        private const float HordeQuadHalf = .38f;
        private int _hordeBirthCount, _pendingHordeBirths;
        /// <summary>第一帧只记录现有敌人、不补播出生（读档或开局时它们早就在场上了）。</summary>
        private bool _hordeSynced;
        /// <summary>盘内棋子上次的星级，星级变高就播六章同款升星雷暴。</summary>
        private readonly Dictionary<int, StarTier> _stars = new Dictionary<int, StarTier>(81);
        private System.Action<BattleEffectView> _fxFinished, _arrangeText;
        private System.Action<PieceView> _corpseFinished;

        /// <summary>进入无尽时调用：建挂点、清空上一局的读数。</summary>
        private void BeginCombatFx(EndlessService service)
        {
            if (_fxRoot == null)
            {
                var root = new GameObject("六章战斗反馈");
                _fxRoot = root.transform;
                _fxRoot.SetParent(transform, false);
                _fxRoot.localScale = Vector3.one / Consts.CellSize;
                _fxFinished = OnFxFinished;
                _arrangeText = ArrangeText;
                _corpseFinished = OnCorpseFinished;
            }
            _fxGeneration++;
            _fxBegun = true;
            BeginGuard(service);
            // 从当前序号开始读：读档恢复时不补播存档前的旧回执。
            _impactRead = service.ImpactSerial;
            _damageRead = service.DamageSerial;
            _deathRead = service.DeathSerial;
            _hordeSynced = false;
            _nextAttackTextAt = 0;
            _textOrder = 0;
        }

        /// <summary>每帧入口第一段：读死亡回执。必须在 SyncPieces 之前，否则棋子会先被当成“普通移除”收走。</summary>
        private void ReadDeaths(EndlessService service)
        {
            int latest = service.DeathSerial;
            if (latest < _deathRead)
            {
                // 服务重新开局会把序号清零；旧读数比它大时直接对齐，否则会一直“等”到序号追上来。
                _deathRead = latest;
            }
            int first = Mathf.Max(_deathRead + 1, latest - service.DeathReceipts.Length + 1);
            for (int serial = first; serial <= latest; serial++)
            {
                EndlessDeathReceipt death = service.DeathReceipts[serial % service.DeathReceipts.Length];
                if (death.serial != serial)
                {
                    continue;
                }
                if (!death.horde)
                {
                    _dyingIds.Add(death.id);
                }
                else
                {
                    HordeDied(death);
                }
            }
            _deathRead = latest;
        }

        /// <summary>每帧入口第二段：读攻击与伤害回执，推进杂兵出生，然后按预算放出特效。</summary>
        private void UpdateCombatFx(EndlessService service)
        {
            EndlessRun run = service.Run;
            UpdateHordeBirths(run);
            ReadImpacts(service);
            ReadDamage(service);
            FlushDamageTexts(run);
        }

        /// <summary>范围特效逐帧推进；放在 LateUpdate，保证本帧新出的特效也能立刻画出第一帧。</summary>
        private void LateUpdate()
        {
            if (!_active)
            {
                return;
            }
            float now = Time.time;
            // 倒序遍历：AdvancePattern 播完会回调 OnFxFinished 把自己从列表里删掉，倒序删除不会跳过元素。
            for (int i = _patterns.Count - 1; i >= 0; i--)
            {
                if (i < _patterns.Count)
                {
                    _patterns[i].AdvancePattern(now);
                }
            }
        }

        // ───────────────────────── 攻击与范围 ─────────────────────────

        private void ReadImpacts(EndlessService service)
        {
            int latest = service.ImpactSerial;
            if (latest < _impactRead)
            {
                // 服务重新开局会把序号清零；旧读数比它大时直接对齐，否则会一直“等”到序号追上来。
                _impactRead = latest;
            }
            int first = Mathf.Max(_impactRead + 1, latest - service.Impacts.Length + 1);
            for (int serial = first; serial <= latest; serial++)
            {
                EndlessImpact hit = service.Impacts[serial % service.Impacts.Length];
                if (hit.serial == serial)
                {
                    PlayImpact(service.Run, hit);
                }
            }
            _impactRead = latest;
        }

        /// <summary>
        /// 把一条攻击回执翻译成六章表现：
        ///   有出手者 → 出手动作 + 口号 + 远程弹道或近战斩击（按棋种）；
        ///   有半径   → 火环/圣光/水波/重剑范围特效（按路线或出手棋种）；
        ///   领主技能的范围已由 BossSkillFxView 播放（见 BossFx），这里跳过避免画两遍。
        /// </summary>
        private void PlayImpact(EndlessRun run, EndlessImpact hit)
        {
            if (hit.combo > 0)
            {
                PlayComboShout(run, hit);
                return;
            }
            if (hit.route == EndlessService.AuraRoute && hit.radius > 0)
            {
                FlashAura(hit.sourceId);
                return;
            }
            PlayImpactSound(hit);
            bool hostile = hit.route == 6;
            if (hostile && run.shake)
            {
                _shakeBoost = Mathf.Max(_shakeBoost, .04f);
            }
            Vector3 from = new Vector3(hit.from.x, hit.from.y, 0);
            Vector3 to = new Vector3(hit.to.x, hit.to.y, 0);
            if (hit.sourceId > 0)
            {
                if (_pieces.TryGetValue(hit.sourceId, out PieceView source))
                {
                    // 预算满了也要让出手的棋子动一下，否则玩家会以为它没在打（六章 OnAttack 同理）。
                    source.PlayAttackPulse();
                }
                TryAttackText(run, hit.sourceId, hit.sourceKind, from);
            }
            if (hit.route == EndlessService.ThrustRoute)
            {
                // 刺客突刺：画尖刺冲击（EndlessArena.Guard），不再走普通近战斩击。
                PushThrust(hit.from, hit.to);
                return;
            }
            if (hit.radius > 0)
            {
                PlayArea(hit, from, to, hostile);
                return;
            }
            if ((to - from).sqrMagnitude < .01f)
            {
                return;
            }
            PieceKind kind = hit.hasKind ? hit.sourceKind : RouteKind(hit.route);
            GameSide side = hostile ? GameSide.Player : GameSide.Enemy;
            if (HasProjectileArt(kind))
            {
                if (CanSpawn(FxKind.Projectile, run.lowEffects ? MaxProjectiles / 2 : MaxProjectiles))
                {
                    Spawn(new FxRequest { Kind = FxKind.Projectile, From = from, To = to, Piece = kind, Side = side });
                }
            }
            else if (CanSpawn(FxKind.Melee, MaxMeleeEffects))
            {
                Spawn(new FxRequest { Kind = FxKind.Melee, From = from, To = to, Piece = kind, Side = side });
            }
        }

        private void PlayArea(EndlessImpact hit, Vector3 from, Vector3 to, bool hostile)
        {
            if (hostile && hit.sourceId == 0)
            {
                // 外围敌人突破门点打棋心：画一发六章箭矢飞向棋心，玩家能看清伤害从哪来。
                // 起终点相同的是领主技能落点，已由 BossSkillFxView 播放，不再叠一层。
                if ((to - from).sqrMagnitude > .01f && CanSpawn(FxKind.Projectile, MaxProjectiles))
                {
                    Spawn(new FxRequest { Kind = FxKind.Projectile, From = from, To = to, Piece = PieceKind.Archer, Side = GameSide.Player });
                }
                return;
            }
            if (!CanSpawn(FxKind.Pattern, MaxPatterns))
            {
                return;
            }
            PieceKind kind = hit.hasKind ? hit.sourceKind : AreaKind(hit.route);
            // 六章范围特效按“格”为单位的整数半径画；无尽半径是小数，取最近的整数并限制在 0~4 格，只影响画面大小。
            int radius = Mathf.Clamp(Mathf.RoundToInt(hit.radius), 0, 4);
            Spawn(new FxRequest { Kind = FxKind.Pattern, From = from, To = to, Piece = kind, Radius = radius });
        }

        /// <summary>没有具体出手者时，按祝福路线挑一个六章棋种的弹道外观：0 弩、1 火、2 圣光、3/4 水、5 重剑、6 敌方箭。</summary>
        private static PieceKind RouteKind(int route)
        {
            switch (route)
            {
                case 0: return PieceKind.Crossbow;
                case 1: return PieceKind.MageFire;
                case 2: return PieceKind.Priest;
                case 3: case 4: return PieceKind.MageWater;
                case 5: return PieceKind.HeavySword;
                default: return PieceKind.Archer;
            }
        }
        /// <summary>范围特效的棋种：火路线用火环、圣光路线用圣光柱、水/雷用水波，其余用重剑震地。</summary>
        private static PieceKind AreaKind(int route)
        {
            switch (route)
            {
                case 1: return PieceKind.MageFire;
                case 2: return PieceKind.Priest;
                case 3: case 4: return PieceKind.MageWater;
                default: return PieceKind.HeavySword;
            }
        }
        /// <summary>
        /// 六章 BattleEffect 预制体只给五种远程棋（弓、弩、水法、火法、牧师）配了弹道原画，
        /// 其余棋种走近战斩击/盾击。这里按棋种枚举判断，和预制体配置保持一致。
        /// </summary>
        private static bool HasProjectileArt(PieceKind kind)
        {
            return kind == PieceKind.Archer || kind == PieceKind.Crossbow || kind == PieceKind.MageWater || kind == PieceKind.MageFire || kind == PieceKind.Priest;
        }

        /// <summary>命中音效：范围爆炸用重击、近战/剑气用刀切、远程命中用拳击；各自有最短间隔（见 EndlessArena.Feel）。</summary>
        private void PlayImpactSound(EndlessImpact hit)
        {
            if (hit.radius > 0)
            {
                PlayFeelSound(SoundBurst);
            }
            else if (hit.hasKind && !HasProjectileArt(hit.sourceKind))
            {
                PlayFeelSound(SoundSlash);
            }
            else
            {
                PlayFeelSound(SoundHit);
            }
        }

        /// <summary>攻击口号：同一棋子 0.65 秒内只喊一次、全场 0.12 秒一条、最多 7 条，和六章节奏一致。</summary>
        private void TryAttackText(EndlessRun run, int sourceId, PieceKind kind, Vector3 position)
        {
            float now = Time.time;
            if (run.lowEffects || now < _nextAttackTextAt || TextCount() >= MaxFloatingTexts || AttackTextCount() >= MaxAttackTexts)
            {
                return;
            }
            if (_attackTextAt.TryGetValue(sourceId, out float last) && now - last < AttackTextCooldown)
            {
                return;
            }
            if (_attackTextAt.Count > 256)
            {
                // 长局里阵亡棋子的记录会越积越多；超过上限整体清空，最多让某个棋子早喊一次。
                _attackTextAt.Clear();
            }
            int index = (int)kind;
            if (!_attackTextLoaded[index])
            {
                _attackTextLoaded[index] = true;
                _attackTexts[index] = GameTextConfig.Require("Attack_" + kind);
            }
            _attackTextAt[sourceId] = now;
            _nextAttackTextAt = now + AttackTextInterval;
            Spawn(new FxRequest { Kind = FxKind.Attack, From = position, Text = _attackTexts[index] });
        }

        // ───────────────────────── 伤害数字 ─────────────────────────

        /// <summary>把新伤害回执合并进待显示表：同目标同类伤害累加，位置取最近一次命中。</summary>
        private void ReadDamage(EndlessService service)
        {
            int latest = service.DamageSerial;
            if (latest < _damageRead)
            {
                // 服务重新开局会把序号清零；旧读数比它大时直接对齐，否则会一直“等”到序号追上来。
                _damageRead = latest;
            }
            int first = Mathf.Max(_damageRead + 1, latest - service.DamageReceipts.Length + 1);
            float now = Time.time;
            for (int serial = first; serial <= latest; serial++)
            {
                EndlessDamageReceipt receipt = service.DamageReceipts[serial % service.DamageReceipts.Length];
                if (receipt.serial != serial)
                {
                    continue;
                }
                long key = ((long)receipt.target << 8) | (byte)receipt.kind;
                if (!_pendingTexts.TryGetValue(key, out PendingText pending))
                {
                    if (_pendingTexts.Count >= MaxPendingTexts)
                    {
                        continue;
                    }
                    pending = new PendingText { Key = key, Side = receipt.side, Kind = receipt.kind, CreatedAt = now };
                }
                pending.Damage = (int)System.Math.Min(int.MaxValue, (long)pending.Damage + receipt.damage);
                pending.Position = new Vector3(receipt.position.x, receipt.position.y, 0);
                _pendingTexts[key] = pending;
            }
            _damageRead = latest;
        }

        /// <summary>
        /// 每帧挑最老的至多 8 条显示；文字满 20 条时先让最旧的一条（优先口号）提前谢幕。
        /// 和六章 FlushDamageTexts 同一思路：插入排序选出最老 8 条，不排序整张表、不分配临时列表。
        /// </summary>
        private void FlushDamageTexts(EndlessRun run)
        {
            float now = Time.time;
            int count = 0;
            _expiredTexts.Clear();
            foreach (PendingText item in _pendingTexts.Values)
            {
                if (now - item.CreatedAt > PendingTextLife)
                {
                    _expiredTexts.Add(item.Key);
                    continue;
                }
                int index = count;
                while (index > 0 && item.CreatedAt < _textBatch[index - 1].CreatedAt)
                {
                    index--;
                }
                if (index >= _textBatch.Length)
                {
                    continue;
                }
                int end = System.Math.Min(count, _textBatch.Length - 1);
                for (int i = end; i > index; i--)
                {
                    _textBatch[i] = _textBatch[i - 1];
                }
                _textBatch[index] = item;
                count = System.Math.Min(count + 1, _textBatch.Length);
            }
            for (int i = 0; i < _expiredTexts.Count; i++)
            {
                _pendingTexts.Remove(_expiredTexts[i]);
            }
            int perFrame = run.lowEffects ? MaxTextsPerFrame / 2 : MaxTextsPerFrame;
            for (int i = 0; i < count && i < perFrame; i++)
            {
                if (TextCount() >= MaxFloatingTexts)
                {
                    RetireOldestText();
                }
                if (TextCount() >= MaxFloatingTexts)
                {
                    break;
                }
                PendingText text = _textBatch[i];
                _pendingTexts.Remove(text.Key);
                Spawn(new FxRequest { Kind = FxKind.Damage, To = text.Position, Damage = text.Damage, Side = text.Side, DamageKind = text.Kind });
            }
        }

        /// <summary>满屏时替换最旧的文字：先退口号，再退最早出现的数字（与六章一致）。</summary>
        private void RetireOldestText()
        {
            BattleEffectView retire = null;
            foreach (BattleEffectView text in _activeFx[(int)FxKind.Attack])
            {
                if (retire == null || text.TextOrder < retire.TextOrder)
                {
                    retire = text;
                }
            }
            if (retire == null)
            {
                foreach (BattleEffectView text in _activeFx[(int)FxKind.Damage])
                {
                    if (retire == null || text.TextOrder < retire.TextOrder)
                    {
                        retire = text;
                    }
                }
            }
            // 遍历结束后再退场：RetireFeedback 会回调 OnFxFinished 修改集合。
            if (retire != null)
            {
                retire.RetireFeedback();
            }
        }

        private int TextCount()
        {
            return _activeFx[(int)FxKind.Damage].Count + _activeFx[(int)FxKind.Attack].Count + _pendingFx[(int)FxKind.Damage] + _pendingFx[(int)FxKind.Attack];
        }
        private int AttackTextCount()
        {
            return _activeFx[(int)FxKind.Attack].Count + _pendingFx[(int)FxKind.Attack];
        }
        /// <summary>新文字排在最前，描边跟着正文一起排序（六章 BoardView 在避让时做同一件事）。</summary>
        private void ArrangeText(BattleEffectView text)
        {
            text.SetTextOrder(++_textOrder);
        }

        // ───────────────────────── 取池与回收 ─────────────────────────

        private bool CanSpawn(FxKind kind, int limit)
        {
            return _activeFx[(int)kind].Count + _pendingFx[(int)kind] < limit;
        }

        /// <summary>先占预算再取池：池里有现成对象就同步播放，没有才走异步加载（只有冷启动那几发会分配闭包）。</summary>
        private void Spawn(FxRequest request)
        {
            _pendingFx[(int)request.Kind]++;
            int generation = _fxGeneration;
            if (GamePoolRequests.TryGetReady<BattleEffectView>(out BattleEffectView ready, _fxRoot))
            {
                PresentFx(ready, request, generation);
                return;
            }
            LoadFx(request, generation);
        }
        private void LoadFx(FxRequest request, int generation)
        {
            GamePoolRequests.GetAsync<BattleEffectView>(effect => PresentFx(effect, request, generation), _fxRoot);
        }
        /// <summary>同步/异步两条路径的共同出口：先验版本，再释放预算，最后登记并开始播放。</summary>
        private void PresentFx(BattleEffectView effect, FxRequest request, int generation)
        {
            if (!_active || generation != _fxGeneration)
            {
                if (effect != null)
                {
                    RevPool.Return(effect, 0);
                }
                return;
            }
            _pendingFx[(int)request.Kind]--;
            if (effect == null)
            {
                RevLog.Warn("无尽战斗反馈加载失败：" + request.Kind, "RevGame");
                return;
            }
            // 先登记再播放：极短弹道会在 Show 里立即结束并回调，回调要能找到并移除它。
            _activeFx[(int)request.Kind].Add(effect);
            _fxShown[(int)request.Kind]++;
            switch (request.Kind)
            {
                case FxKind.Projectile:
                    effect.ShowProjectile(request.From, request.To, _fxFinished, request.Side, request.Piece);
                    break;
                case FxKind.Melee:
                    effect.ShowMelee(request.From, request.To, request.Piece, _fxFinished);
                    break;
                case FxKind.Pattern:
                    _patterns.Add(effect);
                    effect.ShowPattern(request.From, request.To, request.Piece, request.Radius, _fxFinished);
                    break;
                case FxKind.Damage:
                    effect.ShowDamage(request.To, request.Damage, request.DamageKind, _fxFinished, request.Side, _arrangeText);
                    break;
                default:
                    effect.ShowAttack(request.Text, request.From, _fxFinished, _arrangeText);
                    break;
            }
        }
        /// <summary>特效播完的回调：从所有分组里摘掉。回池由 BattleEffectView 自己完成，这里不能再还一次。</summary>
        private void OnFxFinished(BattleEffectView effect)
        {
            for (int i = 0; i < _activeFx.Length; i++)
            {
                _activeFx[i].Remove(effect);
            }
            _patterns.Remove(effect);
        }

        // ───────────────────────── 出生 / 升星 / 死亡 ─────────────────────────

        /// <summary>新棋子视图到手时调用：六章落子同款身体燃烧。</summary>
        private void PlayPieceBirth(PieceView view)
        {
            view.PlayBirthFire();
            _birthsShown++;
        }

        /// <summary>每帧同步视图时检查星级：变高说明刚升星，播六章同款雷暴。</summary>
        private void CheckUpgrade(PieceView view, int id, StarTier star)
        {
            if (_stars.TryGetValue(id, out StarTier old) && star > old)
            {
                view.PlayUpgradeStorm();
                _upgradesShown++;
            }
            _stars[id] = star;
        }

        /// <summary>
        /// 盘内棋子从规则棋盘消失时调用。死亡回执里有它就播溶解（棋子留在原地慢慢化掉），
        /// 否则（三合一被吃掉、读档重建）直接回池。溶解数量超过预算时也直接回池，只是少一段画面。
        /// </summary>
        private void RetirePieceView(int id, PieceView view)
        {
            _stars.Remove(id);
            if (_dyingIds.Remove(id) && _dissolving.Count + _pendingCorpses < MaxCorpses && view.BeginDeathDissolve(_corpseFinished))
            {
                TrackDissolve(view);
                return;
            }
            RevPool.Return(view);
        }

        /// <summary>外围杂兵死亡：正在播出生燃烧的直接就地溶解，否则临时借一枚棋子视图播溶解。</summary>
        private void HordeDied(EndlessDeathReceipt death)
        {
            // 每只杂兵倒下都有一声清脆的“啵”（有最短间隔，成片倒下时变成连续的碎裂声），这是割草爽感的关键。
            PlayFeelSound(SoundKill);
            for (int i = 0; i < _hordeBirth.Length; i++)
            {
                if (_hordeBirth[i] != null && _hordeSeen[i] == death.id)
                {
                    PieceView view = _hordeBirth[i];
                    _hordeBirth[i] = null;
                    _hordeBirthCount--;
                    if (_dissolving.Count + _pendingCorpses < MaxCorpses && view.BeginDeathDissolve(_corpseFinished))
                    {
                        TrackDissolve(view);
                    }
                    else
                    {
                        RevPool.Return(view);
                    }
                    return;
                }
            }
            if (_dissolving.Count + _pendingCorpses >= MaxCorpses)
            {
                return;
            }
            _pendingCorpses++;
            int generation = _fxGeneration;
            Vector3 position = new Vector3(death.position.x, death.position.y, -0.5f);
            if (GamePoolRequests.TryGetReady<PieceView>(out PieceView ready, transform))
            {
                PresentCorpse(ready, position, generation);
                return;
            }
            LoadCorpse(position, generation);
        }
        private void LoadCorpse(Vector3 position, int generation)
        {
            GamePoolRequests.GetAsync<PieceView>(view => PresentCorpse(view, position, generation), transform);
        }
        /// <summary>尸体视图：绑定和批量网格同一张敌方剑士外观，摆到死亡位置后立即开始溶解。</summary>
        private void PresentCorpse(PieceView view, Vector3 position, int generation)
        {
            if (!_active || generation != _fxGeneration)
            {
                if (view != null)
                {
                    RevPool.Return(view);
                }
                return;
            }
            _pendingCorpses--;
            if (view == null)
            {
                return;
            }
            BindHordeLook(view);
            // 尸体和死前的网格方块一样大，不会“死的瞬间突然变大”。
            view.transform.localScale = HordeScale(view);
            view.SnapTo(position);
            if (view.BeginDeathDissolve(_corpseFinished))
            {
                TrackDissolve(view);
            }
            else
            {
                RevPool.Return(view);
            }
        }
        /// <summary>批量网格画的是一星敌方剑士（见 OnPoolGet 的 _frames），临时视图用同一外观，画面才能无缝衔接。</summary>
        private void BindHordeLook(PieceView view)
        {
            view.Bind(--_corpseId, PieceKind.Swordman, StarTier.One, GameSide.Enemy);
            view.SetBodySize(1, false, false);
        }
        /// <summary>让视图身体宽度等于网格方块宽度（2 × HordeQuadHalf）所需的缩放；拿不到贴图尺寸时保持原缩放。</summary>
        private static Vector3 HordeScale(PieceView view)
        {
            Vector3 scale = view.transform.localScale;
            float width = view.BodyWorldWidth;
            return width > .0001f ? scale * (HordeQuadHalf * 2 / width) : scale;
        }
        /// <summary>溶解播完（或保险期限到）后回池；离场时已统一回收的视图不会再进来。</summary>
        private void OnCorpseFinished(PieceView view)
        {
            if (_dissolving.Remove(view))
            {
                RevPool.Return(view);
            }
        }

        /// <summary>
        /// 外围杂兵出生：槽位里的敌人编号变了，就借一枚棋子视图跟着它播 1.4 秒出生燃烧，
        /// 这段时间批量网格里的方块隐藏（见 UpdateHorde），烧完再换回网格。领主（kind ≥ 6）有自己的登场演出，不在这里播。
        /// </summary>
        private void UpdateHordeBirths(EndlessRun run)
        {
            int length = run.horde.Length;
            if (_hordeSeen.Length != length)
            {
                _hordeSeen = new int[length];
                _hordeBirth = new PieceView[length];
                _hordeBirthAt = new float[length];
                _hordeBirthTween = new Tweener[length];
            }
            float now = Time.time;
            for (int i = 0; i < length; i++)
            {
                EndlessHorde h = run.horde[i];
                if (!h.active)
                {
                    ReturnHordeBirth(i);
                    continue;
                }
                if (h.id != _hordeSeen[i])
                {
                    ReturnHordeBirth(i);
                    _hordeSeen[i] = h.id;
                    if (_hordeSynced && h.kind < 6 && _hordeBirthCount + _pendingHordeBirths < MaxHordeBirths)
                    {
                        RequestHordeBirth(i, h.id);
                    }
                    continue;
                }
                PieceView view = _hordeBirth[i];
                if (view == null)
                {
                    continue;
                }
                if (now - _hordeBirthAt[i] >= HordeBirthSeconds)
                {
                    ReturnHordeBirth(i);
                }
                else
                {
                    view.SnapTo(new Vector3(h.x, h.y, -0.5f));
                }
            }
            _hordeSynced = true;
        }
        private void RequestHordeBirth(int slot, int id)
        {
            _pendingHordeBirths++;
            int generation = _fxGeneration;
            if (GamePoolRequests.TryGetReady<PieceView>(out PieceView ready, transform))
            {
                PresentHordeBirth(ready, slot, id, generation);
                return;
            }
            LoadHordeBirth(slot, id, generation);
        }
        private void LoadHordeBirth(int slot, int id, int generation)
        {
            GamePoolRequests.GetAsync<PieceView>(view => PresentHordeBirth(view, slot, id, generation), transform);
        }
        /// <summary>异步到手时这只杂兵可能已经死了或进盘了：核对槽位编号，对不上就还池。</summary>
        private void PresentHordeBirth(PieceView view, int slot, int id, int generation)
        {
            if (!_active || generation != _fxGeneration)
            {
                if (view != null)
                {
                    RevPool.Return(view);
                }
                return;
            }
            _pendingHordeBirths--;
            if (view == null)
            {
                return;
            }
            EndlessService service = RevServices.Get<EndlessService>();
            if (service?.Run == null || slot >= service.Run.horde.Length || !service.Run.horde[slot].active || service.Run.horde[slot].id != id || _hordeBirth[slot] != null)
            {
                RevPool.Return(view);
                return;
            }
            EndlessHorde h = service.Run.horde[slot];
            BindHordeLook(view);
            view.SnapTo(new Vector3(h.x, h.y, -0.5f));
            view.PlayBirthFire();
            // 出生时是六章同款的大身体，烧完要换回较小的网格方块。以前到点直接换，看起来“啪”地一下缩小；
            // 现在用 DOTween 在整段出生时间里平滑缩到网格方块大小（先慢后快再慢），到点换回网格时尺寸正好一致。
            _hordeBirthTween[slot] = view.transform.DOScale(HordeScale(view), HordeBirthSeconds).SetEase(Ease.InOutSine);
            _hordeBirthsShown++;
            _hordeBirth[slot] = view;
            _hordeBirthAt[slot] = Time.time;
            _hordeBirthCount++;
        }
        private void ReturnHordeBirth(int slot)
        {
            if (_hordeBirth[slot] == null)
            {
                return;
            }
            if (_hordeBirthTween[slot] != null)
            {
                _hordeBirthTween[slot].Kill(false);
                _hordeBirthTween[slot] = null;
            }
            // 还池前把缩放复原成标准一格身体，下一位借用者（盘内棋子/尸体）不会继承缩小后的尺寸。
            _hordeBirth[slot].SetBodySize(1, false, false);
            RevPool.Return(_hordeBirth[slot]);
            _hordeBirth[slot] = null;
            _hordeBirthCount--;
        }
        /// <summary>批量网格是否要隐藏这个槽位的方块（它正由出生视图代画）。</summary>
        private bool HordeDrawnByView(int slot)
        {
            return slot < _hordeBirth.Length && _hordeBirth[slot] != null;
        }

        /// <summary>登记一具正在溶解的视图并计数。</summary>
        private void TrackDissolve(PieceView view)
        {
            _dissolving.Add(view);
            _dissolvesShown++;
        }

        /// <summary>本局累计真正播出的反馈次数（只增不减，退出清零）；供自动化验收断言“确实用六章资源播了”。</summary>
        private readonly int[] _fxShown = new int[5];
        private int _birthsShown, _hordeBirthsShown, _upgradesShown, _dissolvesShown;

        /// <summary>只读快照：当前在播数量 + 本局累计次数 + 落子提示格数。不改任何状态。</summary>
        public object FeedbackSnapshot()
        {
            return new Dictionary<string, object>
            {
                ["placementHints"] = PlacementHintCount,
                ["activeProjectiles"] = _activeFx[(int)FxKind.Projectile].Count, ["activeMelee"] = _activeFx[(int)FxKind.Melee].Count,
                ["activePatterns"] = _activeFx[(int)FxKind.Pattern].Count, ["activeTexts"] = TextCount(), ["activeDissolves"] = _dissolving.Count,
                ["projectiles"] = _fxShown[(int)FxKind.Projectile], ["melee"] = _fxShown[(int)FxKind.Melee], ["patterns"] = _fxShown[(int)FxKind.Pattern],
                ["damageTexts"] = _fxShown[(int)FxKind.Damage], ["attackTexts"] = _fxShown[(int)FxKind.Attack],
                ["guard"] = GuardSnapshot(),
                ["auras"] = AuraCount, ["auraFlashes"] = AuraFlashCount, ["comboShouts"] = ComboShoutCount, ["hordeBars"] = HordeBarCount,
                ["births"] = _birthsShown, ["hordeBirths"] = _hordeBirthsShown, ["upgrades"] = _upgradesShown, ["dissolves"] = _dissolvesShown,
                ["withinBudget"] = _activeFx[(int)FxKind.Projectile].Count <= MaxProjectiles && _activeFx[(int)FxKind.Melee].Count <= MaxMeleeEffects
                    && TextCount() <= MaxFloatingTexts && _dissolving.Count <= MaxCorpses && _hordeBirthCount <= MaxHordeBirths
            };
        }

        /// <summary>退出无尽：先让所有迟到回调过期，再把特效、尸体、出生视图全部还池。</summary>
        private void ReleaseCombatFx()
        {
            _fxGeneration++;
            _fxBegun = false;
            for (int i = 0; i < _activeFx.Length; i++)
            {
                // BattleEffectView 回池时 Kill(false)，不会触发完成回调，遍历中集合不会被修改。
                foreach (BattleEffectView effect in _activeFx[i])
                {
                    if (effect != null)
                    {
                        RevPool.Return(effect, 0);
                    }
                }
                _activeFx[i].Clear();
                _pendingFx[i] = 0;
            }
            _patterns.Clear();
            _pendingTexts.Clear();
            _attackTextAt.Clear();
            foreach (PieceView view in _dissolving)
            {
                if (view != null)
                {
                    RevPool.Return(view);
                }
            }
            _dissolving.Clear();
            _pendingCorpses = 0;
            for (int i = 0; i < _hordeBirth.Length; i++)
            {
                ReturnHordeBirth(i);
            }
            System.Array.Clear(_hordeSeen, 0, _hordeSeen.Length);
            _hordeBirthCount = _pendingHordeBirths = 0;
            _dyingIds.Clear();
            _stars.Clear();
            System.Array.Clear(_fxShown, 0, _fxShown.Length);
            _birthsShown = _hordeBirthsShown = _upgradesShown = _dissolvesShown = 0;
        }
    }
}
