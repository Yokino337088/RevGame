// RevSound.cs —— ★ 小白从这里开始（一行播放音效）
//
// ┌─ 2D 还是 3D？先记这一句 ─────────────────────────────────────────────────────────────┐
// │ 2D = 直接播        RevSound.Play("ui_click");                    界面音 / 语音 / BGM   │
// │ 3D = 指定挂在哪    RevSound.PlayOn("cast", enemy.gameObject);     挂到物体上（跟着它） │
// │                   RevSound.PlayAt("boom", hitPoint);             挂到世界坐标点（不跟随）│
// └──────────────────────────────────────────────────────────────────────────────────────┘
// 2D：不会因为距离变小声，永远听得清 —— 不需要（也不该）指定位置。
// 3D：有距离衰减，必须告诉框架"声音从哪发出"：挂在物体上（跟着物体走、物体没了声音就停）或挂在世界某一点。
//
// 【接下来看哪】（只有前两个是你必须看的）
//   ① 本文件                    一行播放 / 音量 / 开关 / 预加载 / 两个事件
//   ② RevSoundKind.cs           四类声音（Ui / Sfx / Voice / Bgm）各自的默认行为
//   ③ RevSoundHandle.cs         句柄：Stop 与"过期句柄不误伤新声音"
//   ④ RevSoundScope.cs          using 一块：退出时把这一块播的声音全停掉
//   ⑤ Implementation\RevSoundCatalog.cs 音效表（想让代码只写"逻辑名"时看；不用就不用读）
//   ⑥ Implementation\ 其余 与 Support\      内核与宿主驱动，不用读
//
// 【三条铁律】
//   ① 音效名 = 相对音效根目录的路径（可带子目录）：`Play("UI/click")` → `<根目录>/Audio/Sfx/UI/click`
//      想让"逻辑名 ≠ 磁盘路径"就用音效表：`RevSound.Register("ui_click", "UI/Button/click")`
//   ② 不提供任何查询：要感知"播完了/没播出去"，订阅 VoiceFinished / Failed 事件自己记
//   ③ 框架自身不打日志：失败一律通过 Failed 事件上报（等框架日志系统出来后再统一接）
//
// 【异步是常态】首次播放时资源还没加载好 → 立即返回句柄，加载完自动播（业务完全不用管异步）；
//   想零延迟就提前 RevSound.Preload(...)。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 音效系统唯一入口：一行播放（2D / 3D）、统一音量、两个观察事件。
    /// <para>门面是静态的（业务调用最省事），但内核 <see cref="RevSoundCore"/> 是实例（可多实例、可脱离门面单测）。</para>
    /// </summary>
    public static class RevSound
    {
        // ============================================================
        // 音频放哪（不用配置：目录是项目级事实，不是业务开关）
        //   音效：Assets/GameRes/Audio/Sfx/<名字>   ←→ 生成的 RevResPath.Audio_Sfx
        //   BGM ：Assets/GameRes/Audio/Bgm/<名字>   ←→ 生成的 RevResPath.Audio_Bgm
        //   路径只写在资源层一处（Implementation\RevSoundAssets.cs）；上层 API 只认"名字"。
        //   （音频放进 GameRes 后，Unity 会自动生成对应的 RevResPath 常量，
        //     业务侧要显式加载时直接用它，享受编译期保护。）
        // ============================================================

        // ============================================================
        // 2D 播放（直接播：跟着听者，不会因为距离变小声）
        // ============================================================

        /// <summary>
        /// 播放一个 <b>2D 音效</b>：直接播就行，不用指定位置 —— 界面音、提示音、语音、BGM 都用它。
        /// <code>RevSound.Play("ui_click");</code>
        /// </summary>
        /// <param name="name">音效名（相对音效根目录的路径，可带子目录；登记过音效表的会翻成表里的路径）</param>
        /// <param name="volume">音量（null = 用音效表 / 分类默认音量）</param>
        /// <param name="kind">分类（null = 用音效表 / 默认 Sfx）</param>
        /// <returns>句柄；被拒绝 / 系统关闭时是 <see cref="RevSoundHandle.Empty"/></returns>
        public static RevSoundHandle Play(string name, float? volume = null, RevSoundKind? kind = null)
            => Core.Play(name, kind, volume, position: null, target: null, is3D: false, loop: null);

        // ============================================================
        // 3D 播放（必须指定"挂在哪"：技能、打击、脚步、环境声）
        // ============================================================

        /// <summary>
        /// 播放一个 <b>3D 音效</b>并<b>挂到指定物体上</b>：声音从这个物体身上发出、跟着它移动；
        /// 物体被销毁（或隐藏）时声音自动收掉 —— 业务不用判空、也不用手动停。
        /// <code>RevSound.PlayOn("skill_cast", enemy.gameObject);</code>
        /// </summary>
        /// <param name="target">挂到哪个物体上（通常是角色 / 特效 / 挂点）</param>
        /// <param name="loop">是否循环（null = 用音效表 / 分类默认值）</param>
        public static RevSoundHandle PlayOn(string name, GameObject target, float? volume = null, bool? loop = null,
                                            RevSoundKind? kind = null)
            => PlayOn(name, target != null ? target.transform : null, volume, loop, kind);

        /// <inheritdoc cref="PlayOn(string, GameObject, float?, bool?, RevSoundKind?)"/>
        public static RevSoundHandle PlayOn(string name, Transform target, float? volume = null, bool? loop = null,
                                            RevSoundKind? kind = null)
            => Core.Play(name, kind, volume, position: null, target: target, is3D: true, loop: loop);

        /// <summary>
        /// 播放一个 <b>3D 音效</b>并<b>挂在世界坐标的某一点</b>（不跟随任何物体）—— 爆炸、落地、脚步点播。
        /// <code>RevSound.PlayAt("explosion", hitPoint);</code>
        /// </summary>
        public static RevSoundHandle PlayAt(string name, Vector3 position, float? volume = null,
                                            RevSoundKind? kind = null)
            => Core.Play(name, kind, volume, position, target: null, is3D: true, loop: null);

        // ============================================================
        // 背景音乐（2D、循环）
        // ============================================================

        /// <summary>
        /// 播放背景音乐（2D、循环；再调一次会替换上一首，可淡入淡出）。
        /// <code>RevSound.PlayBgm("login", fadeSeconds: 1f);</code>
        /// </summary>
        public static RevSoundHandle PlayBgm(string name, float fadeSeconds = 0f, float? volume = null)
            => Core.PlayBgm(name, volume, fadeSeconds);

        /// <summary>
        /// 播放 BGM 歌单（一首播完自动下一首，最后一首播完停止）。
        /// <code>RevSound.PlayBgmList("login_1", "login_2");</code>
        /// </summary>
        public static void PlayBgmList(params string[] names) => Core.PlayBgmList(names, 1f, 0f);

        /// <summary>停止背景音乐（可淡出；同时结束歌单）</summary>
        public static void StopBgm(float fadeSeconds = 0f) => Core.StopBgm(fadeSeconds);

        // ============================================================
        // 停止
        // ============================================================

        /// <summary>停止一次播放（等价于 <c>handle.Stop()</c>）</summary>
        public static void Stop(RevSoundHandle handle) => handle.Stop();

        /// <summary>停止所有正在播的声音（切场景/退出战斗时用；<paramref name="fadeSeconds"/> 可淡出）</summary>
        public static void StopAll(float fadeSeconds = 0f) => Core.StopAll(fadeSeconds);

        // ============================================================
        // 音效表（可选增强：让代码只写"逻辑名"，磁盘怎么整理都不用改调用点）
        // ============================================================

        /// <summary>
        /// 登记一条音效：逻辑名 → 真实资源路径 + 默认分类 / 音量 / 循环。
        /// <code>
        /// RevSound.Register("ui_click", "UI/Button/click", kind: RevSoundKind.Ui, volume: 0.8f);
        /// RevSound.Play("ui_click");        // 代码只认这个逻辑名
        /// </code>
        /// <para>同名重复登记 = <b>覆盖</b>（后登记的生效）；名字或路径为空返回 false。</para>
        /// <para>没登记的名字照样能直接播（原样当路径用）—— <b>音效表是可选增强，不是必须的开关</b>。</para>
        /// <para>解析规则：表里的值只补齐"调用点没写"的参数；调用点显式传的永远优先。</para>
        /// </summary>
        public static bool Register(string name, string path, RevSoundKind kind = RevSoundKind.Sfx,
                                    float volume = 1f, bool loop = false)
            => Core.Register(name, path, kind, volume, loop);

        /// <summary>注销一条音效（没登记过返回 false）</summary>
        public static bool Unregister(string name) => Core.Unregister(name);

        /// <summary>这个名字登记过吗</summary>
        public static bool IsRegistered(string name) => Core.IsRegistered(name);

        /// <summary>清空音效表（清空后所有名字都回到"直接当路径用"）</summary>
        public static void ClearCatalog() => Core.ClearCatalog();

        /// <summary>预加载音效表里的所有音效（每条按自己的分类选根目录；表为空则什么都不做）</summary>
        public static void PreloadAll() => Core.PreloadAll();

        // ============================================================
        // 预加载（想"点了就有声"就先加载；不调用也能播，只是首次会晚一点）
        // ============================================================

        /// <summary>预加载音效（异步；加载完常驻内存，直到 <see cref="Unload"/>）</summary>
        public static void Preload(params string[] names) => Core.Preload(names, bgm: false);

        /// <summary>预加载背景音乐</summary>
        public static void PreloadBgm(params string[] names) => Core.Preload(names, bgm: true);

        /// <summary>卸载一个音效（释放它占用的引用；下次播放会重新加载）</summary>
        public static void Unload(string name) => Core.Unload(name);

        /// <summary>卸载全部音效与音乐（回登录界面/大版本切换时用）</summary>
        public static void UnloadAll() => Core.UnloadAll();

        // ============================================================
        // 开关与音量（改一次立刻对正在播的声音生效）
        // ============================================================

        /// <summary>总开关：false 之后 <see cref="Play"/> 直接空操作（返回空句柄）</summary>
        public static bool Enabled
        {
            get => Core.Enabled;
            set => Core.Enabled = value;
        }

        /// <summary>静音：声音继续走，音量按 0 处理（不影响 Enabled 语义）</summary>
        public static bool Mute
        {
            get => Core.Mute;
            set => Core.Mute = value;
        }

        /// <summary>主音量（最终音量 = 单次音量 × 分类音量 × 主音量）</summary>
        public static float MasterVolume
        {
            get => Core.MasterVolume;
            set => Core.MasterVolume = value;
        }

        /// <summary>取某一类声音的音量（Ui / Sfx / Voice / Bgm 各自独立）</summary>
        public static float GetVolume(RevSoundKind kind) => Core.GetKindVolume(kind);

        /// <summary>设置某一类声音的音量（立刻对正在播的声音生效）</summary>
        public static void SetVolume(RevSoundKind kind, float volume) => Core.SetKindVolume(kind, volume);

        /// <summary>同时允许播放的最大声音数（默认 24；超出时淘汰最旧的一次性音效，拒绝循环音）</summary>
        public static int MaxVoices
        {
            get => Core.MaxVoices;
            set => Core.MaxVoices = value;
        }

        /// <summary>同一帧同名音效是否只播一次（默认开：防按钮连点、同帧重复触发）</summary>
        public static bool FrameDedupe
        {
            get => Core.FrameDedupe;
            set => Core.FrameDedupe = value;
        }

        /// <summary>3D 音效的距离衰减范围（默认 1~50 米；只有 3D 音效受影响）</summary>
        public static void Set3DRange(float minDistance, float maxDistance) => Core.Set3DRange(minDistance, maxDistance);

        /// <summary>业务策略：返回 false 这次播放就不发生（"该不该播"留在业务，内核不知道业务规则）</summary>
        public static IRevSoundPolicy Policy
        {
            get => Core.Policy;
            set => Core.Policy = value;
        }

        // ============================================================
        // 两个观察事件（框架不做查询、不打日志：想知道什么就在这里记）
        // ============================================================

        /// <summary>播放没能发生时触发（参数：音效名、原因）—— 订阅它就能发现"少了哪条音效、为什么"</summary>
        public static event Action<string, RevSoundErrorReason> Failed
        {
            add => Core.Failed += value;
            remove => Core.Failed -= value;
        }

        /// <summary>一条声音播完（或停止）时触发 —— 语音接台词、BGM 歌单轮播都靠它</summary>
        public static event Action<RevSoundHandle> VoiceFinished
        {
            add => Core.VoiceFinished += value;
            remove => Core.VoiceFinished -= value;
        }

        // ============================================================
        // 驱动（默认零配置：自动创建一个隐藏宿主每帧 Tick；手动宿主调 Tick 即可）
        // ============================================================

        /// <summary>
        /// 手动驱动（宿主自己每帧调它）。不调用也没关系：首次播放时会自动创建一个隐藏宿主接管 Tick。
        /// </summary>
        public static void Tick(float deltaTime) => Core.TickManual(deltaTime);

        /// <summary>内核实例（高级用法：想自己 new 一个独立内核时用；日常直接用上面的静态门面）</summary>
        public static RevSoundCore Core { get; } = new RevSoundCore();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession() => Core.ResetForNewSession();

        // ============================================================
        // 作用域（切界面 / 一局战斗：退出时把这一块播的声音全停掉）
        // ============================================================

        /// <summary>
        /// 开一个作用域：块内播的声音可以一键停掉（<c>using (var s = RevSound.OpenScope()) { s.Play(...); }</c>）。
        /// <para>对应王者"生命周期即作用域"：把"什么时候该收声"从散落各处的 Stop 变成一块边界。</para>
        /// </summary>
        public static RevSoundScope OpenScope() => new RevSoundScope(Core);
    }

    /// <summary>
    /// 播放策略（可选）：业务规则"该不该播"的门面钩子。
    /// <para>例如：战斗中不播大厅语音、低配机禁掉某些音效、某活动期间只放活动音。</para>
    /// <para>内核不知道任何业务规则（对应王者"策略可插拔、内核无知"），只在这里问一句。</para>
    /// </summary>
    public interface IRevSoundPolicy
    {
        /// <summary>返回 false：这次播放被拦下（会触发 <see cref="RevSound.Failed"/>，原因是 PolicyRejected）</summary>
        bool CanPlay(string name, RevSoundKind kind);
    }

    /// <summary>播放没能发生的原因（配合 <see cref="RevSound.Failed"/> 使用）。</summary>
    public enum RevSoundErrorReason
    {
        /// <summary>总开关关闭（Enabled = false）</summary>
        Disabled = 0,

        /// <summary>被业务策略拦下（<see cref="IRevSoundPolicy.CanPlay"/> 返回 false）</summary>
        PolicyRejected = 1,

        /// <summary>同一帧、同一个名字已经播过一次（防连点）</summary>
        Duplicated = 2,

        /// <summary>同时在播的声音已达上限（<see cref="RevSound.MaxVoices"/>）</summary>
        TooManyVoices = 3,

        /// <summary>资源加载失败（路径不对 / 包没打进去 / 平台不支持同步加载）</summary>
        LoadFailed = 4,

        /// <summary>3D 播放没给到"挂在哪"（<c>PlayOn</c> 的目标为 null：物体已经销毁了？）</summary>
        NoTarget = 5,
    }
}
