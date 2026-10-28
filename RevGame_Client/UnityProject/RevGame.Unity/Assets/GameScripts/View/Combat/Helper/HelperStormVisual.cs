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
        // Unity反序列化构造函数可能在加载线程运行；原生属性块必须等主线程Begin时才创建。
        private MaterialPropertyBlock _block;
        private HelperWeapon _weapon;
        private int _lane;
        public int FrameIndex { get; private set; }
        public bool IsExplosion { get; private set; }
        public bool Visible => body != null && body.enabled && body.isVisible;
        public HelperWeapon Weapon => _weapon;
        private static readonly int MainTex = Shader.PropertyToID("_MainTex"), FrameUV = Shader.PropertyToID("_FrameUV");

        public void Begin(HelperWeapon weapon, int lane)
        {
            if (_block == null) { _block = new MaterialPropertyBlock(); }
            _weapon = weapon; _lane = lane; IsExplosion = false; FrameIndex = -1;
            body.enabled = true;
        }

        /// <summary>progress是0～1的纯表现时间。前六成飞行，后四成爆炸；这些时长不参与玩法伤害。</summary>
        public void Present(Vector3 start, Vector3 end, float progress)
        {
            float t = Mathf.Clamp01(progress / .6f);
            Vector3 delta = end - start;
            Vector3 bend = Vector3.Cross(delta.normalized, Vector3.forward) * ((_lane % 7 - 3) * .3f);
            body.transform.position = Vector3.Lerp(start, end, t) + bend * Mathf.Sin(t * Mathf.PI);
            IsExplosion = _weapon == HelperWeapon.Missile && progress >= .6f;
            body.transform.rotation = IsExplosion || _weapon == HelperWeapon.Jellyfish ? Quaternion.identity
                : Quaternion.Euler(0, 0, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg - 90);
            Sprite[] frames = IsExplosion ? explosionFrames : _weapon == HelperWeapon.Missile ? missileFrames : jellyFrames;
            if (frames == null || frames.Length == 0) { return; }
            FrameIndex = IsExplosion ? Mathf.Min(frames.Length - 1, (int)((progress - .6f) / .4f * frames.Length))
                : (int)(progress * 14 + _lane) % frames.Length;
            Sprite sprite = frames[FrameIndex];
            if (sprite == null) { return; }
            if (body.sprite != sprite)
            {
                body.sprite = sprite;
                Vector4 uv = DataUtility.GetOuterUV(sprite);
                _block.SetTexture(MainTex, sprite.texture);
                _block.SetVector(FrameUV, new Vector4(uv.x, uv.y, uv.z - uv.x, uv.w - uv.y));
                body.SetPropertyBlock(_block);
            }
            // 美术尺寸是表现参数，真实爆炸半径与激光宽度在服务读取Excel并独立检测。
            float size = IsExplosion ? Mathf.Lerp(1.2f, 2.8f, (progress - .6f) / .4f) : _weapon == HelperWeapon.Missile ? .9f : .65f;
            body.transform.localScale = Vector3.one * size;
            body.color = new Color(1, 1, 1, progress > .8f ? (1 - progress) / .2f : 1);
        }

        public void Clear()
        {
            body.enabled = false; body.sprite = null; body.SetPropertyBlock(null);
            IsExplosion = false; FrameIndex = -1;
        }
    }
}
