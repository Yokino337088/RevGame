// ============================================================
// GameBootstrap.cs —— 场景唯一启动入口。
// 框架初始化一次，注册服务后交给 GameFlow 统一开关域。销毁时按相反顺序注销，便于重复 Play。
// ============================================================
using DG.Tweening;
using RevGame.Data;
using RevGame.Flow;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.Bootstrap
{
    [DefaultExecutionOrder(-100)]
    public sealed class GameBootstrap : MonoBehaviour
    {
        /// <summary>跨场景只保留一个启动宿主；新场景里的调试启动器只能销毁自己，不能重新初始化框架。</summary>
        private static GameBootstrap _instance;

        //作者原话：这个状态变量代表是否已经初始化过了，防止重复初始化
        private bool _ownsServices;

        //作者原话：这个状态变量代表是否已经被释放掉了
        private bool _released;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            _ownsServices = true;
            DontDestroyOnLoad(gameObject);
#if UNITY_EDITOR
            // 配置资产负责持久化；此赋值防止域重载后编辑器延迟同步晚于场景 Awake。
            RevEditorResPolicy.ResRoot = "Assets/GameRes/";
            Application.runInBackground = true;
#endif
            RevUISetting.CanvasOrientation = RevUICanvasOrientation.Landscape;
            // 用户指定后续统一按 1920×1080 布局。框架自带 Canvas 也是这个基准；这里显式约束兜底 Canvas。
            // 这是 UI 的“设计尺子”，不是强制把手机硬件改成另一种分辨率，运行时不调用 SetResolution。
            RevUISetting.ReferenceResolution = UI.GameBattleLayout.DesignResolution;
            // 结算与奖励是必经步骤，点遮罩不能把唯一的继续入口关掉。
            RevUISetting.ClickMaskClosesTop = false;
            RevInitialize.Initialize();
            // 图集必须在任何面板/棋子加载前接上Unity回调；通过RevServices统一注销，重复Play不累积监听。
            RevServices.Register<View.GameSpriteAtlasService, View.GameSpriteAtlasService>(owner: this);
            // ★ 必须立刻取一次，强制现在就创建：RevServices 对 Register<I, T>() 是“懒创建”，不取就要等到第一次 GetRequired 才 new，
            //   而这个服务的构造函数才去订阅 SpriteAtlasManager.atlasRequested。场景里已有 Image 用到图集 Sprite 时，
            //   Unity 会在更早的帧发出请求；那一刻没人监听，Unity 只请求一次、不会重发，图集永远不会交还给 Unity，
            //   结果就是 Image 的 texture 为空（“UI图集像素未绑定”）。取到的对象仍由容器持有并在注销时 Dispose，这里不保存引用。
            RevServices.GetRequired<View.GameSpriteAtlasService>();
#if UNITY_ANDROID || UNITY_EDITOR
            // ★ 手机画面必须全屏。SetResolution(..., false) 中的 false 表示“窗口模式”，不是“不旋转”。
            // Android / Simulator 会因此预留系统导航栏；横屏时它会变成右侧深灰条，放大背景也填不到那里。
            // Device.Screen 在模拟器里控制模拟手机，真机上等同普通 Screen；安全区仍只用于避让刘海的交互布局。
            UnityEngine.Device.Screen.fullScreen = true;
#endif
            // DOTween 仅负责表现；预留容量避免密集弹幕时扩容，Tween 本身也允许复用。
            // 每个池对象自己 Kill 自己的动画，不能用全局 KillAll 误伤其他面板。
            DOTween.Init(recycleAllByDefault: true, useSafeMode: true, logBehaviour: LogBehaviour.ErrorsOnly);
            DOTween.SetTweensCapacity(512, 128);
#if !UNITY_EDITOR
            // 用户明确要求手机横屏；设备方向和根 Canvas 都固定横屏。
            Screen.orientation = ScreenOrientation.LandscapeLeft;
#endif
            // ==================== 全局服务注册（新手必读） ====================
            // 【两个泛型参数是什么】Register<TService, TImpl>() 的第一个是「取用键」，第二个是「实现类」，
            //   并不是固定「接口 + 实现」的写法：
            //     · 取用键：以后谁要拿这个服务，就写 RevServices.GetRequired<取用键>()；
            //     · 实现类：容器真正 new 出来的类型，框架要求它能赋值给取用键（约束 TImpl : TService）。
            //   因此 Register<X, X>() 是合法的“自注册”：拿 X 当键、new 一个 X(这里框架的做法是懒加载new出来)。
            //   写成这样只有一个目的——
            //   让容器负责创建，并在注销时 Dispose（等价于 Register<T>(new T())，但释放权在容器手上）。
            //
            // 【什么时候用接口做键，什么时候用类做键】两种都合规，判断标准是“以后会不会换实现”：
            //     · 会换、要单测 Mock、或只想对外暴露能力  → 用接口做键（下方 IBattleService / IScoreProgress）；
            //     · 全项目只有这一份实现，且调用方要用到具体成员(这个就是单例模式) → 直接用类做键（下方 StoryProgress / GameAudioSettings）；
            //   为了“看起来像接口”而抽一个没有成员的接口，只会多一层跳转，没有收益。
            //
            // 【owner: this 的作用】把这批服务登记在 GameBootstrap 名下；
            //   OnDestroy 里一行 RevServices.UnregisterAllOf(this) 会按相反顺序全部注销，
            //   其中“容器创建”的实例会被 Dispose —— 所以 GameAudioSettings 才实现了 IDisposable。
            // ================================================================
            // 纯本地进度存档（按位标记开场/教学是否看完），没有替换需求，用类做键即可；无参构造供容器创建。
            RevServices.Register<StoryProgress, StoryProgress>(owner: this);
            // 玩家音量偏好：全项目一份、要跨战斗常驻；它实现 IDisposable，注销时由容器负责释放。
            RevServices.Register<GameAudioSettings, GameAudioSettings>(owner: this);
            // 在任何BGM请求之前应用玩家已保存音量；此服务是全局寿命，不随战斗退场丢失。
            // 注意 Register<TService, TImpl>() 是“懒创建”：不在这里取一次，构造函数就不会执行，音量也就不会应用。
            RevServices.GetRequired<GameAudioSettings>();
            // 积分要跨局累计、要落盘、还要能在测试里替换，因此抽了接口：调用方只认 IScoreProgress，不认存储实现。
            RevServices.Register<IScoreProgress, ScoreProgressStore>(owner: this);
            // 战斗服务是全项目最核心的契约：UI、流程、AI 都只认 IBattleService，实现可以整体替换（含固定种子的测试实例）。
            RevServices.Register<IBattleService, BattleService>(owner: this);
            // 流程控制者目前只有一份实现、还没有接口，调用方会直接用到它的具体方法（切场景、Boss 演出、回大厅）。
            RevServices.Register<GameFlow, GameFlow>(owner: this);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 自动化服务与启动宿主同寿命，才能从大厅跨场景跑到章节奖励；正式包完全不注册。
            // 执行器只模拟输入和观察结果，游戏规则仍由原来的GameFlow/BattleService决定。
            RevServices.Register<RevGame.Automation.GameAutomationRunner, RevGame.Automation.GameAutomationRunner>(owner: this);
            GameGmCommands.RegisterAll();
            StoryAutomationCommands.Register();
#endif
        }
        /// <summary>先用框架资源门面异步读导表数据，再开放主界面；首次真机加载不依赖编辑器直读。</summary>
        private async void Start()
        {
            if (!_ownsServices)
            {
                return;
            }
            try
            {
                // 让场景表现先订阅 ScreenChanged，避免表已缓存时同步完成而漏掉首页事件。
                await RevTask.Yield();

                if (_released)
                {
                    return;
                }
                //到这里真正开始去启动游戏的流程
                await RevServices.GetRequired<GameFlow>().StartAsync();
            }
            catch (System.Exception error)
            {
                RevLog.Exception(error, "游戏启动失败：请检查过场面板与资源配置", "RevGame");
            }
        }
        /// <summary>统一应用退出入口；停止 Play 或退出 Player 后复用 OnDestroy 的服务/域清理链。</summary>
        public static void RequestQuit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
        private void OnDestroy()
        {
            // 重复启动器没有注册过服务，绝不能在销毁时注销真正的全局宿主。
            if (!_ownsServices)
            {
                return;
            }
            _released = true;
            _instance = null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 必须先停测试再退出游戏域：测试可能还按着按钮，不能对已销毁的旧面板补发松手/点击。
            GameGmCommands.UnregisterAll();
            StoryAutomationCommands.Unregister();
            RevServices.Unregister<RevGame.Automation.GameAutomationRunner>();

#endif
            // 先退出流程再注销战斗服务，退出回调仍然可以读取最后一份战斗数据。
            RevServices.Unregister<GameFlow>();
            RevServices.UnregisterAllOf(this);
            // 先清掉配置快照再卸载表：之后任何迟到的读取都会立刻报“配置尚未装配”，而不是读到半卸载的数据。
            GameConfig.Reset();
            // 14 张配置表在这里统一卸载（加载方是 GameFlow，释放方是启动宿主，避免两边都卸一次）。
            // 顺序不强制，但必须先 Reset 快照（上一行）：让迟到的读取立刻报“配置尚未装配”，
            // 而不是读到正在被卸载的数据；重复 Play 也不会残留上一局的表。
            RevDataTableManager.Unload<BattleTextTable>();
            RevDataTableManager.Unload<BattleTuningTable>();
            RevDataTableManager.Unload<PieceConfigTable>();
            RevDataTableManager.Unload<StarConfigTable>();
            RevDataTableManager.Unload<PrepBuffConfigTable>();
            RevDataTableManager.Unload<BattleEventConfigTable>();
            RevDataTableManager.Unload<EventRotationConfigTable>();
            RevDataTableManager.Unload<ChapterConfigTable>();
            RevDataTableManager.Unload<EnemyRosterConfigTable>();
            RevDataTableManager.Unload<EnemyReinforceConfigTable>();
            RevDataTableManager.Unload<RewardConfigTable>();
            RevDataTableManager.Unload<RewardBoostConfigTable>();
            RevDataTableManager.Unload<BossSkillConfigTable>();
            RevDataTableManager.Unload<BalanceRuleTable>();
        }
    }
}
