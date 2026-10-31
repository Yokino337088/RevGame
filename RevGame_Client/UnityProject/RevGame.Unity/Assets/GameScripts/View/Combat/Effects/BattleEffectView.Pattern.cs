// 范围特效由棋盘按固定预算推进关键帧，避免每个池对象各自创建Tween并在同一帧写Shader参数。
// 火、水、圣光共享一个单Pass程序Shader和已有图集载体；伤害早已结算，播放与回收不能再次扣血。
using RevGame.Core;
using UnityEngine;
using UnityEngine.Sprites;

namespace RevGame.View
{
    public sealed partial class BattleEffectView
    {
        /// <summary>三种元素的共享材质资产，由预制体持有；运行时只选引用，禁止通过material属性克隆。</summary>
        [SerializeField] private Material firePatternMaterial, waterPatternMaterial, holyPatternMaterial;
        /// <summary>当前租约的棋种、开始时刻和寿命；单位为游戏秒，出池播放时覆盖，不跨租约沿用。</summary>
        private PieceKind _patternKind;
        private float _patternStartedAt, _patternDuration;
        /// <summary>已提交的离散帧编号，-1表示新租约；每份效果最多24个关键帧，重复编号不写参数块。</summary>
        private int _patternKeyframe = -1;
        /// <summary>每次播放的艺术关键帧数。只影响表现，不改变技能时间、伤害、射程或攻击进度。</summary>
        private int _patternKeyframeCount;
        /// <summary>诊断用只读身份与帧号，不暴露修改表现状态的入口。</summary>
        public PieceKind PatternKind => _patternKind;
        public int PatternKeyframe => _patternKeyframe;
        public bool UsesElementalShader => IsPatternActive && spriteRenderer.sharedMaterial != null
            && spriteRenderer.sharedMaterial.shader.name == "RevGame/Combat/ElementalImpact";
        /// <summary>只读验收标记：必须处于本次范围动画的有效租约内，不能把已经回池的旧模式算作在画。</summary>
        public bool IsPatternActive => _animationMode == 3 && _projectileActive && spriteRenderer.enabled;
        /// <summary>
        /// 棋盘攻击形状复用既有池和FullRect盾击Sprite；元素使用独立共享材质，其他棋种保留既有共享Shader。
        /// radius为结算快照中的格子半径，source/center为世界位置；函数只画事实，不持有棋子、不修改模型。
        /// </summary>
        public void ShowPattern(Vector3 source, Vector3 center, PieceKind kind, int radius, System.Action<BattleEffectView> onFinished)
        {
            ResetVisual(center, onFinished);
            if (shieldImpact == null || shieldMaterial == null)
            {
                Revolution.RevLog.Error("攻击范围特效缺少共享盾击资产", "RevGame");
                Finish();
                return;
            }
            damageText.gameObject.SetActive(false);
            spriteRenderer.enabled = true;
            spriteRenderer.sprite = shieldImpact;
            _patternKind = kind;
            Material material = kind == PieceKind.MageFire ? firePatternMaterial
                : kind == PieceKind.MageWater ? waterPatternMaterial : kind == PieceKind.Priest ? holyPatternMaterial : shieldMaterial;
            if (material == null)
            {
                Revolution.RevLog.Error("元素攻击特效材质未配置：" + kind, "RevGame");
                Finish();
                return;
            }
            spriteRenderer.sharedMaterial = material;
            spriteRenderer.drawMode = SpriteDrawMode.Simple;
            // 不设置renderer.material，避免每发攻击克隆材质。参数块归本池对象，每次租用完整覆盖。
            _projectileProperties.Clear();
            _projectileProperties.SetTexture("_MainTex", shieldImpact.texture);
            _projectileProperties.SetVector(SpriteUvId, DataUtility.GetOuterUV(shieldImpact));
            _projectileProperties.SetFloat("_Pattern", (int)kind + 1);
            _projectileProperties.SetFloat("_PatternCells", radius * 2 + 1);
            _projectileProperties.SetFloat("_Progress", 0f);
            float width = Consts.CellSize;
            float height = Consts.CellSize;
            if (kind == PieceKind.MageFire)
            {
                width = height = (radius * 2 + 1) * Consts.CellSize;
            }
            else if (kind == PieceKind.Crossbow || kind == PieceKind.MageWater)
            {
                width = Mathf.Max(Consts.CellSize, Vector3.Distance(source, center));
                height = Consts.CellSize * (kind == PieceKind.MageWater ? 1.05f : .5f);
                transform.position = (source + center) * .5f;
                transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(center.y - source.y, center.x - source.x) * Mathf.Rad2Deg);
            }
            else if (kind == PieceKind.Priest)
            {
                // 圣光柱只向目标上方扩展表现；不扩大治疗射程，也不制造新的命中格。
                width = Consts.CellSize * 2.1f;
                height = Consts.CellSize * 2.6f;
                transform.position = center + Vector3.up * (Consts.CellSize * .35f);
            }
            else if (kind == PieceKind.HeavySword)
            {
                width = height = Consts.CellSize * (radius * 2 + 1);
                transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(center.y - source.y, center.x - source.x) * Mathf.Rad2Deg);
            }
            Vector3 size = shieldImpact.bounds.size;
            transform.localScale = new Vector3(width / Mathf.Max(.01f, size.x), height / Mathf.Max(.01f, size.y), 1f);
            spriteRenderer.color = Color.white;
            _animationMode = 3;
            _projectileActive = true;
            // 以下为纯美术节奏，允许留在代码：不参与攻击冷却、伤害或技能持续时间。
            _patternDuration = kind == PieceKind.MageFire ? .8f : kind == PieceKind.MageWater ? .6f : kind == PieceKind.Priest ? .9f : .5f;
            _patternKeyframeCount = kind == PieceKind.MageWater ? 18 : 24;
            _patternStartedAt = Time.time;
            _patternKeyframe = -1;
            _projectileExpiresAt = _patternStartedAt + _patternDuration + .3f;
            AdvancePattern(_patternStartedAt);
        }

        /// <summary>
        /// 由BoardView轮到本实例时推进一次，返回是否真的写入Shader；没有为每发创建Tween、Timer或闭包。
        /// 使用开始时刻计算进度，低帧率下直接跳到正确关键帧，不补播过期帧，也不拖长实际租约。
        /// </summary>
        public bool AdvancePattern(float now)
        {
            if (!IsPatternActive)
            {
                return false;
            }
            float progress = Mathf.Clamp01((now - _patternStartedAt) / _patternDuration);
            if (progress >= 1f)
            {
                Finish();
                return false;
            }
            int frame = Mathf.Min(_patternKeyframeCount - 1, Mathf.FloorToInt(progress * _patternKeyframeCount));
            if (frame == _patternKeyframe)
            {
                return false;
            }
            _patternKeyframe = frame;
            float stepped = frame / (float)(_patternKeyframeCount - 1);
            _projectileProperties.SetFloat("_Progress", stepped);
            spriteRenderer.SetPropertyBlock(_projectileProperties);
            spriteRenderer.color = new Color(1f, 1f, 1f, Mathf.Clamp01((1f - stepped) * 4f));
            return true;
        }
    }
}
