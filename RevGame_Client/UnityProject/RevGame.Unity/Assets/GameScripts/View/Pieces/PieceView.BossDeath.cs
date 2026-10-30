// Boss胜利演出借用原视图和当前帧；规则层已结算，不重新扣血或占格，不创建第二颗Boss。
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class PieceView
    {
        private Material _bossRestMaterial;
        private SpriteRenderer _bossDeathAura;
        private MaterialPropertyBlock _bossDeathProperties;
        private bool _bossDeathActive;
        /// <summary>首次演出缓存附属渲染层；出生末尾还原其可见状态，死亡回池不重新点亮血条/底座。</summary>
        private SpriteRenderer[] _bossDecorations;
        private bool[] _bossDecorationEnabled;
        private bool _bossHeld;
        public bool IsKingVisual
        {
            get; private set;
        }
        public bool HasBossDeathVisual
        {
            get
            {
                return _bossDeathActive;
            }
        }
        /// <summary>致死瞬间保留当前帧和旧材质，停止移动/换帧；由棋盘持有直到演出结束后回池。</summary>
        public void HoldBossDefeat()
        {
            if (_bossHeld)
            {
                return;
            }
            _bossHeld = true;
            StopFormationMove();
            StopAttackPulse();
            StopBodyVisual();
            RevTimer.CancelAllOf(this);
            RevMono.RemoveAllOf(this);
            _moving = false;

            if (_bossDecorations == null)
            {
                _bossDecorations = GetComponentsInChildren<SpriteRenderer>(true);
                _bossDecorationEnabled = new bool[_bossDecorations.Length];
            }
            for (int index = 0; index < _bossDecorations.Length; index++)
            {
                SpriteRenderer renderer = _bossDecorations[index];

                if (renderer == null || renderer == bodyRenderer || renderer == _bossDeathAura)
                {
                    continue;
                }
                _bossDecorationEnabled[index] = renderer.enabled;
                renderer.enabled = false;
            }
        }
        /// <summary>图集UV随当前帧绑定，材质直接借章节共享资产，所有进度写参数块，不访问renderer.material。</summary>
        public void BeginBossDeath()
        {
            if (_bossDeathActive)
            {
                return;
            }
            if (_bossArt == null || _bossArt.DeathMaterial == null || _bossArt.DeathAuraMaterial == null || bodyRenderer.sprite == null)
            {
                throw new System.InvalidOperationException("Boss章节死亡材质未就绪");
            }
            // 图集租约加载和Sprite的引擎绑定分属两步；空贴图必须在开始前拒绝，不能留下半启动的材质状态。
            if (bodyRenderer.sprite.texture == null)
            {
                throw new System.InvalidOperationException("Boss帧图集像素尚未绑定");
            }
            // 棋盘在致死事实时已Hold；不要再次保存全false的附属层状态，出生也复用此停止入口。
            HoldBossDefeat();
            _bossRestMaterial = bodyRenderer.sharedMaterial;
            bodyRenderer.sharedMaterial = _bossArt.DeathMaterial;
            _bossDeathProperties ??= new MaterialPropertyBlock();
            _bossDeathActive = true;

            if (_bossDeathAura == null)
            {
                var obj = new GameObject("BossDeathAura");
                obj.transform.SetParent(transform, false);
                _bossDeathAura = obj.AddComponent<SpriteRenderer>();
            }
            _bossDeathAura.sprite = bodyRenderer.sprite;
            _bossDeathAura.sharedMaterial = _bossArt.DeathAuraMaterial;
            _bossDeathAura.sortingLayerID = bodyRenderer.sortingLayerID;
            _bossDeathAura.sortingOrder = bodyRenderer.sortingOrder + 1;
            _bossDeathAura.transform.localPosition = bodyRenderer.transform.localPosition;
            // 外层最多略超出身体；第五章雪花与身体同尺寸，由Shader控制22%透明度。
            float auraScale = _bossArt.Chapter == 5 ? 1f : 1.12f;

            // 第六章可在专属材质配置圣光范围；只在演出开始读一次，不克隆材质或改变Boss占格。
            if (_bossArt.DeathAuraMaterial.HasProperty("_AuraScale"))
            {
                auraScale = Mathf.Clamp(_bossArt.DeathAuraMaterial.GetFloat("_AuraScale"), 1f, 2.5f);
            }
            _bossDeathAura.transform.localScale = bodyRenderer.transform.localScale * auraScale;
            _bossDeathAura.enabled = true;
            SetBossDeathProgress(0);
        }
        public float BossDeathSeconds
        {
            get
            {
                return _bossArt == null ? 3.2f : _bossArt.DeathSeconds;
            }
        }
        public bool IsBossFrameTextureReady
        {
            get
            {
                return bodyRenderer != null && bodyRenderer.sprite != null && bodyRenderer.sprite.texture != null;
            }
        }
        /// <summary>出生直接复用死亡Shader，进度从1逆播到0，停用换帧避免同一进度的噪声跳帧。</summary>
        public void BeginBossBirth()
        {
            if (_bossDeathActive)
            {
                return;
            }
            BeginBossDeath();
            SetBossDeathProgress(1);
        }
        /// <summary>入场结束仍是活棋，恢复共享材质与计时监听；死亡则由回池清理，绝不能恢复死亡对象。</summary>
        public void FinishBossBirth()
        {
            if (!_bossDeathActive)
            {
                return;
            }
            bool king = IsKingVisual;
            ClearBossDeath();
            IsKingVisual = king;

            if (_bossDecorations != null)
            {
                for (int index = 0; index < _bossDecorations.Length; index++)
                {
                    SpriteRenderer renderer = _bossDecorations[index];

                    if (renderer != null && renderer != bodyRenderer && renderer != _bossDeathAura)
                    {
                        renderer.enabled = _bossDecorationEnabled[index];
                    }
                }
            }
            if (bodyRenderer != null && bodyRenderer.sprite != null)
            {
                Sprite frame = bodyRenderer.sprite;
                bodyRenderer.sprite = null;
                bodyRenderer.sprite = frame;
            }
            if (isActiveAndEnabled)
            {
                RevTimer.Every(tickInterval, OnTick, -1, RevTimeDomain.Scaled, this);
                RevMono.AddUpdate(AdvanceMove, owner: this);
            }
        }

        /// <summary>纯视觉进度0～1来自非缩放演出时间；body与外部光晕共享同一时间轴。</summary>
        public void SetBossDeathProgress(float progress)
        {
            if (!_bossDeathActive || bodyRenderer == null)
            {
                return;
            }
            Vector4 uv = UnityEngine.Sprites.DataUtility.GetOuterUV(bodyRenderer.sprite);
            _bossDeathProperties.Clear();
            _bossDeathProperties.SetFloat("_Progress", Mathf.Clamp01(progress));
            _bossDeathProperties.SetVector("_FrameUV", new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
            _bossDeathProperties.SetTexture("_MainTex", bodyRenderer.sprite.texture);
            bodyRenderer.SetPropertyBlock(_bossDeathProperties);
            _bossDeathAura.SetPropertyBlock(_bossDeathProperties);
        }
        /// <summary>正常结束与取消共用；回池恢复原材质，清掉动态帧和引用，后续普通棋子不带死亡Shader。</summary>
        private void ClearBossDeath()
        {
            if (_bossDeathActive && bodyRenderer != null)
            {
                bodyRenderer.sharedMaterial = _bossRestMaterial;
                bodyRenderer.SetPropertyBlock(null);
                // 清掉Boss参数块后无条件重新绑定同帧，让Unity重建Atlas采样；取消/出生末帧也不能白闪。
                Sprite frame = bodyRenderer.sprite;
                bodyRenderer.sprite = null;
                bodyRenderer.sprite = frame;
            }
            _bossDeathActive = false;
            _bossRestMaterial = null;
            _bossHeld = false;

            if (_bossDeathAura != null)
            {
                _bossDeathAura.enabled = false;
                _bossDeathAura.sprite = null;
                _bossDeathAura.sharedMaterial = null;
                _bossDeathAura.SetPropertyBlock(null);
            }
            IsKingVisual = false;
        }
    }
}
