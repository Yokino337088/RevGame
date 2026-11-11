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
            // 竖向拖尾位移，0 表示固定锚点。
            public float Drop;
            // 色彩和初始透明度。
            public Color Tint;
        }

        // 消费一次权威起手。角色在恢复期切入新动作时，起始帧自然区分前后两次攻击。
        public void Observe(int seat, int action, int startFrame, int facing, Vector2 position)
        {
            if (action <= 0 || startFrame <= _lastStart[seat])
            {
                return;
            }
            _lastStart[seat] = startFrame;
            int tier = Tier(action);
            float size = tier == 2 ? 670 : tier == 1 ? 460 : 270;
            Image layer = seat == 0 ? First : Second;
            Color tint = Color.white;
            tint.a = tier == 0 ? .76f : .96f;
            Vector2 shape = action == 2 ? new Vector2(780, 345) : action == 4 ? new Vector2(350, 630) : Vector2.one * size;
            Vector2 offset = action == 4 ? new Vector2(facing * 48, 108) : new Vector2(facing * 120, 12);
            Begin(layer, ref _casts[seat], CastSprites[action - 1], position + offset,
                shape, tier == 2 ? .59f : .30f, tint, facing);
            _casts[seat].Drop = action == 4 ? 220 : 0;
            bool audible = Play(CastSounds[action - 1], .78f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            CastMaskForCheck |= 1 << (action - 1);
            if (audible)
            {
                SoundMaskForCheck |= 1 << (action - 1);
            }
#endif
        }

        // 命中发生在逻辑命中帧，特效落在受击角色身上，伤害已经由原生结算。
        public void Hit(int seat, int action, int facing, Vector2 target)
        {
            int tier = Tier(action);
            Vector2 shape = action == 4 ? new Vector2(580, 320) : Vector2.one * (tier == 2 ? 750 : tier == 1 ? 405 : 250);
            Begin(seat == 0 ? FirstImpact : SecondImpact, ref _impacts[seat], ImpactSprites[action - 1],
                target + new Vector2(0, action == 4 ? -145 : 18), shape,
                tier == 2 ? .48f : .22f, Color.white, facing);
            Play(ImpactSounds[tier], tier == 2 ? .92f : .68f);
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            ImpactsForCheck++;
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
            layer.rectTransform.anchoredPosition = effect.Origin + Vector2.down * (effect.Drop * progress * progress);
            Color tint = effect.Tint;
            tint.a *= 1 - progress * progress;
            layer.color = tint;
        }
    }
}
