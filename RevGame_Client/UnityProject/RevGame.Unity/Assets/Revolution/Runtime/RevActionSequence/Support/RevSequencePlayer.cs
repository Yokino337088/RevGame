// RevSequencePlayer.cs —— Unity 驱动组件 / 静态便捷入口（宿主适配层）
// 【用法】清单.Play(gameObject) 或 RevSequencePlayer.Default.Play(清单, source) —— 零配置：内部自动创建隐藏驱动者每帧 Tick。
// 【要点】场景里第二个「驱动全局引擎」的组件会被自动关闭并告警（否则序列速度翻倍、等待时间减半）；
//         驱动者被销毁（比如它在场景里、场景卸载了）时，下次访问 Default 会自动补一个。
// 【暂停】默认跟随 Time.timeScale（暂停时序列也停）；暂停界面里的演出请把 UseUnscaledTime 打开。

using UnityEngine;

namespace Revolution
{
    /// <summary>
    /// 动作序列驱动组件：把引擎挂到 Unity 生命周期上（Update 或 FixedUpdate）。
    /// <para>一般直接用 <c>清单.Play(gameObject)</c> 或 <c>RevSequencePlayer.Default</c> 即可，不需要手动往场景里放它。</para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RevSequencePlayer : MonoBehaviour
    {
        private static RevSequenceRunner _defaultRunner;        // 全局默认引擎
        private static RevSequencePlayer _defaultTicker;        // 正在驱动全局引擎的那个组件
        private static bool _warnedEditMode;

        [Tooltip("勾上 = 本组件驱动自己的引擎（随场景卸载回收）；不勾 = 驱动全局默认引擎")]
        [SerializeField] private bool _ownRunner;

        [Tooltip("勾上 = 在 FixedUpdate 驱动（与物理帧对齐）；不勾 = 在 Update 驱动（与渲染对齐）")]
        [SerializeField] private bool _tickInFixedUpdate;

        [Tooltip("勾上 = 用不受 Time.timeScale 影响的时间（暂停时序列照常跑，适合 UI 演出）")]
        [SerializeField] private bool _useUnscaledTime;

        private RevSequenceRunner _runner;      // _ownRunner 模式下自建

        /// <summary>
        /// 全局默认引擎：第一次访问时自动创建（并自动创建一个隐藏驱动组件）。
        /// <para>它不随场景卸载销毁 —— 这是"全局"的含义；需要清空请调 <c>RevSequencePlayer.Default.Clear()</c>。</para>
        /// </summary>
        public static RevSequenceRunner Default
        {
            get
            {
                _defaultRunner ??= new RevSequenceRunner();
                EnsureDefaultTicker();      // 每次都查：驱动者可能随场景被销毁了（查一次只是个 null 判断）
                return _defaultRunner;
            }
        }

        /// <summary>
        /// 全局默认引擎是否用不受 <c>Time.timeScale</c> 影响的时间（默认 false = 暂停时序列也停）。
        /// </summary>
        public static bool UseUnscaledTime { get; set; }

        /// <summary>本组件驱动的引擎（全局模式返回 <see cref="Default"/>）</summary>
        public RevSequenceRunner Runner => _ownRunner ? (_runner ??= new RevSequenceRunner()) : Default;

        // ── 静态便捷入口 ─────────────────

        /// <summary>用全局默认引擎播放一条序列（也可以直接写 <c>清单.Play(source)</c>）</summary>
        public static RevSequenceHandle Play(RevSequenceDefinition definition, object source = null)
            => Default.Play(definition, source);

        /// <summary>用全局默认引擎"构建并立刻播放"（适合一次性演出）</summary>
        public static RevSequenceHandle Play(RevSequenceBuilder builder, object source = null)
            => Default.Play(builder, source);

        /// <summary>用全局默认引擎播放并返回可 await 的任务（true = 正常跑完，false = 被取消）</summary>
        public static RevTask<bool> PlayAsync(RevSequenceDefinition definition, object source = null)
            => Default.PlayAsync(definition, source);

        // ── Unity 生命周期 ──────────────────────────────────────────

        private void Awake()
        {
            if (_ownRunner) return;     // 场景级引擎：无需和全局的那位抢

            // 全局模式：同一时刻只允许一个驱动者（重复驱动 = 所有序列每帧推进两次）
            if (_defaultTicker != null && _defaultTicker != this)
            {
                RevLog.Warn(
                    "[RevSequencePlayer] 场景里已存在一个驱动全局默认引擎的 RevSequencePlayer → 本组件已自动关闭。" +
                    "（两个驱动者会让所有序列同帧推进两次：等待时间减半、动画加速）", RevSequenceRunner.LogTag);

                enabled = false;
                return;
            }

            _defaultTicker = this;
        }

        private void Update()
        {
            if (!_tickInFixedUpdate) Runner.Tick(Unscaled ? Time.unscaledDeltaTime : Time.deltaTime);
        }

        private void FixedUpdate()
        {
            if (_tickInFixedUpdate) Runner.Tick(Unscaled ? Time.fixedUnscaledDeltaTime : Time.fixedDeltaTime);
        }

        private bool Unscaled => _ownRunner ? _useUnscaledTime : (_useUnscaledTime || UseUnscaledTime);

        private void OnDestroy()
        {
            if (_ownRunner)
            {
                // 场景级引擎：随宿主一起收掉（未跑完的序列会被取消）
                _runner?.Dispose();
                _runner = null;
                return;
            }

            if (_defaultTicker == this) _defaultTicker = null;
        }

        /// <summary>访问 Default 时自动补一个隐藏驱动者（运行期才创建，编辑期不污染场景）</summary>
        private static void EnsureDefaultTicker()
        {
            if (_defaultTicker != null) return;

            if (!Application.isPlaying)
            {
                // 编辑期（例如编辑器脚本里访问 Default）不创建物体，避免把隐藏物体写进场景
                if (!_warnedEditMode)
                {
                    _warnedEditMode = true;
                    RevLog.Warn("[RevSequencePlayer] 在非运行状态访问了 Default：未创建自动驱动者（运行时会自动创建）", RevSequenceRunner.LogTag);
                }
                return;
            }

            var host = new GameObject("[RevSequencePlayer]");
            _defaultTicker = host.AddComponent<RevSequencePlayer>();
            DontDestroyOnLoad(host);
        }

        /// <summary>
        /// 每次进入播放模式都复位静态状态（关闭"域重载"时静态字段会跨会话残留：
        /// 上一局的序列、指向已销毁对象的驱动者都会带进新的一局）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _defaultRunner = null;
            _defaultTicker = null;
            _warnedEditMode = false;
            UseUnscaledTime = false;
        }
    }
}
