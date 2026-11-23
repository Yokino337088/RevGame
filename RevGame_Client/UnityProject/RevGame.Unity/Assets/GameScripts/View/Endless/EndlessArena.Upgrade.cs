// ============================================================
// EndlessArena.Upgrade.cs —— 手牌升级目标与升级爆发的共享Shader表现。
// 一张最多82个方块的网格同时画一个拖牌目标和81枚棋子的短暂升级爆发。
// 服务先提交升级，表现只读身份和星级；不创建逐棋材质，不靠动画回调升级或扣牌。
// ============================================================
using System.Collections.Generic;
using RevGame.Core;
using RevGame.Pieces;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    /// <summary>竞技场升级显示职责：显示HUD所指身份和服务已提交升星事实，资源/网格随竞技场租约释放。</summary>
    public sealed partial class EndlessArena
    {
        /// <summary>项目交互配方绑定的共享材质网格，随竞技场租约复用；缺失时明确报错，不画白片。</summary>
        [SerializeField] private MeshRenderer upgradeRenderer;
        /// <summary>81格最多81枚盘内棋子，再加一个拖曳目标；这是绘制结构上限而非玩法数值。</summary>
        private const int UpgradeCapacity = 82;
        /// <summary>升级爆发1.4秒、直径约3.6格，纯表现节奏；明显大于一格身体，仍不扩大升级或伤害范围。</summary>
        private const float UpgradeSeconds = 1.4f, UpgradeHalfSize = 1.8f;
        /// <summary>松手前锁定目标的Id；面板拥有选择，竞技场只接收和显示，不自行决定升级规则。</summary>
        private int _cardUpgradeTarget;
        /// <summary>升级事实发生时的现实时间，身份随本局失效；退出时清空，不能在重入新局补播。</summary>
        private readonly Dictionary<int, float> _upgradeAt = new Dictionary<int, float>(81);
        /// <summary>删除过期记录的竞技场私有缓冲，避免遍历Dictionary时修改或逐帧创建列表。</summary>
        private readonly List<int> _expiredUpgrades = new List<int>(81);
        /// <summary>竞技场持有的升级网格批次，首次显示时创建，回池保留容量。</summary>
        private EndlessEffectBatch _upgradeBatch;
        /// <summary>上一帧实际方块数和本局累计爆发数；只读诊断不参与业务。</summary>
        private int _upgradeDrawCount, _upgradeBursts;
        /// <summary>缺资源只报告一次，避免每帧拼日志；新的Prefab组件会重新检查。</summary>
        private bool _upgradeResourceError;

        /// <summary>UI转发当前预览Id。传0立即停止目标环；已经发生的升级爆发继续按自身寿命显示。</summary>
        public void SetCardUpgradeTarget(int id)
        {
            _cardUpgradeTarget = id;
        }
        /// <summary>将模型身份映射到平滑移动后的画面中心；资源尚未交付时回退模型坐标。</summary>
        public Vector2 CardTargetCenter(Piece piece)
        {
            return VisualCenter(piece);
        }
        /// <summary>只在视图发现真实升星时登记爆发，重复观察同一星级不会重复登记或播放声音。</summary>
        private void FlashCardUpgrade(int id)
        {
            _upgradeAt[id] = Time.unscaledTime;
            _upgradeBursts++;
            RevSound.Play("Upgrade", volume: .45f);
        }
        /// <summary>
        /// 复用预分配网格：拖牌对象用金色锁定环，已经升级对象用大环、光柱和星芒。
        /// 进度仍写 UV2。模型删除或播放结束后撤掉对应方块，只提交正在画的索引范围。
        /// </summary>
        private void UpdateCardUpgradeVisual(EndlessService service)
        {
            // 普通帧没有目标/爆发，也没有上一帧需要清除的几何，就不扫描全部棋子或上传空网格。
            if (_cardUpgradeTarget == 0 && _upgradeAt.Count == 0 && _upgradeDrawCount == 0)
            {
                return;
            }
            if (upgradeRenderer == null)
            {
                if (!_upgradeResourceError)
                {
                    _upgradeResourceError = true;
                    RevLog.Error("无尽升级共享网格缺失，请执行项目交互资产配方", "RevGame");
                }
                return;
            }
            _upgradeBatch ??= BindEffectBatch(upgradeRenderer, UpgradeCapacity, "无尽升级目标与爆发共享网格");
            int count = 0;
            float now = Time.unscaledTime;
            Piece target = service.Board.FindPiece(_cardUpgradeTarget);
            // 身体高亮与目标环共享身份/时刻，普通棋子保持0；不会改变共享材质或棋子战力。
            foreach (KeyValuePair<int, PieceView> pair in _pieces)
            {
                float strength = pair.Key == _cardUpgradeTarget ? .7f : 0;
                if (_upgradeAt.TryGetValue(pair.Key, out float started))
                {
                    strength = Mathf.Max(strength, Mathf.Clamp01(1-(now-started)/UpgradeSeconds));
                }
                pair.Value.SetCardHighlight(strength);
            }
            if (target != null && target.IsAlive)
            {
                _upgradeBatch.WriteSquare(count++, VisualCenter(target), .85f, -.8f, new Color(1, .85f, .35f, 1), new Vector2(Mathf.Repeat(now, 10), 0));
            }
            _expiredUpgrades.Clear();
            foreach (KeyValuePair<int, float> entry in _upgradeAt)
            {
                float progress = (now - entry.Value) / UpgradeSeconds;
                Piece upgraded = service.Board.FindPiece(entry.Key);
                if (progress >= 1 || upgraded == null || !upgraded.IsAlive)
                {
                    _expiredUpgrades.Add(entry.Key);
                    continue;
                }
                if (count < UpgradeCapacity)
                {
                    _upgradeBatch.WriteSquare(count++, VisualCenter(upgraded), UpgradeHalfSize, -.8f, new Color(.65f, .92f, 1, 1), new Vector2(progress, 1));
                }
            }
            for (int i = 0; i < _expiredUpgrades.Count; i++)
            {
                _upgradeAt.Remove(_expiredUpgrades[i]);
            }
            _upgradeDrawCount = count;
            _upgradeBatch.Submit(count);
            upgradeRenderer.enabled = count > 0;
        }
        /// <summary>开发态读取实际索引范围、材质与爆发计数，不改变升级状态。</summary>
        public object UpgradeVisualSnapshot()
        {
            return new Dictionary<string, object>
            {
                ["target"] = _cardUpgradeTarget, ["quads"] = _upgradeDrawCount, ["bursts"] = _upgradeBursts,
                ["activeBursts"] = _upgradeAt.Count, ["sharedShader"] = upgradeRenderer != null && upgradeRenderer.sharedMaterial != null && upgradeRenderer.sharedMaterial.shader.name == "RevGame/Endless/CardUpgrade",
                ["visible"] = upgradeRenderer != null && upgradeRenderer.enabled && _upgradeDrawCount > 0,
                ["indices"] = _upgradeBatch == null ? 0 : _upgradeBatch.Mesh.GetIndexCount(0)
            };
        }
        /// <summary>回池清当前局身份和闪光，撤掉索引并关闭渲染器，防止旧目标在下一局出现。</summary>
        private void ResetCardUpgradeVisual()
        {
            _cardUpgradeTarget = 0;
            _upgradeAt.Clear();
            _expiredUpgrades.Clear();
            _upgradeBursts = 0;
            foreach (KeyValuePair<int, PieceView> pair in _pieces)
            {
                pair.Value.SetCardHighlight(0);
            }
            _upgradeBatch?.Submit(0);
            _upgradeDrawCount = 0;
            if (upgradeRenderer != null)
            {
                upgradeRenderer.enabled = false;
            }
        }
        /// <summary>竞技场真实销毁时才释放网格；普通回池保留容量供下次复用。</summary>
        private void DestroyCardUpgradeMesh()
        {
            Destroy(_upgradeBatch?.Mesh);
        }
    }
}
