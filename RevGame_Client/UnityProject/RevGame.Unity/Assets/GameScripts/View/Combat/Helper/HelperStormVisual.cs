using RevGame.Core;
using UnityEngine;
using UnityEngine.Sprites;

namespace RevGame.View
{
    /// <summary>
    /// 池化攻击上的序列帧组件，生命周期完全由HelperAttackView管理，不独立计时、不结算伤害。
    /// 导弹/水母/爆炸共用图集兼容材质，只有帧UV通过属性块传入，避免每发克隆材质。
    /// </summary>
    public sealed class HelperStormVisual : MonoBehaviour
    {
        [SerializeField] private SpriteRenderer body;
        [SerializeField] private Sprite[] missileFrames, jellyFrames, explosionFrames;
        [Tooltip("导弹命中的蘑菇云Quad，复用共享网格和材质；水母不显示蘑菇云。")]
        [SerializeField] private MeshRenderer mushroom;
        [Tooltip("纯美术缩放，不改变服务中的命中半径或伤害。")]
        [SerializeField] private float missileScale = 1.35f, jellyScale = 1.3f;
        // Unity反序列化构造函数可能在加载线程运行；原生属性块必须等主线程Begin时才创建。
        private MaterialPropertyBlock _block;
        private MaterialPropertyBlock _mushroomBlock;
        private HelperWeapon _weapon;
        private int _lane;
        private float _impactAt;
        private Vector3 _impactPosition;
        public int FrameIndex
        {
            get; private set;
        }
        public bool IsExplosion
        {
            get; private set;
        }
        public bool Visible => body != null && body.enabled && body.isVisible;
        /// <summary>开发态读取实际爆炸状态，不能用“弹道播放完成”代替命中后弹体已消失的检查。</summary>
        public bool ProjectileVisible => body != null && body.enabled && !IsExplosion;
        public bool MushroomVisible => mushroom != null && mushroom.enabled;
        public bool MushroomOnCamera => MushroomVisible && mushroom.isVisible;
        public float BodyScale => body == null ? 0 : body.transform.localScale.x;
        public HelperWeapon Weapon => _weapon;
        private static readonly int MainTex = Shader.PropertyToID("_MainTex"), FrameUV = Shader.PropertyToID("_FrameUV");
        private static readonly int EffectKind = Shader.PropertyToID("_EffectKind"), Progress = Shader.PropertyToID("_Progress");

        public void Begin(HelperWeapon weapon, int lane)
        {
            if (_block == null)
            {
                _block = new MaterialPropertyBlock();
            }
            if (_mushroomBlock == null)
            {
                _mushroomBlock = new MaterialPropertyBlock();
            }
            _weapon = weapon;
            _lane = lane;
            IsExplosion = false;
            FrameIndex = -1;
            _impactAt = -1f;

            if (mushroom != null)
            {
                mushroom.enabled = false;
            }
            body.enabled = true;
        }
        /// <summary>
        /// progress是0～1的纯表现时间。接触目标的可见身体后立刻隐藏弹体并切爆炸帧，水母不会黏在目标上。
        /// 真实伤害已经由服务提交；这里的距离检测只决定换画面，绝不再次扣血或选择第二个受害者。
        /// </summary>
        public void Present(Vector3 start, Vector3 end, float progress, float contactRadius = .32f)
        {
            float t = Mathf.Clamp01(progress / .6f);
            Vector3 delta = end - start;
            Vector3 bend = Vector3.Cross(delta.normalized, Vector3.forward) * ((_lane % 7 - 3) * .3f);
            Vector3 next = Vector3.Lerp(start, end, t) + bend * Mathf.Sin(t * Mathf.PI);

            if (!IsExplosion && (progress >= .6f || (next - end).sqrMagnitude <= contactRadius * contactRadius))
            {
                IsExplosion = true;
                _impactAt = progress;
                _impactPosition = end;
                // 只通知声音表现。每条攻击只会跨过这道门一次，即使目标被回池也不会反复爆炸。
                GameAudio.PlayHelperImpact(_weapon);
            }
            body.transform.position = IsExplosion ? _impactPosition : next;
            body.transform.rotation = IsExplosion || _weapon == HelperWeapon.Jellyfish ? Quaternion.identity
                : Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90);
            Sprite[] frames = IsExplosion ? explosionFrames : _weapon == HelperWeapon.Missile ? missileFrames : jellyFrames;

            if (frames == null || frames.Length == 0)
            {
                return;
            }
            float burst = IsExplosion ? Mathf.Clamp01((progress - _impactAt) / Mathf.Max(.05f, 1 - _impactAt)) : 0;
            FrameIndex = IsExplosion ? Mathf.Min(frames.Length - 1, (int)(burst * frames.Length))
                : (int)(progress * 14 + _lane) % frames.Length;
            Sprite sprite = frames[FrameIndex];

            if (sprite == null || sprite.texture == null)
            {
                // 冷AB下Sprite引用可能先于图集像素到达；缺图时隐藏这一帧，不能把默认白纹理画成方块。
                body.enabled = false;
                return;
            }
            body.enabled = true;

            if (body.sprite != sprite)
            {
                body.sprite = sprite;
                Vector4 uv = DataUtility.GetOuterUV(sprite);
                _block.SetTexture(MainTex, sprite.texture);
                _block.SetVector(FrameUV, new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
                _block.SetFloat(EffectKind, IsExplosion && _weapon == HelperWeapon.Jellyfish ? 2 : IsExplosion ? 1 : 0);
                body.SetPropertyBlock(_block);
            }
            // 美术尺寸是表现参数，真实爆炸半径与激光宽度在服务读取Excel并独立检测。
            float size = IsExplosion ? Mathf.Lerp(.85f, _weapon == HelperWeapon.Missile ? 1.8f : 1.45f, burst)
                : _weapon == HelperWeapon.Missile ? missileScale : jellyScale;
            body.transform.localScale = Vector3.one * size;
            body.color = new Color(1, 1, 1, IsExplosion ? 1 - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(.35f, 1f, burst)) : 1);

            if (mushroom != null)
            {
                mushroom.enabled = IsExplosion && _weapon == HelperWeapon.Missile;

                if (mushroom.enabled)
                {
                    mushroom.transform.position = _impactPosition + Vector3.up * .9f;
                    mushroom.transform.localScale = new Vector3(3.2f, 3.8f, 1f);
                    _mushroomBlock.SetFloat(Progress, burst);
                    mushroom.SetPropertyBlock(_mushroomBlock);
                }
            }
        }
        public void Clear()
        {
            body.enabled = false;
            body.sprite = null;
            body.SetPropertyBlock(null);

            if (mushroom != null)
            {
                mushroom.enabled = false;
                mushroom.SetPropertyBlock(null);
            }
            IsExplosion = false;
            FrameIndex = -1;
            _impactAt = -1f;
        }
    }
}
