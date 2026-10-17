// ============================================================
// RevSceneProgressTracker.cs —— 进度换算（**纯 C#**，可脱机断言）
//
// 位置：Runtime\RevScene\Core\
//
// 【它解决三个真实问题】
//   ① **90% 平台期**：Unity 的 AsyncOperation.progress 在"场景激活前"最高只到 0.9，
//      直接喂给进度条，玩家看到的是"卡在 90% 一动不动，然后瞬间满"。
//      这里把 [0, 0.9] 归一化成 [0, 1]。
//   ② **回跳**：Unity 的采样值偶尔抖动，进度条一回退就很显眼 —— 这里保证**只增不减**。
//   ③ **闪一下**：小场景加载太快，加载界面刚出来就没了。传 minSeconds 就给进度条限速：
//      到点之前最多按时间节奏走，同时也才允许真正激活场景。
//
// 【为什么单独一个类、且不碰 UnityEngine】
//   时间以参数进来（Tick(raw, deltaTime)），所以它能在普通 .NET 工程里
//   脱机跑断言：只增不减 / 90% 换算 / 限速 / 何时允许激活，全部是可验证的纯函数行为。
// ============================================================
namespace Revolution
{
    /// <summary>
    /// 场景加载进度换算器：把 Unity 的原始进度变成"能直接喂进度条"的 0~1。
    /// <b>纯 C#</b>：不引用 UnityEngine，时间由调用方喂进来。
    /// </summary>
    public sealed class RevSceneProgressTracker
    {
        /// <summary>Unity 在"场景激活前"的进度上限（AsyncOperation.progress 到它就停住）。</summary>
        public const float UnityCap = 0.9f;

        private float _raw;         // Unity 最近一次给的原始进度
        private float _value;       // 归一化后的显示进度（单调不减）
        private float _elapsed;     // 已经过的秒数（由调用方累加，保持纯函数）
        private bool _completed;    // 是否已经收尾

        /// <summary>进度条最少要走多少秒（0 = 不限速，加载多快就多快）。</summary>
        public float MinSeconds { get; }

        /// <summary>构造一个换算器。</summary>
        /// <param name="minSeconds">进度条最短展示时长（秒）；≤ 0 表示不限速。</param>
        public RevSceneProgressTracker(float minSeconds = 0f)
        {
            MinSeconds = minSeconds > 0f ? minSeconds : 0f;
        }

        /// <summary>Unity 最近一次给的原始进度（0~1）。</summary>
        public float Raw => _raw;

        /// <summary>归一化后的显示进度（0~1，<b>单调不减</b>，可直接喂进度条）。</summary>
        public float Value => _value;

        /// <summary>已经统计到的耗时（秒）。</summary>
        public float Elapsed => _elapsed;

        /// <summary>是否已经收尾（调用过 <see cref="Complete"/>）。</summary>
        public bool IsCompleted => _completed;

        /// <summary>
        /// 现在能不能让 Unity 真正激活场景（= 显示进度已到 100%）。
        /// 没到 100% 就激活，进度条会"瞬间跳满" —— 那个跳变就是体验差的来源。
        /// </summary>
        public bool CanActivate => _completed || _value >= 1f;

        /// <summary>复位（同一个实例复用；一般每次加载 new 一个即可）。</summary>
        public void Reset()
        {
            _raw = 0f;
            _value = 0f;
            _elapsed = 0f;
            _completed = false;
        }

        /// <summary>
        /// 推进一帧。
        /// </summary>
        /// <param name="rawProgress">Unity 的 <c>AsyncOperation.progress</c>（0~1）</param>
        /// <param name="deltaTime">这一帧的时长（秒）——请传<b>不受时间缩放影响</b>的时长</param>
        /// <returns>归一化后的显示进度（0~1）</returns>
        public float Tick(float rawProgress, float deltaTime)
        {
            if (_completed) return _value;

            if (deltaTime > 0f) _elapsed += deltaTime;
            if (rawProgress > _raw) _raw = rawProgress;      // 原始值也只增不减
            if (_raw < 0f) _raw = 0f;

            // ① 归一化：0 ~ 0.9 → 0 ~ 1（超过上限一律按 1 算）
            float normalized = _raw >= UnityCap ? 1f : _raw / UnityCap;
            if (normalized < 0f) normalized = 0f;

            // ② 限速：显示值最多按"最短展示时长"的节奏走到 100%（小场景不会一闪而过）
            float paced = 1f;
            if (MinSeconds > 0f)
            {
                paced = _elapsed / MinSeconds;
                if (paced > 1f) paced = 1f;
            }

            float target = normalized < paced ? normalized : paced;

            // ③ 只增不减
            if (target > _value) _value = target;
            return _value;
        }

        /// <summary>收尾：显示进度直接置满（在场景真正激活 / 加载完成之后调用）。</summary>
        public void Complete()
        {
            _completed = true;
            _raw = 1f;
            _value = 1f;
            if (MinSeconds > 0f && _elapsed < MinSeconds) _elapsed = MinSeconds;
        }
    }
}
