using DG.Tweening;
using RevGame.Core;
using UnityEngine;
using UnityEngine.Sprites;

namespace RevGame.View
{
    public sealed partial class BattleEffectView
    {
        /// <summary>只读验收标记：必须处于本次范围动画的有效租约内，不能把已经回池的旧模式算作在画。</summary>
        public bool IsPatternActive => _animationMode == 3 && _projectileActive && spriteRenderer.enabled;
        /// <summary>
        /// 棋盘攻击形状复用既有池、盾击Sprite及共享Shader，不新增纹理/材质租约。
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
            spriteRenderer.sharedMaterial = shieldMaterial;
            // 不设置renderer.material，避免每发攻击克隆材质。参数块归本池对象，每次租用完整覆盖。
            _projectileProperties.Clear();
            _projectileProperties.SetTexture("_MainTex", shieldImpact.texture);
            _projectileProperties.SetVector(SpriteUvId, DataUtility.GetOuterUV(shieldImpact));
            _projectileProperties.SetFloat("_Pattern", (int)kind + 1);
            _projectileProperties.SetFloat("_PatternCells", radius * 2 + 1);
            _projectileProperties.SetFloat("_Progress", 0f);
            spriteRenderer.SetPropertyBlock(_projectileProperties);
            float width = Consts.CellSize;
            float height = Consts.CellSize;
            if (kind == PieceKind.MageFire)
            {
                width = height = (radius * 2 + 1) * Consts.CellSize;
            }
            else if (kind == PieceKind.Crossbow || kind == PieceKind.MageWater)
            {
                width = Mathf.Max(Consts.CellSize, Vector3.Distance(source, center));
                height = Consts.CellSize * .5f;
                transform.position = (source + center) * .5f;
                transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(center.y - source.y, center.x - source.x) * Mathf.Rad2Deg);
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
            // 纯表现秒数不进数值表；寿命保险与Tween结束都走同一Finish，回池先Kill(false)。
            _projectileExpiresAt = Time.time + .65f;
            _animation = DOTween.To(_readProgress, _writeProgress, 1f, .5f).SetEase(Ease.Linear).OnComplete(_finishAnimation);
        }
    }
}
