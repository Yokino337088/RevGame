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
        private bool _ownsServices;
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
            RevServices.Register<IBattleService, BattleService>(owner: this);
            RevServices.Register<GameFlow, GameFlow>(owner: this);

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // 自动化服务与启动宿主同寿命，才能从大厅跨场景跑到章节奖励；正式包完全不注册。
            // 执行器只模拟输入和观察结果，游戏规则仍由原来的GameFlow/BattleService决定。
            RevServices.Register<RevGame.Animation.GameAutomationRunner, RevGame.Animation.GameAutomationRunner>(owner: this);
            RevGame.Animation.GameAutomationCommands.Register();
            GameGmCommands.Register();
            GameCombatGmCommands.Register();
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
            RevGame.Animation.GameAutomationCommands.Unregister();
            RevServices.Unregister<RevGame.Animation.GameAutomationRunner>();
            GameCombatGmCommands.Unregister();
            GameGmCommands.Unregister();
#endif
            // 先退出流程再注销战斗服务，退出回调仍然可以读取最后一份战斗数据。
            RevServices.Unregister<GameFlow>();
            RevServices.UnregisterAllOf(this);
            // 先清掉配置快照再卸载表：之后任何迟到的读取都会立刻报“配置尚未装配”，而不是读到半卸载的数据。
            GameConfig.Reset();
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
