// ============================================================
// BoardCellView.cs —— 一格的图像和点击转发器。
// 每格直接记住 BoardPos，点击时无需用浮点世界坐标反推，避免边界点错格。
// 它只保存显示状态，不保存棋子或修改棋盘；落子请求交给上层回调。
// ============================================================
using System;
using RevGame.Core;
using Revolution;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RevGame.View
{
    [RevPoolPrefab(RevResPath.Board_Tile, "BoardCell", Group = RevResGroup.Battle)]
    public sealed class BoardCellView : MonoBehaviour, IPointerClickHandler, IRevPoolable
    {
        [SerializeField]
        private SpriteRenderer starRenderer;
        [SerializeField]
        private SpriteRenderer highlightRenderer;
        [SerializeField]
        private SpriteRenderer eyeRenderer;
        private Action<BoardPos> _onClick;
        public BoardPos Position { get; private set; }

        public bool IsHighlighted
        {
            get
            {
                return highlightRenderer != null && highlightRenderer.enabled;
            }
        }

        /// <summary>绑定显示位置与点击去向；不在这里判断游戏规则。</summary>
        public void Bind(BoardPos pos, bool isStar, Action<BoardPos> onClick)
        {
            Position = pos;
            _onClick = onClick;
            transform.position = pos.ToWorldPosition(Consts.CellSize, Consts.BoardOriginOffset);
            if (starRenderer != null)
            {
                starRenderer.enabled = isStar;
            }

            SetHighlight(false);
            SetEye(false);
        }

        public void SetHighlight(bool visible)
        {
            if (highlightRenderer != null)
            {
                highlightRenderer.enabled = visible;
            }
        }

        public void SetEye(bool visible)
        {
            if (eyeRenderer != null)
            {
                eyeRenderer.enabled = visible;
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            if (_onClick != null)
            {
                _onClick(Position);
            }
        }

        public void OnPoolGet()
        {
        // 实际位置由 Bind 设置，出池时不要读取上一局的服务。
        }

        public void OnPoolReturn()
        {
            // ★ 委托会引用棋盘宿主，回池时必须摘掉，否则旧场景仍可能被保留在内存中。
            _onClick = null;
            SetHighlight(false);
            SetEye(false);
        }
    }
}
