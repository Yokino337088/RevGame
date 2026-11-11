using RevGame.Pvp.Duel;
using Revolution;
using UnityEngine;
using UnityEngine.UI;

namespace RevGame.View
{
    // 决斗场景拥有四个复用特效层。动作开始与命中分别消费已确认事件，回滚不重播音效和粒子。
    public sealed class DuelStrikeEffects : MonoBehaviour
    {
        // 黑方的出招图层，诊断读取 enabled 确认它实际播放。
        public Image First;
        // 白方的出招图层。
        public Image Second;
        // 黑方打出的命中特效，定位到白方的位置。
        public Image FirstImpact;
        // 白方打出的命中特效，定位到黑方的位置。
        public Image SecondImpact;
        // 按动作号 1～6 排列的起手特效，由场景配方引用同一图集。
        public Sprite[] CastSprites;
        // 按动作号 1～6 排列的命中形状，可与起手形状复用贴图。
        public Sprite[] ImpactSprites;
        // 动作 1～6 的不同原声音效。由内置决斗场景持有。
        public AudioClip[] CastSounds;
        // 普攻、技能、大招的三种命中音色。
        public AudioClip[] ImpactSounds;
        // 白方鸦鸦独占的六个起手图形，按原生动作编号排列。
        public Sprite[] YayaCastSprites;
        // 白方鸦鸦的六个命中图形，在受击者位置播放。
        public Sprite[] YayaImpactSprites;
        // 白方每一段普攻和每种法术的短音效。
        public AudioClip[] YayaCastSounds;
        // 白方普攻、技能和大招命中时的不同音色。
        public AudioClip[] YayaImpactSounds;
        // 场景唯一的二维声源。退出场景即释放，音量读取 Revolution 门面。
        public AudioSource Speaker;

        // 按座位存上一次已播放的动作起始帧。每席每帧最多开始一次动作。
        private readonly int[] _lastStart = { -1, -1 };
        // 两个出招图层各自的播放时长和缩放。
        private readonly Effect[] _casts = new Effect[2];
        // 两个命中图层可与出招同时播放。
        private readonly Effect[] _impacts = new Effect[2];
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        // 已触发的动作类型位，便于验收每种动作都真正使用了不同的序列。
        internal int CastMaskForCheck { get; private set; }
        // 实际提交播放的动作音效类型位。
        internal int SoundMaskForCheck { get; private set; }
        // 已展示的命中数量。
        internal int ImpactsForCheck { get; private set; }
        // 白方实际落屏的动作、声音和命中单独计数，黑方同时出招不会污染验收。
        internal int YayaCastMaskForCheck { get; private set; }
        internal int YayaSoundMaskForCheck { get; private set; }
        internal int YayaImpactsForCheck { get; private set; }
#endif

        // 单个表现事件的播放参数，值类型随图层复用。
        private struct Effect
        {
            // 当前事件开始时的本地非缩放秒数。
            public float Start;
            // 总播放时长，单位秒。
            public float Duration;
            // 放大前的宽高，单位为擂台画布局部像素。
            public Vector2 Size;
            // 出招锚点。坠枪拖尾从此处开始向地面拉出。
            public Vector2 Origin;
            // 乌鸦或坠枪从起点到终点的画布偏移，单位像素。只改变渲染轨迹。
            public Vector2 Travel;
            // 乌鸦飞到终点所需秒数，以已确认的命中年龄对齐。
            public float TravelSeconds;
            // 飞行弧线的最高额外偏移，单位画布像素。
            public float Arc;
            // 色彩和初始透明度。
            public Color Tint;
        }

        // 消费一次权威起手。角色在恢复期切入新动作时，起始帧自然区分前后两次攻击。
        public void Observe(int seat, int action, int startFrame, int facing, Vector2 position, Vector2 target, float reach)
        {
            if (action <= 0 || startFrame <= _lastStart[seat])
            {
                return;
            }
            _lastStart[seat] = startFrame;
            bool yaya = seat == 1 && YayaCastSprites != null && YayaCastSprites.Length == 6;
            if (yaya)
            {
                CastYaya(action, facing, position, target, reach);
            }
            else
            {
                CastEmber(seat, action, facing, position, reach);
            }
            bool audible = Play((yaya ? YayaCastSounds : CastSounds)[action - 1], .78f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            int bit = 1 << (action - 1);
            CastMaskForCheck |= bit;
            if (yaya)
            {
                YayaCastMaskForCheck |= bit;
            }
            if (audible)
            {
                SoundMaskForCheck |= bit;
                if (yaya)
                {
                    YayaSoundMaskForCheck |= bit;
                }
            }
#endif
        }

        // 红枪的横向枪芒按权威距离展开。坠枪用命中年龄作为落地时刻，贴地的冲击由命中层负责。
        private void CastEmber(int seat, int action, int facing, Vector2 position, float reach)
        {
            float hitSeconds = DuelConfig.ActionHitFrame(action) * DuelConfig.FrameMs / 1000f;
            Vector2 size = action switch
            {
                2 => new Vector2(reach * 1.12f, 235),
                3 => new Vector2(reach * 1.36f, 475),
                4 => new Vector2(reach * .96f, 510),
                _ => new Vector2(reach * 1.16f, 232)
            };
            Vector2 origin = position + new Vector2(facing * reach * .47f, action == 4 ? 98 : 14);
            ref Effect effect = ref _casts[seat];
            Begin(seat == 0 ? First : Second, ref effect, CastSprites[action - 1], origin, size,
                hitSeconds + (action == 3 ? .27f : .12f), Color.white, facing);
            if (action == 4)
            {
                effect.Travel = new Vector2(facing * reach * .12f, -145 - origin.y);
                effect.TravelSeconds = hitSeconds;
            }
        }

        // 将鸦袭、掠羽和鸦宴绘成不同尺寸和方向。飞行距离只用于画面，受击由权威帧触发。
        private void CastYaya(int action, int facing, Vector2 position, Vector2 target, float reach)
        {
            bool bird = action == 2 || action == 4;
            float hitSeconds = DuelConfig.ActionHitFrame(action) * DuelConfig.FrameMs / 1000f;
            float height = action switch { 1 => -90, 4 => 170, 5 => 15, 6 => 70, _ => 55 };
            Vector2 origin = position + new Vector2(facing * (bird ? 65 : 95), height);
            Vector2 size = action switch
            {
                2 => new Vector2(300, 214),
                3 => new Vector2(575, 480),
                4 => new Vector2(310, 265),
                1 => new Vector2(230, 135),
                6 => new Vector2(310, 230),
                _ => new Vector2(245, 190)
            };
            Begin(Second, ref _casts[1], YayaCastSprites[action - 1], origin, size,
                hitSeconds + (action == 3 ? .28f : .14f), Color.white, facing);
            if (bird)
            {
                // 以起手帧的对手位置定方向和长度。实际命中点另读该帧受击者坐标。
                float forward = Mathf.Clamp((target.x - position.x) * facing, 90, reach);
                float endHeight = Mathf.Clamp(target.y - position.y, -75, 90) + (action == 4 ? -85 : 20);
                _casts[1].Travel = position + new Vector2(forward * facing, endHeight) - origin;
                _casts[1].TravelSeconds = hitSeconds;
                _casts[1].Arc = action == 4 ? 45 : 26;
            }
        }

        // 命中发生在逻辑命中帧，特效落在受击角色身上，伤害已经由原生结算。
        public void Hit(int seat, int action, int facing, Vector2 target)
        {
            int tier = Tier(action);
            bool yaya = seat == 1 && YayaImpactSprites != null && YayaImpactSprites.Length == 6;
            Vector2 shape = action == 4 ? new Vector2(580, 320) : Vector2.one * (tier == 2 ? 750 : tier == 1 ? 405 : 250);
            // 白方低踢的受击闪光贴近小腿，其它命中仍从权威受击者取锚点。
            float impactHeight = yaya && action == 1 ? -90 : action == 4 ? -145 : 18;
            Begin(seat == 0 ? FirstImpact : SecondImpact, ref _impacts[seat], (yaya ? YayaImpactSprites : ImpactSprites)[action - 1],
                target + new Vector2(0, impactHeight), shape,
                tier == 2 ? .48f : .22f, Color.white, facing);
            Play((yaya ? YayaImpactSounds : ImpactSounds)[tier], tier == 2 ? .92f : .68f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            ImpactsForCheck++;
            if (yaya)
            {
                YayaImpactsForCheck++;
            }
#endif
        }

        // 将六种动作映射为普攻、技能、大招三级强度。普攻段位共享冲击级别，起手仍各有音色。
        private static int Tier(int action)
        {
            return action == 3 ? 2 : action == 2 || action == 4 ? 1 : 0;
        }

        // 给已有图层换形状、定位和朝向，预置引用避免每帧查找资源或实例化材质。
        private static void Begin(Image layer, ref Effect effect, Sprite sprite, Vector2 position, Vector2 size, float seconds, Color tint, int facing)
        {
            effect = new Effect { Start = Time.unscaledTime, Duration = seconds, Size = size, Origin = position, Tint = tint };
            layer.sprite = sprite;
            layer.color = tint;
            layer.rectTransform.anchoredPosition = position;
            layer.rectTransform.sizeDelta = size;
            layer.rectTransform.localScale = new Vector3(facing < 0 ? -1 : 1, 1, 1);
            layer.enabled = true;
        }

        // 内置场景的短音效直接从已序列化片段播放，统一使用项目音量与静音开关。
        private bool Play(AudioClip clip, float volume)
        {
            if (!RevSound.Enabled || RevSound.Mute || clip == null)
            {
                return false;
            }
            Speaker.PlayOneShot(clip, volume * RevSound.MasterVolume * RevSound.GetVolume(RevSoundKind.Sfx));
            return true;
        }

        // 渲染时间只推进特效缩放和淡出。两席命中可同帧发生，分别用自己的图层。
        private void Update()
        {
            float now = Time.unscaledTime;
            Paint(First, _casts[0], now);
            Paint(Second, _casts[1], now);
            Paint(FirstImpact, _impacts[0], now);
            Paint(SecondImpact, _impacts[1], now);
        }

        // 早段展开，后段淡出。结束时关闭组件，避免绘制透明全屏图层。
        private static void Paint(Image layer, Effect effect, float now)
        {
            if (!layer.enabled)
            {
                return;
            }
            float progress = (now - effect.Start) / effect.Duration;
            if (progress >= 1)
            {
                layer.enabled = false;
                return;
            }
            float grow = 1 - Mathf.Pow(1 - progress, 3);
            layer.rectTransform.sizeDelta = effect.Size * (.76f + grow * .47f);
            Vector2 position = effect.Origin;
            if (effect.TravelSeconds > 0)
            {
                float flight = Mathf.Clamp01((now - effect.Start) / effect.TravelSeconds);
                position += effect.Travel * flight + Vector2.up * (4 * effect.Arc * flight * (1 - flight));
            }
            layer.rectTransform.anchoredPosition = position;
            Color tint = effect.Tint;
            tint.a *= 1 - progress * progress;
            layer.color = tint;
        }
    }
}
