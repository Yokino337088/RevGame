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
        // 已序列化的短效模板，随内置决斗场景加载，池中所有实例共享一份材质。
        public DuelArenaFxGraphic ParticlePrefab;
        // 两席起手及两席命中的四组网格参数，每组更新自己的出场进度。
        private DuelCastReveal[] _reveal;
        // 每场上限由画面的透明过绘预算决定。过载只跳过装饰火花。
        private readonly Particle[] _particles = new Particle[24];
        // 活跃粒子数；归还时用末位填空，逐帧无需扩容。
        private int _particleCount;
        // 上一次预热使用的模板存在时，离场才销毁对应池。
        private bool _poolReady;
        // 两套技能粒子沿用红枪和夜羽的原画色系。
        private static readonly Color EmberTint = new Color(1, .38f, .12f);
        private static readonly Color YayaTint = new Color(.51f, .33f, 1);

        // 角色配置在第 0 帧决定；默认值仍匹配原场景和编辑器预览。
        private bool _firstYaya;
        private bool _secondYaya = true;

        // 收集两种原有共享材质后才覆盖图层，镜像对局仍不需要克隆材质。
        internal void SetRoles(bool firstYaya, bool secondYaya)
        {
            Material ember = First.material;
            Material yaya = Second.material;
            _firstYaya = firstYaya;
            _secondYaya = secondYaya;
            First.material = FirstImpact.material = firstYaya ? yaya : ember;
            Second.material = SecondImpact.material = secondYaya ? yaya : ember;
        }

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
        // 已借出的拖尾、碎光及冲击环数量，读取此值可确认真实短效产生。
        internal int SpawnedParticlesForCheck { get; private set; }
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
            // 以横向枪芒或扩张羽环展开，中心移动的招式同步生成短拖尾。
            public bool Radial, Trails;
            // 开始飞行前的蓄力时间和下一次拖尾释放时间，单位表现秒。
            public float LaunchDelay, NextTrail;
            // 当前招式特效的轴向，决定颗粒拉伸和飞行方向。
            public int Facing;
            // 拖尾沿角色的颜色渐暗。
            public Color Energy;
        }

        // 一个池租约的播放状态。位置与速度都是场地设计像素，每帧不调用业务服务。
        private struct Particle
        {
            // 归还时使用 RevPool，退出场景会统一收回活跃项。
            public DuelArenaFxGraphic Graphic;
            // 归一化进度的时钟及寿命。
            public float Started, Lifetime;
            // 独立插值的起点、速度与尺寸。
            public Vector2 Origin, Velocity;
            // 颜色和图形类型用于更新共用 Shader 的顶点。
            public Color Tint;
            public DuelArenaFxGraphic.Shape Shape;
        }

        // 只在进场时找一次四个网格修改器，并预留一组可立即取用的粒子。
        internal void Prepare()
        {
            _reveal = new[]
            {
                First.GetComponent<DuelCastReveal>(), Second.GetComponent<DuelCastReveal>(),
                FirstImpact.GetComponent<DuelCastReveal>(), SecondImpact.GetComponent<DuelCastReveal>()
            };
            _poolReady = true;
            for (int index = 0; index < _particles.Length; index++)
            {
                _particles[index].Graphic = RevPool.Get<DuelArenaFxGraphic>(ParticlePrefab.gameObject, First.transform.parent);
            }
            for (int index = 0; index < _particles.Length; index++)
            {
                RevPool.Return(_particles[index].Graphic, 0);
                _particles[index] = default;
            }
        }

        // 消费一次权威起手。角色在恢复期切入新动作时，起始帧自然区分前后两次攻击。
        public void Observe(int seat, int action, int startFrame, int facing, Vector2 position, Vector2 target, float reach)
        {
            if (action <= 0 || startFrame <= _lastStart[seat])
            {
                return;
            }
            _lastStart[seat] = startFrame;
            bool yaya = (seat == 0 ? _firstYaya : _secondYaya) && YayaCastSprites != null && YayaCastSprites.Length == 6;
            if (yaya)
            {
                CastYaya(seat, action, facing, position, target, reach);
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

        // 绯焰贯枪沿出手方向冲出，赤阵在地面展开，坠枪从高处落到同一个判定轨迹。
        private void CastEmber(int seat, int action, int facing, Vector2 position, float reach)
        {
            float contact = ContactSeconds(action);
            bool plunge = action == 4;
            bool radial = action == 3;
            Vector2 origin = position + new Vector2(facing * (radial ? reach * .48f : 65), plunge ? 180 : 6);
            Vector2 size = action switch
            {
                2 => new Vector2(reach * .78f, 205),
                3 => new Vector2(reach * 1.14f, 420),
                4 => new Vector2(reach * .89f, 410),
                _ => new Vector2(reach * .87f, action == 6 ? 212 : 156)
            };
            ref Effect effect = ref _casts[seat];
            Begin(seat == 0 ? First : Second, _reveal[seat], ref effect, CastSprites[action - 1],
                origin, size, contact + (radial ? .34f : .16f), facing, radial, EmberTint);
            effect.Travel = radial ? Vector2.zero : plunge
                ? new Vector2(facing * reach * .38f, -142 - origin.y)
                : new Vector2(facing * Mathf.Max(0, reach * (action == 2 ? .69f : .56f) - 65), action == 6 ? 31 : 0);
            effect.LaunchDelay = contact * .19f;
            effect.TravelSeconds = contact;
            effect.Trails = !radial;
            if (radial)
            {
                SpawnRing(origin - new Vector2(0, 100), new Vector2(reach * 1.13f, 185), EmberTint, .47f);
            }
        }

        // 鸦袭和掠羽沿不同高度的弧线飞行，射程外也飞到配置上限；鸦宴按环形向外释放。
        private void CastYaya(int seat, int action, int facing, Vector2 position, Vector2 target, float reach)
        {
            bool bird = action is 2 or 4;
            bool radial = action == 3;
            float contact = ContactSeconds(action);
            float height = action switch { 1 => -90, 4 => 150, 5 => 15, 6 => 70, _ => 55 };
            Vector2 origin = position + new Vector2(facing * (radial ? reach * .42f : 65), height);
            Vector2 size = action switch
            {
                2 => new Vector2(268, 198),
                3 => new Vector2(reach * 1.20f, 445),
                4 => new Vector2(285, 222),
                _ => new Vector2(reach * .84f, action == 1 ? 135 : 200)
            };
            ref Effect effect = ref _casts[seat];
            Begin(seat == 0 ? First : Second, _reveal[seat], ref effect, YayaCastSprites[action - 1], origin, size,
                contact + (radial ? .32f : .17f), facing, radial, YayaTint);
            if (bird)
            {
                float forward = Mathf.Clamp((target.x - position.x) * facing, 130, reach);
                float endHeight = Mathf.Clamp(target.y - position.y, -75, 90) + (action == 4 ? -80 : 30);
                effect.Travel = position + new Vector2(forward * facing, endHeight) - origin;
                effect.Arc = action == 4 ? 85 : 52;
            }
            else if (!radial)
            {
                effect.Travel = new Vector2(facing * Mathf.Max(0, reach * .57f - 65), action == 6 ? 30 : 0);
            }
            effect.LaunchDelay = contact * .24f;
            effect.TravelSeconds = contact;
            effect.Trails = !radial;
            if (radial)
            {
                SpawnRing(origin, new Vector2(reach, 410), YayaTint, .44f);
            }
        }

        // 以原生命中年龄为基准计算图形抵达时刻，单位为表现秒。
        private static float ContactSeconds(int action)
        {
            return DuelConfig.ActionHitFrame(action) * DuelConfig.FrameMs / 1000f;
        }

        // 命中发生在逻辑命中帧，特效落在受击角色身上，伤害已经由原生结算。
        public void Hit(int seat, int action, int facing, Vector2 target)
        {
            int tier = Tier(action);
            bool yaya = (seat == 0 ? _firstYaya : _secondYaya) && YayaImpactSprites != null && YayaImpactSprites.Length == 6;
            Vector2 shape = action == 4 ? new Vector2(580, 320) : Vector2.one * (tier == 2 ? 750 : tier == 1 ? 405 : 250);
            // 白方低踢的受击闪光贴近小腿，其它命中仍从权威受击者取锚点。
            float impactHeight = yaya && action == 1 ? -90 : action == 4 ? -145 : 18;
            Vector2 origin = target + new Vector2(0, impactHeight);
            Color tint = yaya ? YayaTint : EmberTint;
            Begin(seat == 0 ? FirstImpact : SecondImpact, _reveal[seat + 2], ref _impacts[seat],
                (yaya ? YayaImpactSprites : ImpactSprites)[action - 1], origin, shape, tier == 2 ? .48f : .25f, facing, true, tint);
            SpawnRing(origin, Vector2.one * (tier == 2 ? 470 : 260), tint, tier == 2 ? .39f : .25f);
            // 碎光沿离开角色的方向散开，不读取随机数或碰撞组件。
            int count = tier == 2 ? 9 : tier == 1 ? 7 : 4;
            for (int index = 0; index < count; index++)
            {
                float angle = Mathf.Lerp(-.88f, .95f, index / (float)(count - 1));
                Vector2 direction = new Vector2(facing * Mathf.Cos(angle), Mathf.Sin(angle));
                SpawnParticle(origin, direction * (440 + index * 39), new Vector2(54 + index * 6, 24), tint,
                    .19f + .017f * index, DuelArenaFxGraphic.Shape.Streak);
            }
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

        // 起手从小范围和透明状态开始，避免第一帧直接贴出一整张效果图。
        private static void Begin(Image layer, DuelCastReveal reveal, ref Effect effect, Sprite sprite, Vector2 position, Vector2 size, float seconds, int facing, bool radial, Color energy)
        {
            effect = new Effect
            {
                Start = Time.unscaledTime, Duration = seconds, Size = size, Origin = position,
                Tint = Color.white, Radial = radial, Facing = facing, Energy = energy
            };
            layer.sprite = sprite;
            layer.color = new Color(1, 1, 1, 0);
            layer.rectTransform.anchoredPosition = position;
            layer.rectTransform.sizeDelta = size * .34f;
            layer.rectTransform.localScale = new Vector3(facing < 0 ? -1 : 1, 1, 1);
            layer.enabled = true;
            reveal.Paint(0, radial);
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

        // 每帧推进独立的起手/命中层与粒子租约。逻辑事件只在确认帧进入 Observe 和 Hit。
        private void Update()
        {
            if (_reveal == null)
            {
                return;
            }
            float now = Time.unscaledTime;
            Paint(First, _reveal[0], ref _casts[0], now);
            Paint(Second, _reveal[1], ref _casts[1], now);
            Paint(FirstImpact, _reveal[2], ref _impacts[0], now);
            Paint(SecondImpact, _reveal[3], ref _impacts[1], now);
            for (int index = _particleCount - 1; index >= 0; index--)
            {
                ref Particle particle = ref _particles[index];
                float age = now - particle.Started;
                if (age >= particle.Lifetime)
                {
                    ReturnParticle(index);
                    continue;
                }
                float phase = Mathf.Clamp01(age / particle.Lifetime);
                Vector2 offset = particle.Velocity * age * (1 - .35f * phase);
                particle.Graphic.rectTransform.anchoredPosition = particle.Origin + offset;
                Color tint = particle.Tint;
                tint.a *= 1 - phase * phase;
                particle.Graphic.color = tint;
                particle.Graphic.Paint(particle.Shape, phase);
            }
        }

        // 蓄力、加速和收尾各有曲线，拖尾只随图形飞行，对权威命中没有副作用。
        private void Paint(Image layer, DuelCastReveal reveal, ref Effect effect, float now)
        {
            if (!layer.enabled)
            {
                return;
            }
            float age = now - effect.Start;
            float progress = Mathf.Clamp01(age / effect.Duration);
            if (progress >= 1)
            {
                layer.enabled = false;
                return;
            }

            float appear = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0, Mathf.Min(.10f, effect.Duration * .30f), age));
            float dissipate = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.42f, 1, progress));
            float size = Mathf.Lerp(.34f, 1.08f, appear);
            float flight = effect.TravelSeconds > 0 ? Mathf.InverseLerp(effect.LaunchDelay, effect.TravelSeconds, age) : 0;
            float distance = flight * (2 - flight);
            Vector2 position = effect.Origin + effect.Travel * distance + Vector2.up * (4 * effect.Arc * flight * (1 - flight));
            layer.rectTransform.anchoredPosition = position;
            layer.rectTransform.sizeDelta = effect.Size * size;
            Color tint = effect.Tint;
            tint.a *= appear * dissipate;
            layer.color = tint;
            reveal.Paint(appear, effect.Radial);
            if (effect.Trails && flight > 0 && flight < 1 && age >= effect.NextTrail)
            {
                effect.NextTrail = age + .034f;
                Vector2 direction = effect.Travel.sqrMagnitude > 1 ? effect.Travel.normalized : Vector2.right * effect.Facing;
                Vector2 tail = position - direction * effect.Size.x * .24f;
                SpawnParticle(tail, direction * 168, new Vector2(effect.Size.x * .66f, 52), effect.Energy, .18f, DuelArenaFxGraphic.Shape.Streak);
            }
        }

        // 环形粒子不受朝向影响，使用相同的池和顶点参数。
        private void SpawnRing(Vector2 origin, Vector2 size, Color tint, float lifetime)
        {
            SpawnParticle(origin, Vector2.zero, size, tint, lifetime, DuelArenaFxGraphic.Shape.Ring);
        }

        // 只借用限量实例，峰值时优先保证主图形，额外火花可以丢弃。
        private void SpawnParticle(Vector2 origin, Vector2 velocity, Vector2 size, Color tint, float lifetime, DuelArenaFxGraphic.Shape shape)
        {
            if (!_poolReady || _particleCount == _particles.Length)
            {
                return;
            }
            var graphic = RevPool.Get<DuelArenaFxGraphic>(ParticlePrefab.gameObject, First.transform.parent);
            if (graphic == null)
            {
                return;
            }
            RectTransform rect = graphic.rectTransform;
            rect.anchoredPosition = origin;
            rect.sizeDelta = size;
            rect.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(velocity.y, velocity.x) * Mathf.Rad2Deg);
            graphic.color = tint;
            graphic.Paint(shape, 0);
            _particles[_particleCount++] = new Particle
            {
                Graphic = graphic, Origin = origin, Velocity = velocity,
                Tint = tint, Started = Time.unscaledTime, Lifetime = lifetime, Shape = shape
            };
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            SpawnedParticlesForCheck++;
#endif
        }

        // 交换末项后归还短效，组件退出时不会留下上一局的轨迹。
        private void ReturnParticle(int index)
        {
            RevPool.Return(_particles[index].Graphic, 0);
            _particles[index] = _particles[--_particleCount];
            _particles[_particleCount] = default;
        }

        // 场景卸载或组件停用时，先收回所有租约。
        private void OnDisable()
        {
            while (_particleCount > 0)
            {
                ReturnParticle(_particleCount - 1);
            }
        }

        // 这个池按场景持有的模板建键，离场销毁该池，其它系统的对象池不受影响。
        private void OnDestroy()
        {
            if (_poolReady && ParticlePrefab != null)
            {
                RevPool.DestroyPool(ParticlePrefab.gameObject);
            }
        }
    }
}
