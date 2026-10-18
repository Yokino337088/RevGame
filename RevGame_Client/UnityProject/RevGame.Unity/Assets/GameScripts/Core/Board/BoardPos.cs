// ============================================================
// BoardPos.cs
// 规则统一使用整数格坐标，避免用世界浮点位置推算棋盘造成半格偏移。
// ============================================================
using System;

namespace RevGame.Core
{
    /// <summary>
    /// 棋盘坐标（格子坐标，不是世界坐标）。
    ///
    /// 为什么不用 Vector2Int：
    /// 1) 它是 Unity 类型，纯 C# 逻辑（规则、AI、单测）里不该依赖引擎；
    /// 2) 这里要重写 <see cref = "Equals"/> / <see cref = "GetHashCode"/> 才能当 Dictionary 的 key，
    ///    Vector2Int 虽然也能用，但语义上"格子"和"向量"是两回事，混用迟早写错。
    ///
    /// 坐标约定：左下角是 (0,0)，X 向右，Y 向上。
    /// </summary>
    public readonly struct BoardPos : IEquatable<BoardPos>
    {
        /// <summary>X 轴（列）。</summary>
        public readonly int X;
        /// <summary>Y 轴（行）。</summary>
        public readonly int Y;
        public BoardPos(int x, int y)
        {
            X = x;
            Y = y;
        }

        /// <summary>把这个坐标压成一维索引（只在需要线性存储时用，例如存档）。</summary>
        public int ToIndex(int boardSize)
        {
            return Y * boardSize + X;
        }

        /// <summary>从一维索引还原坐标。</summary>
        public static BoardPos FromIndex(int index, int boardSize)
        {
            return new BoardPos(index % boardSize, index / boardSize);
        }

        /// <summary>是否落在棋盘内。</summary>
        public bool IsInside(int boardSize)
        {
            return X >= 0 && Y >= 0 && X < boardSize && Y < boardSize;
        }

        /// <summary>
        /// 四邻居（上下左右）。围棋的"气"只看四邻居，**不含斜角** —— 这是规则不是习惯，
        /// 写成八邻居会让"眼"的判定全错。
        /// </summary>
        public BoardPos[] GetOrthogonalNeighbors()
        {
            return new[]
            {
                new BoardPos(X + 1, Y),
                new BoardPos(X - 1, Y),
                new BoardPos(X, Y + 1),
                new BoardPos(X, Y - 1),
            };
        }

        /// <summary>转换成世界坐标（棋盘中心为原点）。</summary>
        public UnityEngine.Vector3 ToWorldPosition(float cellSize, float originOffset)
        {
            return new UnityEngine.Vector3(originOffset + (X + 0.5f) * cellSize, originOffset + (Y + 0.5f) * cellSize, 0f);
        }

        /// <summary>世界坐标反算格子（点在哪一格）。</summary>
        public static BoardPos FromWorldPosition(UnityEngine.Vector3 world, float cellSize, float originOffset)
        {
            int x = UnityEngine.Mathf.FloorToInt((world.x - originOffset) / cellSize);
            int y = UnityEngine.Mathf.FloorToInt((world.y - originOffset) / cellSize);
            return new BoardPos(x, y);
        }

        public bool Equals(BoardPos other)
        {
            return X == other.X && Y == other.Y;
        }

        public override bool Equals(object obj)
        {
            return obj is BoardPos other && Equals(other);
        }

        public override int GetHashCode()
        {
            // 棋盘最大 9×9，用 (Y << 8) ^ X 足够且无冲突（X/Y 都 < 256）。
            return (Y << 8) ^ X;
        }

        public static bool operator ==(BoardPos a, BoardPos b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(BoardPos a, BoardPos b)
        {
            return !a.Equals(b);
        }

        public override string ToString()
        {
            return "(" + X + "," + Y + ")";
        }
    }
}
