// ============================================================
// GameLayoutChecks.cs —— 用典型手机与平板尺寸检查战斗区域的几何边界。
// 测的是“能否落在安全区、互不遮挡、反复计算不漂移”的约束，而不是复制排版公式。
// 游戏里仍需实际查看截图；几何断言不能代替字体、美术和真机手感验证。
// ============================================================
using System;
using RevGame.UI;
using UnityEngine;

namespace RevGame.Editor
{
    public static class GameLayoutChecks
    {
        public static string Run()
        {
            int count = 0;
            Vector2[] sizes = { new Vector2(1920, 1080), new Vector2(2340, 1080), new Vector2(3088, 1440), new Vector2(1440, 1080), new Vector2(1280, 720) };
            Rect[] safeAreas = { new Rect(0, 0, 1, 1), new Rect(0.03f, 0.02f, 0.96f, 0.96f), new Rect(0, 0, 0.95f, 1) };
            foreach (Vector2 size in sizes)
            {
                foreach (Rect safe in safeAreas)
                {
                    GameBattleLayout.Regions areas = GameBattleLayout.Calculate(size, safe);
                    Rect board = areas.Board;
                    Require(Mathf.Abs(board.width - board.height) < 0.01f, "棋盘必须保持正方形", ref count);
                    Require(Contains(areas.Safe, board) && Contains(areas.Safe, areas.Left) && Contains(areas.Safe, areas.Right), "区域越出设备安全区", ref count);
                    Require(!board.Overlaps(areas.Left) && !board.Overlaps(areas.Right) && !board.Overlaps(areas.Header), "侧栏/标题遮挡棋盘", ref count);
                    Rect hint = GameBattleLayout.InRegion(areas.Left, 0.92f, 1f, 0.15f);
                    Rect hand = GameBattleLayout.InRegion(areas.Left, 0.50f, 1f, 0.65f);
                    Rect assist = GameBattleLayout.InRegion(areas.Left, 0.065f, 1f, 0.125f);
                    Require(!hint.Overlaps(hand) && !hand.Overlaps(assist), "六张手牌与提示重叠", ref count);
                    CheckHandCards(areas, hand, ref count);
                    Rect notice = GameBattleLayout.InRegion(areas.Right, 0.56f, 0.86f, 0.14f);
                    Rect info = GameBattleLayout.InRegion(areas.Right, 0.78f, 1f, 0.22f);
                    Require(!notice.Overlaps(info), "增益提示遮挡信息栏", ref count);
                    float[] buttonY = { 0.06f, 0.18f, 0.30f, 0.42f };
                    for (int i = 0; i < buttonY.Length; i++)
                    {
                        Rect button = GameBattleLayout.InRegion(areas.Right, buttonY[i], 0.94f, 0.10f);
                        Require(Contains(areas.Right, button) && !button.Overlaps(notice), "操作按钮越界或遮挡增益提示", ref count);
                        if (i > 0)
                        {
                            Require(!button.Overlaps(GameBattleLayout.InRegion(areas.Right, buttonY[i - 1], 0.94f, 0.10f)), "操作按钮互相遮挡", ref count);
                        }
                    }
                    Require(GameBattleLayout.Calculate(size, safe).Board == board, "相同尺寸重新排版发生漂移", ref count);
                }
            }
            Rect normalized = GameBattleLayout.NormalizeSafeArea(new Rect(89, 0, 2999, 1440), new Vector2(3088, 1440));
            Require(Mathf.Abs(normalized.xMin - 89f / 3088f) < 0.00001f && normalized.xMax == 1f, "模拟设备安全区归一化错误", ref count);
            Require(GameBattleLayout.NormalizeSafeArea(Rect.zero, Vector2.zero) == new Rect(0, 0, 1, 1), "零尺寸初始化没有安全兜底", ref count);
            return "通过 " + count + " 条横屏布局断言（5种尺寸×3种安全区、六牌间距/放大边界/重复排版、侧栏/棋盘/按钮/提示不重叠）";
        }

        /// <summary>检查真实排版返回的六张牌，避免只验证手牌区域却漏掉区域内部的拥挤。</summary>
        private static void CheckHandCards(GameBattleLayout.Regions areas, Rect hand, ref int count)
        {
            Rect[] cards = new Rect[6];
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i] = GameBattleLayout.HandCard(hand, areas.Safe.height, i);
                Require(Contains(hand, cards[i]), "第 " + (i + 1) + " 张手牌超出手牌区域", ref count);
                // 重排必须从区域和索引重新计算，不能在上次坐标上累加，否则反复调整窗口会越排越偏。
                Require(GameBattleLayout.HandCard(hand, areas.Safe.height, i) == cards[i], "六张手牌重复排版发生漂移", ref count);
                for (int previous = 0; previous < i; previous++)
                {
                    Require(!cards[i].Overlaps(cards[previous]), "手牌彼此遮挡", ref count);
                }
            }

            for (int row = 0; row < 3; row++)
            {
                Rect left = cards[row * 2];
                Rect right = cards[row * 2 + 1];
                // “没有重叠”不代表间距足够。至少保留四分之一张牌宽，让玩家能清楚分辨两列。
                // 用牌宽作为参照，测试在高清手机和较小窗口中表达的是同一种视觉要求。
                float visibleGap = right.xMin - left.xMax;
                Require(visibleGap >= Mathf.Max(left.width, right.width) * 0.25f, "手牌两列的水平间距不足四分之一张牌宽", ref count);

                // 选牌反馈会围绕卡片中心放大到 1.08 倍，因此检查动画峰值，而不只是静止状态。
                // 此处只约束同行横向；上下三行保持原有节奏，不要求六张牌同时放大时纵向也留空。
                Rect enlargedLeft = ScaleAroundCenter(left, 1.08f);
                Rect enlargedRight = ScaleAroundCenter(right, 1.08f);
                Require(enlargedLeft.xMax <= enlargedRight.xMin, "同行手牌放大时互相遮挡", ref count);
                Require(ContainsHorizontally(areas.Left, enlargedLeft) && ContainsHorizontally(areas.Left, enlargedRight), "手牌放大时横向越出左侧操作区", ref count);
            }
        }

        private static Rect ScaleAroundCenter(Rect source, float scale)
        {
            Vector2 size = source.size * scale;
            return new Rect(source.center - size * 0.5f, size);
        }

        private static bool ContainsHorizontally(Rect outer, Rect inner)
        {
            const float tolerance = 0.01f;
            return inner.xMin >= outer.xMin - tolerance && inner.xMax <= outer.xMax + tolerance;
        }

        private static bool Contains(Rect outer, Rect inner)
        {
            const float tolerance = 0.01f;
            return inner.xMin >= outer.xMin - tolerance && inner.yMin >= outer.yMin - tolerance && inner.xMax <= outer.xMax + tolerance && inner.yMax <= outer.yMax + tolerance;
        }

        private static void Require(bool condition, string message, ref int count)
        {
            if (!condition) { throw new InvalidOperationException(message); }
            count++;
        }
    }
}
