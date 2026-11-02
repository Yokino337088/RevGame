// ============================================================
// EndlessArena.Placement.cs —— 六章同款绿色落子提示（新手先读这段）
//
// 六章每个棋格都是独立的 BoardCellView，高亮层用 PlacementMagic 共享材质（亮绿双环 + 角标 + 光珠）。
// 无尽棋盘是一整张网格，没有逐格对象，所以这里一次性建 81 个只带 SpriteRenderer 的小节点，
// 使用同一张高亮图和同一份 PlacementMagic.mat，不复制材质、不另写 Shader。
//
// 【格子对齐】PlacementMagic 按世界坐标算“在第几格、格内哪里”，格宽和原点来自材质（六章是 1.15 / -5.175）。
// 无尽格宽 1、原点 -0.5，所以用一个共享的 MaterialPropertyBlock 覆盖 _CellSize / _Origin，图案才会落在格子正中。
//
// 【刷新时机】只在“选中的牌/棋子、占格版本、阶段、手牌数、改阵步数”变化时重算 81 格，不是每帧。
// 是否能落子完全问 EndlessService.CanPlaceAt，与真正点击走同一套判断，亮的格子点下去一定成功。
// ============================================================
using RevGame.View;
using Revolution;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessArena
    {
        [Tooltip("六章落子提示原图（Art/Board），由无尽资产生成器装配。")]
        [SerializeField] private Sprite placementSprite;
        [Tooltip("六章 PlacementMagic 共享材质，由无尽资产生成器装配；运行时只用参数块，不克隆。")]
        [SerializeField] private Material placementMaterial;
        /// <summary>六章高亮层的排序（BoardCell 预制体里是 5）：盖在棋盘和外围敌潮上，压在棋子（9 以上）下面。</summary>
        private const int PlacementSortingOrder = 5;
        private static readonly int CellSizeId = Shader.PropertyToID("_CellSize");
        private static readonly int OriginId = Shader.PropertyToID("_Origin");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        /// <summary>81 个提示格，下标 = y * 9 + x。随竞技场复用，只建一次。</summary>
        private SpriteRenderer[] _placementCells;
        private MaterialPropertyBlock _placementProperties;
        /// <summary>上次刷新的状态指纹与图集绑定版本；都没变就跳过重算。</summary>
        private int _placementSignature = int.MinValue, _placementBinding = -1;
        /// <summary>验收用：当前亮着几格。</summary>
        public int PlacementHintCount
        {
            get;
            private set;
        }

        /// <summary>每帧调用，但只有状态变化时才真正改 81 个渲染器。</summary>
        private void UpdatePlacement(EndlessService service)
        {
            EndlessRun run = service.Run;
            bool selecting = run.selectedCard >= 0 || run.selectedUnit >= 0;
            int signature = -1;
            if (selecting)
            {
                unchecked
                {
                    signature = run.selectedCard * 397 ^ run.selectedUnit * 7919 ^ service.Board.TopologyVersion * 31 ^ (int)run.phase * 131 ^ run.hand.Count * 17 ^ run.slowMoves * 1009 ^ (run.slowLeft > 0 ? 1 << 30 : 0);
                }
            }
            if (signature == _placementSignature && _placementBinding == GameSpriteAtlasService.BindingVersion)
            {
                return;
            }
            if (!EnsurePlacementCells())
            {
                return;
            }
            _placementSignature = signature;
            int lit = 0;
            for (int y = 0; y < 9; y++)
            {
                for (int x = 0; x < 9; x++)
                {
                    bool visible = selecting && service.CanPlaceAt(x, y);
                    SpriteRenderer cell = _placementCells[y * 9 + x];
                    if (cell.enabled != visible)
                    {
                        cell.enabled = visible;
                    }
                    lit += visible ? 1 : 0;
                }
            }
            PlacementHintCount = lit;
        }

        /// <summary>首次使用时建 81 个提示节点；缺资源就报错并不再尝试，绝不显示白块。</summary>
        private bool EnsurePlacementCells()
        {
            if (placementSprite == null || placementMaterial == null)
            {
                if (_placementBinding != -2)
                {
                    _placementBinding = -2;
                    RevLog.Error("无尽落子提示缺少六章 PlacementMagic 资源，请重新执行无尽资产生成", "RevGame");
                }
                return false;
            }
            if (_placementCells == null)
            {
                _placementCells = new SpriteRenderer[81];
                _placementProperties = new MaterialPropertyBlock();
                for (int i = 0; i < 81; i++)
                {
                    var go = new GameObject("落子提示_" + i % 9 + "_" + i / 9);
                    go.transform.SetParent(transform, false);
                    // z=1.5：在棋盘网格（z=2）前、棋子（z=-0.5）后；排序层级另由 sortingOrder 保证。
                    go.transform.localPosition = new Vector3(i % 9, i / 9, 1.5f);
                    SpriteRenderer cell = go.AddComponent<SpriteRenderer>();
                    cell.sprite = placementSprite;
                    cell.sharedMaterial = placementMaterial;
                    cell.drawMode = SpriteDrawMode.Sliced;
                    cell.size = Vector2.one;
                    cell.sortingOrder = PlacementSortingOrder;
                    cell.enabled = false;
                    _placementCells[i] = cell;
                }
            }
            if (_placementBinding != GameSpriteAtlasService.BindingVersion)
            {
                _placementBinding = GameSpriteAtlasService.BindingVersion;
                // 设置参数块后 SpriteRenderer 不再自动提供 _MainTex，必须显式写入当前图集页，否则退回白图。
                _placementProperties.SetTexture(MainTexId, placementSprite.texture);
                _placementProperties.SetFloat(CellSizeId, 1f);
                _placementProperties.SetFloat(OriginId, -.5f);
                for (int i = 0; i < _placementCells.Length; i++)
                {
                    _placementCells[i].SetPropertyBlock(_placementProperties);
                }
            }
            return true;
        }

        /// <summary>退出无尽时熄灭全部提示，下次进入强制重算。</summary>
        private void ResetPlacement()
        {
            _placementSignature = int.MinValue;
            PlacementHintCount = 0;
            if (_placementCells == null)
            {
                return;
            }
            for (int i = 0; i < _placementCells.Length; i++)
            {
                _placementCells[i].enabled = false;
            }
        }
    }
}
