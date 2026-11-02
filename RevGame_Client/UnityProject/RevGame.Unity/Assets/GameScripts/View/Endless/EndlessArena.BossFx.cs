// ============================================================
// EndlessArena.BossFx.cs —— 把领主技能“画出来”（新手先读这段）
//
// 【职责】
// EndlessService 只负责算伤害，并把“放了什么技能”写进 BossCasts。
// 这个文件每帧读取新记录，然后做四件事：
//   1. 从对象池取六章同款的 BossSkillFxView 播放 Shader 特效；
//   2. 让棋盘格子亮起（预警是持续亮，命中是一闪再衰减）；
//   3. 播放六章同款的技能音效；
//   4. 给领主换上对应章节的 Boss 外观。
//
// 【棋盘特效怎么做的】
// 六章的棋格是 81 个独立对象，可以逐格做 Tween 动画；无尽棋盘为了省绘制是一整张面片。
// 所以这里把每格亮度放进一个 81 长度的数组传给 Shader，由 Shader 按格子编号取值，
// 只多一次数组上传，没有额外的绘制调用。
// ============================================================
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Board;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessArena
    {
        /// <summary>Boss 美术配置的资源根目录，和六章 BoardView 用同一份配置。</summary>
        private const string BossArtRoot = "Config/Boss";
        /// <summary>同时存在的特效组上限。只限制表现，超出时少画一组，伤害不受影响。</summary>
        private const int MaxFxGroups = 6;
        /// <summary>命中后格子亮度每秒衰减多少：3 表示约 0.33 秒灭掉。</summary>
        private const float CellFxDecay = 3f;
        /// <summary>预警期间格子保持的亮度，比命中闪光暗，避免遮挡棋子。</summary>
        private const float CellWarnLevel = .55f;

        /// <summary>无尽棋盘每格 1 个世界单位。</summary>
        private const float FxCellSize = 1f;
        /// <summary>
        /// 特效的棋盘原点，也就是第 0 格的左下角。
        /// 特效用 原点 + (格号 + 0.5) * 格宽 算格子中心；无尽棋盘第 N 格的中心就在坐标 N，
        /// 棋盘左下角是 -0.5。这里写 0 的话，所有特效都会往右上偏半格，落在格线交点上。
        /// </summary>
        private const float FxBoardOrigin = -.5f;
        private static readonly int CellFxId = Shader.PropertyToID("_CellFx");
        private static readonly int SkillTintId = Shader.PropertyToID("_SkillTint");
        private static readonly int ShockId = Shader.PropertyToID("_Shock");

        /// <summary>每格当前亮度，下标 = y * 9 + x。预分配，运行中不产生垃圾。</summary>
        private readonly float[] _cellFx = new float[81];
        /// <summary>当前正在播放的特效，退出时要全部收回。</summary>
        private readonly HashSet<BossSkillFxView> _bossFx = new HashSet<BossSkillFxView>();
        /// <summary>本局用过的技能音效别名，退出时统一卸载，防止音效一直占内存。</summary>
        private readonly HashSet<string> _skillSounds = new HashSet<string>();
        /// <summary>已经处理到第几条技能记录。</summary>
        private int _castSerial;
        /// <summary>棋盘冲击波：xy 是圆心格，z 是开始时刻（战斗秒），w 是强度。</summary>
        private Vector4 _shock = new Vector4(0, 0, -100, 0);
        /// <summary>当前技能的主色，传给棋盘 Shader。</summary>
        private Color _skillTint = Color.white;
        /// <summary>全部格子都熄灭后就不再上传数组，省一点开销。</summary>
        private bool _cellFxDirty;
        /// <summary>BGM 轮播器，进入无尽创建，退出释放。</summary>
        private EndlessMusicPlaylist _music;
        /// <summary>已加载的 Boss 美术配置。</summary>
        private BossChapterArtConfig _bossArt;
        /// <summary>当前美术对应第几章；0 表示没有加载任何 Boss 美术。</summary>
        private int _bossArtChapter;
        /// <summary>Boss 图集的使用凭证：拿着它图集才不会被卸载，退出时归还。</summary>
        private GameSpriteAtlasService.ScopedAtlasLease _bossAtlas;
        /// <summary>加载版本号。换章或退出时递增，迟到的异步结果发现版本不对就自己作废。</summary>
        private int _bossArtVersion;

        /// <summary>每帧入口：播放新技能、衰减格子亮度、把棋盘参数写给 Shader。</summary>
        private void UpdateBossFx(EndlessService service, float deltaTime)
        {
            _music?.Tick(deltaTime);
            EndlessUnit boss = service.BossUnit();
            if (boss != null)
            {
                EnsureBossArt(boss.BossChapter);
            }
            ReadNewCasts(service);
            UpdateCellFx(service, boss, deltaTime);
        }

        /// <summary>
        /// 读取上一帧之后新增的技能记录。如果一帧里新增太多（环形缓冲被覆盖），
        /// 直接跳到还没被覆盖的最旧一条，只会少播几个特效。
        /// </summary>
        private void ReadNewCasts(EndlessService service)
        {
            int latest = service.BossCastSerial;
            int first = Mathf.Max(_castSerial + 1, latest - service.BossCasts.Length + 1);
            for (int serial = first; serial <= latest; serial++)
            {
                EndlessBossCast cast = service.BossCasts[serial % service.BossCasts.Length];
                if (cast.serial == serial)
                {
                    PlayCast(cast);
                }
            }
            _castSerial = latest;
        }

        /// <summary>播放一条技能记录：Boss 蓄力动作、Shader 特效、音效、棋盘反馈。</summary>
        private void PlayCast(EndlessBossCast cast)
        {
            BossSkillInfo info = cast.info;
            _skillTint = BossSkillFxView.SkillTint(info.Kind, false);
            if (_pieces.TryGetValue(info.BossId, out PieceView bossView))
            {
                if (cast.telegraph)
                {
                    bossView.PlayBossCharge(info.Kind, info.Windup);
                }
                else
                {
                    bossView.PlayAttackPulse();
                }
            }
            SpawnSkillFx(info, cast.telegraph);
            if (!cast.telegraph)
            {
                PlaySkillSound(info.Kind);
                // 命中格立刻最亮，然后在 UpdateCellFx 里衰减，形成棋盘被震亮的效果。
                for (int y = 0; y < 9; y++)
                {
                    for (int x = 0; x < 9; x++)
                    {
                        if (BossExpandedGeometry.Contains(info.Kind, info.A, info.B, info.C, info.Radius, info.Index, new BoardPos(x, y)))
                        {
                            _cellFx[y * 9 + x] = 1;
                        }
                    }
                }
                _cellFxDirty = true;
                _shock = new Vector4(info.A.X, info.A.Y, RevServices.GetRequired<EndlessService>().Run.seconds, cast.exclusive ? 1f : .6f);
                _shakeBoost = cast.exclusive ? .14f : .06f;
            }
        }

        /// <summary>从对象池取特效播放。异步回来时再核对一次版本，已退出无尽就直接还回去。</summary>
        private void SpawnSkillFx(BossSkillInfo info, bool telegraph)
        {
            if (_bossFx.Count >= MaxFxGroups)
            {
                return;
            }
            int version = _generation;
            GamePoolRequests.GetAsync<BossSkillFxView>(fx =>
            {
                if (fx == null)
                {
                    return;
                }
                if (!_active || version != _generation)
                {
                    RevPool.Return(fx, 0);
                    return;
                }
                _bossFx.Add(fx);
                fx.Play(info, telegraph, OnSkillFxFinished, FxCellSize, FxBoardOrigin);
            }, transform);
        }

        /// <summary>特效完整播完后回池。</summary>
        private void OnSkillFxFinished(BossSkillFxView fx)
        {
            if (_bossFx.Remove(fx))
            {
                RevPool.Return(fx, 0);
            }
        }

        /// <summary>播放六章同款技能音效。音效按需注册，第一次用时才异步加载，退出时统一卸载。</summary>
        private void PlaySkillSound(BossSkillKind kind)
        {
            string alias = GameAudio.GetBossSkillSoundName(kind);
            if (!RevSound.IsRegistered(alias))
            {
                RevSound.Register(alias, GameAudio.GetBossSkillSoundPath(kind));
            }
            _skillSounds.Add(alias);
            RevSound.Play(alias, volume: .7f);
        }

        /// <summary>计算每格亮度：预警中的格子不低于预警亮度，其余格子按时间衰减。</summary>
        private void UpdateCellFx(EndlessService service, EndlessUnit boss, float deltaTime)
        {
            bool warning = boss != null && boss.WarningAt > 0;
            if (!_cellFxDirty && !warning)
            {
                return;
            }
            bool any = false;
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    int index = y * 9 + x;
                    float value = Mathf.Max(0, _cellFx[index] - deltaTime * CellFxDecay);
                    if (warning && service.IsWarned(boss, x, y))
                    {
                        value = Mathf.Max(value, CellWarnLevel);
                    }
                    _cellFx[index] = value;
                    any |= value > 0;
                }
            }
            _cellFxDirty = any;
            _gridProperties.SetFloatArray(CellFxId, _cellFx);
            _gridProperties.SetColor(SkillTintId, _skillTint);
        }

        /// <summary>
        /// 按章节加载 Boss 外观。流程和六章 BoardView 一致：先拿图集凭证，再读配置，
        /// 每一步异步回来都核对版本号，防止退出或换章后把旧结果绑到新棋子上。
        /// </summary>
        private async void EnsureBossArt(int chapter)
        {
            if (chapter == _bossArtChapter || chapter <= 0)
            {
                return;
            }
            int version = ++_bossArtVersion;
            // 先按旧章名称释放旧配置，再记录新章；否则Release会错误扣减新章引用。
            ReleaseBossArt();
            _bossArtChapter = chapter;
            string name = BossChapterArtConfig.ResourceName(chapter);
            GameSpriteAtlasService.ScopedAtlasLease lease = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas(BossChapterArtConfig.AtlasName(chapter));
            await lease.WaitReadyAsync();
            if (!_active || version != _bossArtVersion)
            {
                lease.Dispose();
                return;
            }
            RevResHandle handle = RevResManager.LoadAsync(BossArtRoot, name, typeof(BossChapterArtConfig), null, RevResGroup.Scene);
            while (handle.IsLoading && _active && version == _bossArtVersion)
            {
                await RevTask.Yield();
            }
            if (!_active || version != _bossArtVersion || !handle.IsLoaded)
            {
                RevResManager.Release(BossArtRoot, name);
                lease.Dispose();
                return;
            }
            _bossAtlas = lease;
            _bossArt = handle.Content as BossChapterArtConfig;
        }

        /// <summary>把 Boss 美术应用到棋子上。每帧调用但只在真正变化时生效（由 _bossArtBound 判断）。</summary>
        private void BindBossArt(EndlessService service, EndlessUnit boss)
        {
            if (boss == null && _bossArt != null)
            {
                // Boss尸体完成六章溶解后才归还帧，保留完整死亡表现。
                if (_dissolving.Count == 0)
                {
                    ReleaseBossArt();
                }
                return;
            }
            if (boss == null || _bossArt == null || _bossArt.Chapter != boss.BossChapter || _bossArtBound == boss.Piece.Id)
            {
                return;
            }
            if (_pieces.TryGetValue(boss.Piece.Id, out PieceView view))
            {
                view.BindBossArt(_bossArt);
                view.SetBodySize(boss.Piece.Size, true, false);
                _bossArtBound = boss.Piece.Id;
            }
        }

        /// <summary>归还 Boss 美术占用的配置和图集。</summary>
        private void ReleaseBossArt()
        {
            if (_bossArt != null)
            {
                RevResManager.Release(BossArtRoot, BossChapterArtConfig.ResourceName(_bossArtChapter));
                _bossArt = null;
            }
            _bossAtlas?.Dispose();
            _bossAtlas = null;
            _bossArtChapter = 0;
            _bossArtBound = -1;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>
        /// 测试用：报告轮播状态，以及内存里实际存在哪些曲目。
        /// 后者直接问 Unity 而不是相信自己的记录，这样“卸载”有没有真的生效一目了然。
        /// </summary>
        public object MusicSnapshot()
        {
            var loaded = new List<string>();
            foreach (EndlessMusic row in RevDataTableManager.Get<EndlessMusicTable>().All)
            {
                foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>())
                {
                    if (clip.name == row.Sound)
                    {
                        loaded.Add(clip.name);
                        break;
                    }
                }
            }
            return new Dictionary<string, object>
            {
                ["current"] = _music?.CurrentName ?? "", ["next"] = _music?.NextName ?? "", ["fading"] = _music?.FadingName ?? "",
                ["remaining"] = _music?.DebugRemaining ?? -1, ["loadedInMemory"] = loaded
            };
        }

        /// <summary>测试用：把当前曲目跳到结尾前若干秒。</summary>
        public void DebugSeekMusic(float remaining)
        {
            _music?.DebugSeek(remaining);
        }
#endif

        /// <summary>退出无尽时清理本文件管理的全部资源：特效、音效、音乐、Boss 美术。</summary>
        private void ReleaseBossFx()
        {
            _music?.Dispose();
            _music = null;
            foreach (BossSkillFxView fx in _bossFx)
            {
                RevPool.Return(fx, 0);
            }
            _bossFx.Clear();
            foreach (string alias in _skillSounds)
            {
                RevSound.Unload(alias);
            }
            _skillSounds.Clear();
            _bossArtVersion++;
            ReleaseBossArt();
            System.Array.Clear(_cellFx, 0, _cellFx.Length);
            _castSerial = 0;
            _cellFxDirty = false;
            _shock = new Vector4(0, 0, -100, 0);
        }
    }
}
