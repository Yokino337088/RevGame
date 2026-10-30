// Boss关键帧跟随棋盘租约：仅Boss回合加载当前章，旧动画/死亡消融结束后归还，不把六章图集永久预热。
using System;
using RevGame.Services;
using Revolution;
using UnityEngine;

namespace RevGame.View
{
    public sealed partial class BoardView
    {
        private const string BossArtRoot = "Config/Boss";
        /// <summary>当前章节配置/图集由棋盘持有，多个Boss视图只是借其Sprite[]。</summary>
        private BossChapterArtConfig _bossArt;
        /// <summary>当前章图集的独立租约，释放顺序晚于所有借帧的视图。</summary>
        private GameSpriteAtlasService.ScopedAtlasLease _bossAtlas;
        /// <summary>用于成对归还资源门面的逻辑名，不拼运行时磁盘路径。</summary>
        private string _bossArtName;
        /// <summary>0表示普通回合；相同章节不重复发起资源请求。</summary>
        private int _bossRequestedChapter;
        /// <summary>每次换章/退场递增，旧异步结果只能自行清理。</summary>
        private int _bossArtVersion;
        /// <summary>首次帧/图集准备成功后开放战斗推进；失败由LoadError显示。</summary>
        private bool _bossArtReady = true;
        public bool IsBossArtReady
        {
            get
            {
                return _bossArtReady;
            }
        }
        public int BossArtChapter
        {
            get
            {
                return _bossArt == null ? 0 : _bossArt.Chapter;
            }
        }
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        /// <summary>只读真实模型与视图绑定，验证该章全部Boss出现且8帧正确；没有Boss时不能空集合冒充就绪。</summary>
        public bool AreBossViewsReady(int chapter)
        {
            int bosses = 0;
            foreach (var piece in RevServices.GetRequired<IBattleService>().EnemyPieces)
            {
                if (!piece.IsAlive || !piece.IsBoss)
                {
                    continue;
                }
                bosses++;

                if (!_pieces.TryGetValue(piece.Id, out PieceView view) || view == null
                    || view.BossAppearanceChapter != chapter || view.AnimationFrameCount != 8)
                {
                    return false;
                }
            }
            return bosses == 1;
        }
#endif

        /// <summary>刷新只检查当前章与Boss回合，真正加载一次；同章血量/气眼变化不会重置动画或再加引用。</summary>
        private async void RequestBossArt()
        {
            IBattleService battle = RevServices.GetRequired<IBattleService>();
            int chapter = battle.IsBossRound ? battle.Chapter : 0;

            if (_released || chapter == _bossRequestedChapter)
            {
                return;
            }
            _bossRequestedChapter = chapter;
            _bossArtReady = chapter == 0;
            int version = ++_bossArtVersion;
            string name = chapter == 0 ? null : BossChapterArtConfig.ResourceName(chapter);
            bool requested = false;
            bool adopted = false;
            GameSpriteAtlasService.ScopedAtlasLease atlas = null;
            try
            {
                BossChapterArtConfig config = null;

                if (chapter != 0)
                {
                    // 先建立图集租约再读配置；配置里的Sprite可能触发atlasRequested，提前归属可避免取消后留下无主图集。
                    atlas = RevServices.GetRequired<GameSpriteAtlasService>().AcquireScopedAtlas(BossChapterArtConfig.AtlasName(chapter));
                    await atlas.WaitReadyAsync();

                    if (_released || this == null || version != _bossArtVersion)
                    {
                        return;
                    }
                    requested = true;
                    RevResHandle handle = RevResManager.LoadAsync(BossArtRoot, name, typeof(BossChapterArtConfig), null, RevResGroup.Scene);
                    float deadline = Time.realtimeSinceStartup + 30f;
                    while (handle.IsLoading && !_released && version == _bossArtVersion)
                    {
                        if (Time.realtimeSinceStartup > deadline)
                        {
                            throw new TimeoutException("Boss美术配置加载超时：" + name);
                        }
                        await RevTask.Yield();
                    }
                    if (_released || this == null || version != _bossArtVersion)
                    {
                        return;
                    }
                    config = handle.Content as BossChapterArtConfig;

                    if (!handle.IsLoaded || config == null)
                    {
                        throw new InvalidOperationException("Boss美术配置加载失败：" + handle.ErrorReason);
                    }
                    config.Validate(chapter);
                }
                // 上一回合Boss可能正在消融；等它回池后再卸掉旧图集，不能把尸体短效变成白块。
                while (!_released && version == _bossArtVersion && HasDissolvingBossArt())
                {
                    await RevTask.Yield();
                }
                if (_released || this == null || version != _bossArtVersion)
                {
                    return;
                }
                GameSpriteAtlasService.ScopedAtlasLease oldAtlas = _bossAtlas;
                string oldName = _bossArtName;
                _bossArt = config;
                _bossAtlas = atlas;
                _bossArtName = name;
                adopted = true;
                foreach (var pair in _pieces)
                {
                    PieceView view = pair.Value;
                    var piece = FindPiece(pair.Key);

                    if (piece != null && piece.IsBoss)
                    {
                        view.BindBossArt(config);
                        view.SetBodySize(piece.Size, true, piece.IsKing);
                        PrimeBossIntro(view);
                    }
                    else

                        if (view.BossAppearanceChapter != 0)
                        {
                            view.BindBossArt(null);
                        }
                }
                oldAtlas?.Dispose();

                if (oldName != null)
                {
                    RevResManager.Release(BossArtRoot, oldName);
                }
                // 资源句柄完成后再等主将当前帧像素绑定；期间保持原来的场景屏障，不提前播放出生Shader。
                float frameDeadline = Time.realtimeSinceStartup + 15f;
                while (!_released && version == _bossArtVersion && config != null)
                {
                    bool framesReady = true;
                    foreach (PieceView view in _pieces.Values)
                    {
                        if (view.BossAppearanceChapter == chapter && !view.IsBossFrameTextureReady)
                        {
                            framesReady = false;
                            break;
                        }
                    }

                    if (framesReady)
                    {
                        break;
                    }
                    if (Time.realtimeSinceStartup > frameDeadline)
                    {
                        throw new TimeoutException("Boss图集像素未能绑定：" + name);
                    }
                    await RevTask.Yield();
                }
                if (!_released && version == _bossArtVersion)
                {
                    foreach (PieceView view in _pieces.Values)
                    {
                        PrimeBossIntro(view);
                    }
                    _bossArtReady = true;
                }
            }
            catch (Exception error)
            {
                if (_released || version != _bossArtVersion)
                {
                    return;
                }
                LoadError = "Boss序列帧加载失败：" + error.Message;
                RevLog.Exception(error, LoadError, "RevGame");
                var flow = RevServices.Get<RevGame.Flow.GameFlow>();

                if (flow != null && !flow.IsTransitioning)
                {
                    flow.ShowChapterBackgroundFailure(LoadError);
                }
            }
            finally
            {
                // 异步被取消/换章时本请求自还租约；不会误释放后来接管的配置和图集。
                if (!adopted)
                {
                    atlas?.Dispose();

                    if (requested)
                    {
                        RevResManager.Release(BossArtRoot, name);
                    }
                }
            }
        }
        private bool HasDissolvingBossArt()
        {
            foreach (PieceView view in _dissolvingPieces)
            {
                if (view != null && view.BossAppearanceChapter != 0)
                {
                    return true;
                }
            }
            return false;
        }
        /// <summary>所有棋子和消融已回池后调用，Sprite引用已清；在途请求自行在finally释放。</summary>
        private void ReleaseBossArt()
        {
            ++_bossArtVersion;
            _bossAtlas?.Dispose();
            _bossAtlas = null;

            if (_bossArtName != null)
            {
                RevResManager.Release(BossArtRoot, _bossArtName);
            }
            _bossArtName = null;
            _bossArt = null;
            _bossRequestedChapter = 0;
            _bossArtReady = true;
        }
    }
}
