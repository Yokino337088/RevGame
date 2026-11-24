using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using RevGame.Services;
using Revolution;

/// <summary>把真实倍率表逐格同原公式比对，测试换配置、活棋增减和单独查询成本。</summary>
internal static class FirepowerTableRegression
{
    /// <summary>棋盘不可能同时占有比格子更多的棋子。</summary>
    private const int MaxPieces = Consts.BoardSize * Consts.BoardSize;
    /// <summary>单轮计时中的重复查询次数，避开单次读取时间太小的问题。</summary>
    private const int Lookups = 1_000_000;
    /// <summary>相同输入交替计时的轮数。</summary>
    private const int Repeats = 9;

    /// <summary>默认只运行等价断言。显式模式再计时，返回实际通过的断言数量。</summary>
    internal static int Run(string directory, bool benchmark = false)
    {
        int checks = 0;
        // 用位模式比较单精度结果。伤害最后会取整，早于取整的小差异也不能放过去。
        void Equal(float expected, float actual, string reason)
        {
            if (BitConverter.SingleToInt32Bits(expected) != BitConverter.SingleToInt32Bits(actual))
            {
                throw new InvalidOperationException("玩家火力查表不一致：" + reason);
            }
            checks++;
        }

        var alternate = ReadAlternate(directory);
        for (int count = -1; count <= MaxPieces + 1; count++)
        {
            Equal(Previous(count), Balance.PlayerFirepowerAt(count), "正式配置人数 " + count);
            Equal(Previous(count, alternate.PlayerFirepowerSoftCount, alternate.PlayerFirepowerExponent, alternate.PlayerFirepowerMinScale),
                alternate.PlayerFirepowerAt(count), "第二份配置人数 " + count);
        }

        // 从正式占格入口增减棋子，让真实拓扑版本变化。第零格测试空盘倍率。
        var battle = BattleService.CreateAttackRegressionFixture();
        int total = Math.Min(MaxPieces, Balance.PlayerFirepowerSoftCount + 2);
        var roster = new Piece[total];
        Equal(1f, battle.ReadRegressionFirepower(), "空盘");
        for (int i = 0; i < roster.Length; i++)
        {
            roster[i] = battle.AddRegressionPiece(PieceKind.Shield, StarTier.One, GameSide.Player,
                new BoardPos(i % Consts.BoardSize, i / Consts.BoardSize));
            Equal(Previous(i + 1), battle.ReadRegressionFirepower(), "加入棋子 " + (i + 1));
        }
        for (int i = roster.Length - 1; i >= 0; i--)
        {
            battle.RemoveRegressionPiece(roster[i]);
            Equal(Previous(i), battle.ReadRegressionFirepower(), "离场后活棋数 " + i);
        }
        Console.WriteLine("FirepowerTableRegression: " + checks + " bitwise assertions passed.");
        if (benchmark)
        {
            Measure();
        }
        return checks;
    }

    /// <summary>在独立表对象改参数，走生产读取与派生流程。全局正式配置和仓库文件保持原样。</summary>
    private static BalanceValues ReadAlternate(string directory)
    {
        string original = File.ReadAllText(Path.Combine(directory, "BalanceRule.txt"));
        string adjusted = Regex.Replace(original, @"(?m)^Player\.Firepower(SoftCount|Exponent)\t[^\t\r\n]+",
            match => "Player.Firepower" + match.Groups[1].Value + "\t" + (match.Groups[1].Value == "SoftCount" ? "1" : "3"));
        var table = new BalanceRuleTable();
        table.LoadText(adjusted, out int errors);
        var problems = new List<string>();
        BalanceValues values = BalanceValues.Read(table, problems);
        if (errors != 0 || problems.Count != 0 || values.PlayerFirepowerSoftCount != 1 || values.PlayerFirepowerExponent != 3)
        {
            throw new InvalidOperationException("替代火力表没有装配成功：" + string.Join("，", problems));
        }
        return values;
    }

    /// <summary>保留修改前正常路径的配置读取顺序，作为逐位验证与计时基线。</summary>
    private static float Previous(int count)
    {
        if (count <= Balance.PlayerFirepowerSoftCount)
        {
            return 1f;
        }
        double ratio = Balance.PlayerFirepowerSoftCount / (double)count;
        return (float)Math.Max(Balance.PlayerFirepowerMinScale, Math.Pow(ratio, Balance.PlayerFirepowerExponent));
    }

    /// <summary>用同一份原公式校验独立配置，避免基线错误地只读全局参数。</summary>
    private static float Previous(int count, int threshold, float exponent, float minimum)
    {
        return count <= threshold ? 1f : (float)Math.Max(minimum, Math.Pow(threshold / (double)count, exponent));
    }

    /// <summary>对高密度阵容循环查询相同人数序列，交替次序计时并保留校验和。</summary>
    private static void Measure()
    {
        var counts = new int[256];
        int first = Math.Min(MaxPieces, Balance.PlayerFirepowerSoftCount + 1);
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = first + i * 17 % (MaxPieces - first + 1);
        }

        var oldTicks = new double[Repeats];
        var newTicks = new double[Repeats];
        Func<int, float> baseline = Previous;
        Func<int, float> lookup = Balance.PlayerFirepowerAt;
        Time(baseline, counts, out _);
        Time(lookup, counts, out _);
        for (int i = 0; i < Repeats; i++)
        {
            double oldTotal, newTotal;
            if ((i & 1) == 0)
            {
                oldTicks[i] = Time(baseline, counts, out oldTotal);
                newTicks[i] = Time(lookup, counts, out newTotal);
            }
            else
            {
                newTicks[i] = Time(lookup, counts, out newTotal);
                oldTicks[i] = Time(baseline, counts, out oldTotal);
            }
            if (oldTotal != newTotal)
            {
                throw new InvalidOperationException("计时两组累计倍率不一致");
            }
        }
        Array.Sort(oldTicks);
        Array.Sort(newTicks);
        Console.WriteLine(FormattableString.Invariant(
            $"{Lookups} lookups/round, {Repeats} rounds, alive {first}-{MaxPieces}, median [min,max] ms: formula {oldTicks[Repeats / 2]:F3} [{oldTicks[0]:F3},{oldTicks[Repeats - 1]:F3}], table {newTicks[Repeats / 2]:F3} [{newTicks[0]:F3},{newTicks[Repeats - 1]:F3}]."));
    }

    /// <summary>两组共用循环与校验和，计时包含查配置及计算或查表。</summary>
    private static double Time(Func<int, float> path, int[] counts, out double total)
    {
        total = 0;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < Lookups; i++)
        {
            total += path(counts[i & (counts.Length - 1)]);
        }
        return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }
}
