// ============================================================
// EndlessArena.Feel.cs —— 剑气、外围血条、组合技喊招、割草音效（新手先读这段）
//
// 【剑气】所有近战棋的剑气画在一张动态网格里（RevGame/Endless/SwordAura），一枚棋子一个方块，整场 1 次绘制。
//         方块大小直接取 EndlessService.AuraRadius —— 和伤害判定同一个数，画面上的圈就是真实切割范围。
//         切中敌人时服务写一条“剑气闪光”回执，这里把对应棋子的闪光强度拉满再在 0.25 秒内衰减。
// 【血条】外围杂兵没有 PieceView，另用一张动态网格（RevGame/Endless/HpBar）画头顶血条，只画受过伤的敌人。
//         盘内棋子沿用 PieceView 自带的六章血条，不重复画。
// 【组合技】服务写一条喊招回执（combo = 编号+1），这里弹出组合技名字飘字（颜色/字号来自 EndlessCombo 表，
//         持续时间/冲击倍率沿用 BattleText 表里该棋种攻击口号的配置），再加一下震屏和组合技音效。
// 【音效】Kenney 的 CC0 打击音效（见 文档/第三方素材授权）。同一种声音设最短间隔，
//         避免上百只怪同时挨打时变成噪音；间隔只影响听感，不影响伤害。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessArena
    {
        [Tooltip("近战剑气批量网格，材质 RevGame/Endless/SwordAura，由无尽资产生成器装配。")]
        [SerializeField] private MeshRenderer auraRenderer;
        [Tooltip("外围敌人头顶血条批量网格，材质 RevGame/Endless/HpBar，由无尽资产生成器装配。")]
        [SerializeField] private MeshRenderer hpBarRenderer;

        /// <summary>剑气方块上限：盘面只有 81 格，近战棋不可能超过这个数，是结构容量不是数值。</summary>
        private const int AuraCapacity = 81;
        /// <summary>外圈符文环画在方块半径的 0.93 处，方块要比切割半径大一点，圈才正好落在真实范围上。</summary>
        private const float AuraRingRatio = .93f;
        /// <summary>剑气闪光从 1 衰减到 0 的时长（秒，纯表现）。</summary>
        private const float AuraFlashSeconds = .25f;
        /// <summary>外围血条相对敌人中心的高度与半宽/半高（格，纯表现）。</summary>
        private const float BarOffset = .44f, BarHalfWidth = .3f, BarHalfHeight = .055f;
        /// <summary>本模式用到的音效别名与逻辑路径（相对 Audio/Sfx），进入时登记、退出时卸载。</summary>
        private static readonly string[] FeelSounds =
        {
            "EndlessSlash", "EndlessHit", "EndlessKill", "EndlessBurst", "EndlessCombo"
        };
        private const int SoundSlash = 0, SoundHit = 1, SoundKill = 2, SoundBurst = 3, SoundCombo = 4;
        /// <summary>每种音效的最短间隔（秒）与音量。数值只影响听感，属于表现节奏，不进玩法表。</summary>
        private static readonly float[] SoundGap = { .07f, .05f, .045f, .12f, .15f };
        private static readonly float[] SoundVolume = { .32f, .26f, .3f, .4f, .55f };
        private readonly float[] _soundAt = new float[5];

        private Mesh _auraMesh, _barMesh;
        private int _barDrawCount;
        private Vector3[] _auraVertices, _barVertices;
        private Vector2[] _auraUv, _auraData, _barUv;
        private Color[] _auraColors, _barColors;
        /// <summary>每枚近战棋上次被剑气闪光点亮的时间（unscaled 秒）。</summary>
        private readonly Dictionary<int, float> _auraFlash = new Dictionary<int, float>(16);
        /// <summary>组合技飘字配置缓存，按组合技编号；首次用到时由表行拼出 BattleText，避免每次喊招分配字符串。</summary>
        private readonly Dictionary<int, BattleText> _comboTexts = new Dictionary<int, BattleText>(8);
        private int _comboShouts, _auraFlashes;

        /// <summary>进入无尽：建两张网格（只建一次）、登记音效。</summary>
        private void BeginFeel()
        {
            if (_auraMesh == null)
            {
                _auraMesh = CreateMesh(AuraCapacity, out _auraVertices, out _auraUv, out _auraColors);
                _auraData = new Vector2[AuraCapacity * 4];
                _auraMesh.uv2 = _auraData;
                _auraMesh.name = "无尽剑气批量网格";
                auraRenderer.GetComponent<MeshFilter>().sharedMesh = _auraMesh;
                _barMesh = CreateMesh(EndlessConfig.Count("HordeLimit"), out _barVertices, out _barUv, out _barColors);
                _barMesh.name = "无尽外围血条批量网格";
                hpBarRenderer.GetComponent<MeshFilter>().sharedMesh = _barMesh;
                // 血条方块的 uv 固定为 0~1，只写一次；之后每帧只改顶点位置和颜色。
                for (int i = 0; i < _barUv.Length; i += 4)
                {
                    _barUv[i] = new Vector2(0, 0);
                    _barUv[i + 1] = new Vector2(1, 0);
                    _barUv[i + 2] = new Vector2(1, 1);
                    _barUv[i + 3] = new Vector2(0, 1);
                }
                _barMesh.uv = _barUv;
                for (int i = 0; i < _auraUv.Length; i += 4)
                {
                    _auraUv[i] = new Vector2(0, 0);
                    _auraUv[i + 1] = new Vector2(1, 0);
                    _auraUv[i + 2] = new Vector2(1, 1);
                    _auraUv[i + 3] = new Vector2(0, 1);
                }
                _auraMesh.uv = _auraUv;
            }
            for (int i = 0; i < FeelSounds.Length; i++)
            {
                if (!RevSound.IsRegistered(FeelSounds[i]))
                {
                    RevSound.Register(FeelSounds[i], "Endless/" + FeelSounds[i]);
                }
                _soundAt[i] = 0;
            }
            _auraFlash.Clear();
            _comboShouts = _auraFlashes = 0;
        }

        /// <summary>播一种割草音效；距离上次同类声音太近就跳过。</summary>
        private void PlayFeelSound(int index)
        {
            float now = Time.unscaledTime;
            if (now - _soundAt[index] < SoundGap[index])
            {
                return;
            }
            _soundAt[index] = now;
            RevSound.Play(FeelSounds[index], volume: SoundVolume[index]);
        }

        /// <summary>剑气闪光回执：点亮这枚棋子的剑气，并给一声刀切音。</summary>
        private void FlashAura(int pieceId)
        {
            _auraFlash[pieceId] = Time.unscaledTime;
            _auraFlashes++;
            PlayFeelSound(SoundSlash);
        }

        /// <summary>组合技喊招：组合技名字飘字（不受普通口号冷却限制，满了就挤掉最旧的字）+ 震屏 + 音效。</summary>
        private void PlayComboShout(EndlessRun run, EndlessImpact hit)
        {
            int id = hit.combo - 1;
            if (!_comboTexts.TryGetValue(id, out BattleText text))
            {
                EndlessComboTable table = RevDataTableManager.Get<EndlessComboTable>();
                if (table == null || !table.FindByKey(id, out EndlessCombo row))
                {
                    return;
                }
                // 持续时间与冲击倍率借用该棋种攻击口号在 BattleText 表里的配置，和六章口号同一节奏。
                BattleText attack = GameTextConfig.Require("Attack_" + hit.sourceKind);
                text = new BattleText
                {
                    Id = "EndlessCombo_" + id, Text = row.Name + "！", ColorHex = row.ColorHex, FontSize = row.FontSize,
                    Duration = attack.Duration, PeakScale = attack.PeakScale
                };
                _comboTexts[id] = text;
            }
            if (_pieces.TryGetValue(hit.sourceId, out PieceView source))
            {
                source.PlayAttackPulse();
            }
            if (TextCount() >= MaxFloatingTexts)
            {
                RetireOldestText();
            }
            Spawn(new FxRequest { Kind = FxKind.Attack, From = new Vector3(hit.from.x, hit.from.y + .25f, 0), Text = text });
            _comboShouts++;
            PlayFeelSound(SoundCombo);
            if (run.shake)
            {
                _shakeBoost = Mathf.Max(_shakeBoost, .07f);
            }
        }

        /// <summary>每帧重写剑气网格：只画当前带剑气的己方近战棋，其余方块收成一个点（不产生像素）。</summary>
        private void UpdateAuras(EndlessService service)
        {
            float now = Time.unscaledTime;
            int count = 0;
            for (int i = 0; i < service.Units.Count && count < AuraCapacity; i++)
            {
                EndlessUnit unit = service.Units[i];
                if (!EndlessService.HasAura(unit))
                {
                    continue;
                }
                Piece p = unit.Piece;
                Vector2 center = VisualCenter(p);
                float half = service.AuraRadius(p) / AuraRingRatio;
                float flash = 0;
                if (_auraFlash.TryGetValue(p.Id, out float at))
                {
                    flash = Mathf.Clamp01(1 - (now - at) / AuraFlashSeconds);
                }
                Color tint = AuraTint(p.Def.Kind);
                tint.a = flash;
                int v = count * 4;
                // z=-0.4：在棋子（-0.5）身后、棋盘和外围敌人前面，剑刃从棋子背后转出来。
                _auraVertices[v] = new Vector3(center.x - half, center.y - half, -.4f);
                _auraVertices[v + 1] = new Vector3(center.x + half, center.y - half, -.4f);
                _auraVertices[v + 2] = new Vector3(center.x + half, center.y + half, -.4f);
                _auraVertices[v + 3] = new Vector3(center.x - half, center.y + half, -.4f);
                // 相位用编号乘黄金比例取小数，各棋子错开；刃数 = 星级 + 1。
                Vector2 data = new Vector2(Mathf.Repeat(p.Id * .618f, 1), (int)p.Def.Star + 1);
                for (int n = 0; n < 4; n++)
                {
                    _auraColors[v + n] = tint;
                    _auraData[v + n] = data;
                }
                count++;
            }
            for (int i = count; i < _auraLastCount; i++)
            {
                int v = i * 4;
                _auraVertices[v] = _auraVertices[v + 1] = _auraVertices[v + 2] = _auraVertices[v + 3] = Vector3.zero;
            }
            _auraLastCount = count;
            _auraMesh.vertices = _auraVertices;
            _auraMesh.colors = _auraColors;
            _auraMesh.uv2 = _auraData;
            AuraCount = count;
        }
        private int _auraLastCount;
        /// <summary>验收用：当前画着几圈剑气。</summary>
        public int AuraCount
        {
            get;
            private set;
        }
        /// <summary>剑气主色：剑士冰蓝、盾战金色、重剑赤焰、刺客紫影。只是外观区分，不影响伤害。</summary>
        private static Color AuraTint(PieceKind kind)
        {
            switch (kind)
            {
                case PieceKind.Shield: return new Color(1f, .82f, .35f);
                case PieceKind.HeavySword: return new Color(1f, .42f, .22f);
                case PieceKind.Assassin: return new Color(.74f, .48f, 1f);
                default: return new Color(.4f, .88f, 1f);
            }
        }

        /// <summary>每帧重写外围血条：只画活着、受过伤、且没有被出生视图代画的敌人。</summary>
        private void UpdateHordeBars(EndlessRun run)
        {
            EndlessHordeComponents horde = RevServices.GetRequired<EndlessService>().Horde;
            int bars = 0;
            int active = horde.ActiveCount;
            for (int cursor = 0; cursor < active; cursor++)
            {
                int i = horde.ActiveSlot(cursor);
                EndlessHorde h = horde[i];
                int v = bars * 4;
                if (!h.active || h.hp >= h.maxHp || h.maxHp <= 0 || HordeDrawnByView(i))
                {
                    continue;
                }
                float ratio = Mathf.Clamp01(h.hp / h.maxHp);
                float scale = h.kind >= 6 ? 2.2f : 1f;
                float x = h.x, y = h.y + BarOffset * scale, w = BarHalfWidth * scale, hh = BarHalfHeight * Mathf.Sqrt(scale);
                _barVertices[v] = new Vector3(x - w, y - hh, -.6f);
                _barVertices[v + 1] = new Vector3(x + w, y - hh, -.6f);
                _barVertices[v + 2] = new Vector3(x + w, y + hh, -.6f);
                _barVertices[v + 3] = new Vector3(x - w, y + hh, -.6f);
                // 绿 → 黄 → 红；alpha 存剩余比例给 Shader 决定填充长度。
                Color color = ratio > .5f ? Color.Lerp(new Color(1f, .82f, .2f), new Color(.35f, .95f, .35f), (ratio - .5f) * 2) : Color.Lerp(new Color(1f, .22f, .18f), new Color(1f, .82f, .2f), ratio * 2);
                color.a = ratio;
                for (int n = 0; n < 4; n++)
                {
                    _barColors[v + n] = color;
                }
                bars++;
            }
            for (int i = bars * 4; i < _barDrawCount * 4; i++)
            {
                _barVertices[i] = Vector3.zero;
            }
            _barDrawCount = bars;
            _barMesh.vertices = _barVertices;
            _barMesh.colors = _barColors;
            HordeBarCount = bars;
        }
        /// <summary>验收用：当前画着几条外围血条。</summary>
        public int HordeBarCount
        {
            get;
            private set;
        }

        /// <summary>退出无尽：卸载本模式音效，清掉闪光记录；网格随竞技场复用。</summary>
        private void ReleaseFeel()
        {
            for (int i = 0; i < FeelSounds.Length; i++)
            {
                RevSound.Unload(FeelSounds[i]);
            }
            _auraFlash.Clear();
            _auraLastCount = AuraCapacity;
            AuraCount = HordeBarCount = 0;
        }

        /// <summary>验收用的剑气/组合技计数。</summary>
        public int ComboShoutCount => _comboShouts;
        public int AuraFlashCount => _auraFlashes;

        private void DestroyFeelMeshes()
        {
            if (_auraMesh != null)
            {
                Destroy(_auraMesh);
                Destroy(_barMesh);
            }
        }
    }
}
