using RevGame.View;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.Pvp
{
    /// <summary>
    /// 一张有限网格绘制象牙白棋盘、黑白棋子及全部提示。整张棋盘由RevPool租用，材质共享；
    /// 棋局改变时只上传81点数据，Shader负责呼吸/流光，不在每帧遍历围棋连通块或克隆材质。
    /// </summary>
    [RevPoolPrefab("Pvp", "PvpBoardView", Group = RevResGroup.Battle)]
    public sealed class PvpBoardView : MonoBehaviour
    {
        [SerializeField] private MeshRenderer boardRenderer;
        [SerializeField] private MeshRenderer backgroundRenderer;
        private readonly Vector4[] _cells = new Vector4[81];
        private readonly Vector4[] _marks = new Vector4[81];
        private readonly int[] _liberties = new int[81];
        private readonly GoBoardAnalysis _analysis = new GoBoardAnalysis();
        private MaterialPropertyBlock _boardBlock, _backgroundBlock;
        private int _revision = -1, _hover = -1, _width, _height;
        private float _territoryUntil;
        private int _shownTerritoryMove = -1;
        private Camera _camera;
        public bool Ready { get { return boardRenderer != null && backgroundRenderer != null && boardRenderer.sharedMaterial != null && backgroundRenderer.sharedMaterial != null; } }

        private void OnEnable()
        {
            _camera = Camera.main;
            _revision = -1;
            _hover = -1;
            _width = 0;
            _shownTerritoryMove = -1;
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
            // 这是表现时钟：改变Time不会改变落子或胜负。正常局中按策划分段显地，终局/复盘常显。
            bool reveal = core != null && (core.Phase != GoPhase.Playing || service.ReplayStep >= 0 || Time.unscaledTime < _territoryUntil);
            _boardBlock.SetFloat("_ShowTerritory", service.HintsVisible && reveal ? 1 : 0);
            _boardBlock.SetFloat("_Clock", Time.unscaledTime);
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
            _camera.orthographicSize = Mathf.Max(6.4f, 5.2f / aspect);
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
                if (point >= 0)
                {
                    service.Preview(x, y);
                }
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
            int selected = service.SelectedPoint >= 0 ? service.SelectedPoint : _hover;
            int last = -1;
            if (core != null)
            {
                core.Analyze(_analysis);
                for (int i = 0; i < 81; i++)
                {
                    int liberties = _analysis.Liberties[i];
                    // 同一块只在代表点画一个气数徽标，其他点用负号保留叫吃标记，避免满盘重复数字。
                    _cells[i] = new Vector4((int)core.GetStone(i), _analysis.Group[i] == i ? liberties : -liberties,
                        _analysis.Territory[i], _analysis.Eye[i]);
                    _marks[i].w = core.DeadStones.Contains(i) ? 1 : 0;
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
                        int count = core.CopyLiberties(selected, _liberties);
                        for (int i = 0; i < count; i++)
                        {
                            _marks[_liberties[i]].x = 1;
                        }
                    }
                    else if (core.TryPreview(core.Turn, selected % 9, selected / 9, out GoPlayResult preview))
                    {
                        for (int i = 0; i < 81; i++)
                        {
                            _marks[i].y = preview.Captured.Contains(i) ? 1 : 0;
                        }
                    }
                }
                // 所有仅剩1气的棋块常显其唯一气口，玩家无需悬停也能发现危险。
                for (int i = 0; i < 81; i++)
                {
                    if (_analysis.Group[i] == i && _analysis.Liberties[i] <= 2)
                    {
                        int count = core.CopyLiberties(i, _liberties);
                        for (int j = 0; j < count; j++)
                        {
                            _marks[_liberties[j]].x = 1;
                        }
                    }
                }
                for (int i = 0; i < service.SuggestionCount; i++)
                {
                    GoHint hint = service.Suggestions[i];
                    _marks[hint.Y * 9 + hint.X].z = i + 1;
                }
                if (core.MoveNumber != _shownTerritoryMove && core.MoveNumber >= service.Config.territoryRevealFrom
                    && (core.MoveNumber - service.Config.territoryRevealFrom) % service.Config.territoryRevealEvery == 0)
                {
                    _shownTerritoryMove = core.MoveNumber;
                    _territoryUntil = Time.unscaledTime + 1.2f;
                }
            }
            _boardBlock.SetVectorArray("_Cells", _cells);
            _boardBlock.SetVectorArray("_Marks", _marks);
            _boardBlock.SetFloat("_ShowHints", service.HintsVisible ? 1 : 0);
            _boardBlock.SetFloat("_Selected", selected);
            _boardBlock.SetFloat("_Last", last);
            _boardBlock.SetFloat("_Turn", core == null ? 1 : (int)core.Turn);
            boardRenderer.SetPropertyBlock(_boardBlock);
        }
        private void OnDisable()
        {
            _revision = -1;
            _hover = -1;
            _territoryUntil = 0;
        }
    }
}
