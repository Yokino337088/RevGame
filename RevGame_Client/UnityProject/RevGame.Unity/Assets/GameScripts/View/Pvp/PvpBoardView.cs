using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Sprites;

namespace RevGame.Pvp
{
    /// <summary>
    /// 一张有限网格绘制象牙白棋盘、黑白棋子及全部提示。整张棋盘由RevPool租用，材质共享；
    /// 棋局改变时只上传81点数据，Shader负责呼吸/流光，不在每帧遍历围棋连通块或克隆材质。
    /// </summary>
    [RevPoolPrefab("Pvp", "PvpBoardView", Group = RevResGroup.Battle)]
    public sealed partial class PvpBoardView : MonoBehaviour
    {
        [SerializeField] private MeshRenderer boardRenderer;
        [SerializeField] private MeshRenderer backgroundRenderer;
        private readonly Vector4[] _cells = new Vector4[81];
        private readonly Vector4[] _marks = new Vector4[81];
        private readonly int[] _liberties = new int[81];
        private readonly GoBoardAnalysis _analysis = new GoBoardAnalysis();
        [SerializeField] private Sprite[] eyeBlinkFrames;
        // 棋盘Shader不经过SpriteRenderer，预先取四张图集副本，避免运行时反复找帧。
        private readonly Sprite[] _packedEyeFrames = new Sprite[4];
        private int _eyeFrame = -1;
        private GoCore _analyzedCore;
        private GoPointSet _previousEyes;
        private int _eyeAtlasVersion;
        public int EyeVersion
        {
            get;
            private set;
        }
        internal int VisibleEyeCount
        {
            get;
            private set;
        }
        internal int VisibleTerritoryCount
        {
            get;
            private set;
        }
        internal bool EyePacked => _eyeFrame >= 0 && _packedEyeFrames[_eyeFrame] != null && _packedEyeFrames[_eyeFrame].packed;
        internal string EyeFrameName => _eyeFrame < 0 ? "" : eyeBlinkFrames[_eyeFrame].name;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        internal int EyeFramesSeen { get; private set; }
#endif
        internal bool ShaderReady => Ready && boardRenderer.sharedMaterial.shader.isSupported;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
        internal int[] CopyLibertyMarksForCheck()
        {
            var marks = new int[GoCore.PointCount];
            for (int i = 0; i < marks.Length; i++)
            {
                marks[i] = (int)_marks[i].x;
            }
            return marks;
        }
#endif
        private MaterialPropertyBlock _boardBlock, _backgroundBlock;
        private int _revision = -1, _hover = -1, _width, _height;
        private Camera _camera;
        public bool Ready { get { return boardRenderer != null && backgroundRenderer != null && boardRenderer.sharedMaterial != null && backgroundRenderer.sharedMaterial != null && eyeBlinkFrames != null && eyeBlinkFrames.Length == 4 && eyeBlinkFrames[0] != null; } }

        private void OnEnable()
        {
            _camera = Camera.main;
            _revision = -1;
            _hover = -1;
            _width = 0;
            _eyeAtlasVersion = -1;
            _eyeFrame = -1;
            EyeVersion = 0;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            EyeFramesSeen = 0;
#endif
            _previousEyes = default;
            _analyzedCore = null;
            _boardBlock = _boardBlock ?? new MaterialPropertyBlock();
            _backgroundBlock = _backgroundBlock ?? new MaterialPropertyBlock();
        }
        private void Update()
        {
            PvpService service = RevServices.Get<PvpService>();
            if (service == null || !Ready || _camera == null)
            {
                return;
            }
            FitCamera();
            ReadInput(service);
            if (_revision != service.Revision)
            {
                _revision = service.Revision;
                RefreshBoard(service);
            }
            GoCore core = service.DisplayCore;
            // 围住的区域按规则分析结果常显；Shader时钟只控制呼吸光，不改变落子、领地或胜负。
            bool counting = core != null && ReferenceEquals(_scoreCore, core);
            _boardBlock.SetFloat("_ShowTerritory", core != null && (service.HintsVisible || core.Phase == GoPhase.Scoring) ? 1 : 0);
            _boardBlock.SetFloat("_ScoreProgress", counting ? _scoreProgress : -1);
            _boardBlock.SetFloat("_Clock", Time.unscaledTime);
            bool live = service.ReplayStep < 0;
            _boardBlock.SetFloat("_ComputerPreview", live ? service.ComputerPreviewPoint : -1);
            _boardBlock.SetFloat("_ComputerLand", live ? service.ComputerLandingPoint : -1);
            _boardBlock.SetFloat("_ComputerDrop", Mathf.Clamp01(service.ComputerLandingAge / PvpService.LandingSeconds));
            BindEyeTexture();
            boardRenderer.SetPropertyBlock(_boardBlock);
        }
        private void FitCamera()
        {
            if (_width == Screen.width && _height == Screen.height)
            {
                return;
            }
            _width = Screen.width;
            _height = Screen.height;
            float aspect = _width / (float)Mathf.Max(1, _height);
            _camera.orthographicSize = Mathf.Max(6.4f, 8.6f / aspect);
            _camera.transform.position = new Vector3(4, 4, -10);
            float height = _camera.orthographicSize * 2;
            float backgroundWidth = Mathf.Max(height * aspect, height * (16f / 9f));
            backgroundRenderer.transform.localScale = new Vector3(backgroundWidth, backgroundWidth * 9f / 16f, 1);
            _backgroundBlock.SetFloat("_Aspect", 16f / 9f);
            // 原画按Cover裁切，建筑保持比例；不用把16:9图片横向拉伸成手机长屏。
            backgroundRenderer.SetPropertyBlock(_backgroundBlock);
        }
        private void ReadInput(PvpService service)
        {
            PvpPanel panel = RevUI.Get<PvpPanel>();
            if (panel == null || panel.IsModalOpen || service.DisplayCore == null || service.ReplayStep >= 0)
            {
                return;
            }
            bool touch = Input.touchCount > 0;
            Vector2 screen = touch ? Input.GetTouch(0).position : (Vector2)Input.mousePosition;
            // 编辑器失焦、鼠标离开Game窗口时可能返回无穷坐标；投影前拒绝，避免逐帧刷警告和伪落点。
            if (float.IsNaN(screen.x) || float.IsInfinity(screen.x) || float.IsNaN(screen.y) || float.IsInfinity(screen.y)
                || !_camera.pixelRect.Contains(screen))
            {
                if (_hover != -1)
                {
                    _hover = -1;
                    RefreshBoard(service);
                }
                return;
            }
            bool click = touch ? Input.GetTouch(0).phase == TouchPhase.Ended : Input.GetMouseButtonDown(0);
            bool overUi = EventSystem.current != null && (touch
                ? EventSystem.current.IsPointerOverGameObject(Input.GetTouch(0).fingerId)
                : EventSystem.current.IsPointerOverGameObject());
            if (overUi)
            {
                return;
            }
            Vector3 world = _camera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, 10));
            int x = Mathf.RoundToInt(world.x), y = Mathf.RoundToInt(world.y);
            int point = x >= 0 && x < 9 && y >= 0 && y < 9 ? y * 9 + x : -1;
            if (_hover != point)
            {
                _hover = point;
                RefreshBoard(service);
            }
            if (click && point >= 0)
            {
                service.SelectPoint(x, y);
            }
        }
        private void RefreshBoard(PvpService service)
        {
            GoCore core = service.DisplayCore;
            for (int i = 0; i < 81; i++)
            {
                _cells[i] = Vector4.zero;
                _marks[i] = Vector4.zero;
            }
            int selected = core != null && core.Phase == GoPhase.Playing && service.ReplayStep < 0
                ? (service.SelectedPoint >= 0 ? service.SelectedPoint : _hover) : -1;
            int last = -1;
            if (core != null)
            {
                AnalyzeBoard(core, service.ReplayStep < 0);
                bool counting = ReferenceEquals(_scoreCore, core);
                VisibleTerritoryCount = 0;
                VisibleEyeCount = 0;
                for (int i = 0; i < GoCore.PointCount; i++)
                {
                    bool open = core.Phase == GoPhase.Playing && _analysis.OpenTerritory[i];
                    int owner = open ? 0 : _analysis.Territory[i];
                    int eye = core.Phase == GoPhase.Playing && _analysis.Eye[i] == 1 ? 1 : 0;
                    _cells[i] = new Vector4((int)core.GetStone(i), counting ? _scoreOrder[i] : _analysis.Liberties[i], owner, eye);
                    _marks[i].w = core.DeadStones.Contains(i) ? 1 : 0;
                    VisibleTerritoryCount += owner == 0 ? 0 : 1;
                    VisibleEyeCount += eye;
                }
                int visibleMoves = service.ReplayStep >= 0 ? service.ReplayStep : service.Moves.Count;
                if (visibleMoves > 0)
                {
                    for (int i = visibleMoves - 1; i >= 0; i--)
                    {
                        PvpMove move = service.Moves[i];
                        if (move.kind == (int)GoAction.Play)
                        {
                            last = move.y * 9 + move.x;
                            break;
                        }
                    }
                }
                if (selected >= 0 && selected < 81)
                {
                    if (core.GetStone(selected) != GoSide.Empty)
                    {
                        MarkLiberties(core, selected);
                    }
                    else if (core.TryPreview(core.Turn, selected % 9, selected / 9, out GoPlayResult preview))
                    {
                        for (int i = 0; i < 81; i++)
                        {
                            _marks[i].y = preview.Captured.Contains(i) ? 1 : 0;
                        }
                    }
                }
                // 两气以内的棋块常显气口，玩家无需悬停也能发现危险。
                for (int i = 0; i < 81; i++)
                {
                    if (_analysis.Group[i] == i && _analysis.Liberties[i] <= 2)
                    {
                        MarkLiberties(core, i);
                    }
                }
                for (int i = 0; i < service.SuggestionCount; i++)
                {
                    GoHint hint = service.Suggestions[i];
                    _marks[hint.Y * 9 + hint.X].z = i + 1;
                }

            }
            _boardBlock.SetVectorArray("_Cells", _cells);
            _boardBlock.SetVectorArray("_Marks", _marks);
            _boardBlock.SetFloat("_ShowHints", service.HintsVisible ? 1 : 0);
            _boardBlock.SetFloat("_Selected", selected);
            _boardBlock.SetFloat("_Last", core != null && core.Phase == GoPhase.Playing ? last : -1);
            _boardBlock.SetFloat("_Turn", core == null ? 1 : (int)core.Turn);
            boardRenderer.SetPropertyBlock(_boardBlock);
        }

        /// <summary>气口按所属棋块着色。黑1、白2，可合并为3，避免共用气口被最后遍历的棋块覆盖。</summary>
        private void MarkLiberties(GoCore core, int stone)
        {
            int side = (int)core.GetStone(stone);
            int count = core.CopyLiberties(stone, _liberties);
            for (int i = 0; i < count; i++)
            {
                int point = _liberties[i];
                _marks[point].x = (int)_marks[point].x | side;
            }
        }

        private void AnalyzeBoard(GoCore core, bool live)
        {
            if (ReferenceEquals(core, _analyzedCore))
            {
                return;
            }
            core.Analyze(_analysis);
            _analyzedCore = core;
            if (live && core.Phase == GoPhase.Playing)
            {
                GoPointSet eyes = default;
                for (int i = 0; i < GoCore.PointCount; i++)
                {
                    if (_analysis.Eye[i] == 1)
                    {
                        eyes.Add(i);
                    }
                }
                if ((eyes.Low & ~_previousEyes.Low) != 0 || (eyes.High & ~_previousEyes.High) != 0)
                {
                    EyeVersion++;
                }
                _previousEyes = eyes;
            }
        }

        private void BindEyeTexture()
        {
            // 与六章一致：平时睁眼，短时依次闭合再睁开。整张棋盘共用时钟和图集UV。
            float age = Mathf.Repeat(Time.unscaledTime, 3.8f);
            int frame = age < 3.25f ? 0 : age < 3.35f ? 1 : age < 3.43f ? 2 : age < 3.55f ? 3 : age < 3.63f ? 2 : age < 3.73f ? 1 : 0;
            if (frame == _eyeFrame && _eyeAtlasVersion == GameSpriteAtlasService.BindingVersion)
            {
                return;
            }
            _eyeFrame = frame;
            _eyeAtlasVersion = GameSpriteAtlasService.BindingVersion;
#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
            if (VisibleEyeCount > 0)
            {
                EyeFramesSeen |= 1 << frame;
            }
#endif
            Sprite sprite = _packedEyeFrames[frame] ?? eyeBlinkFrames[frame];
            _boardBlock.SetTexture("_EyeTex", sprite.texture);
            _boardBlock.SetVector("_EyeUv", DataUtility.GetOuterUV(sprite));
        }

        internal void BindEyeFrames(GameSpriteAtlasService.ScopedAtlasLease atlas)
        {
            ReleaseEyeFrames();
            try
            {
                for (int i = 0; i < _packedEyeFrames.Length; i++)
                {
                    _packedEyeFrames[i] = atlas.GetPackedSprite(eyeBlinkFrames[i].name);
                }
            }
            catch
            {
                ReleaseEyeFrames();
                throw;
            }
        }

        internal void ReleaseEyeFrames()
        {
            for (int i = 0; i < _packedEyeFrames.Length; i++)
            {
                if (_packedEyeFrames[i] != null)
                {
                    Destroy(_packedEyeFrames[i]);
                    _packedEyeFrames[i] = null;
                }
            }
            _eyeFrame = -1;
        }

        private void OnDestroy()
        {
            ReleaseEyeFrames();
        }

        private void OnDisable()
        {
            _revision = -1;
            _hover = -1;
            _analyzedCore = null;
            ResetScoreCount();
            if (boardRenderer != null)
            {
                boardRenderer.SetPropertyBlock(null);
            }
            _boardBlock?.Clear();
        }
    }
}
