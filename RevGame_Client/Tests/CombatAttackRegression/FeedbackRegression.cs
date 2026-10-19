// 验证真实表现候选缓冲的公平性。这里的整数仅代表不可变攻击快照，不创建 Unity 物体。
using System;
using System.Collections.Generic;
using RevGame.View;

internal static class FeedbackRegression
{
    private static int _assertions;

    public static int Run()
    {
        VerifyBoundAndRotation(12, 1);
        VerifyBoundAndRotation(24, 4);
        VerifyOldFifoAdmissionBaseline();
        VerifyMergeAndBurst();
        VerifyPauseExpiryAndClear();
        Console.WriteLine("FairFeedbackBuffer: " + _assertions + " assertions passed (12/24 capacity, 40 sources, pause/expiry/clear).");
        return _assertions;
    }

    /// <summary>
    /// 模拟四十颗棋子始终按编号顺序齐射，数量超过手机预算。
    /// 若回到旧的先来先得算法，靠后棋子会一直抢不到名额，此处会稳定失败。
    /// </summary>
    private static void VerifyBoundAndRotation(int capacity, int perSource)
    {
        var feedback = new FairFeedbackBuffer<int>(capacity, perSource, 0.5f);
        var displayed = new HashSet<int>();
        float nextOffer = 0f;
        float nextDisplay = 0f;
        for (int step = 0; step <= 800; step++)
        {
            float now = step * 0.01f;
            if (now + 0.0001f >= nextOffer)
            {
                for (int id = 1; id <= 40; id++)
                {
                    feedback.Offer(id, id, now);
                    Require(feedback.Count <= capacity, capacity + " 容量不能因齐射无限增长");
                }
                nextOffer += 0.25f;
            }
            if (now + 0.0001f >= nextDisplay)
            {
                if (feedback.TryPeek(now, 0.5f, out int snapshot, out int id))
                {
                    Require(snapshot == id, "公平调度不能混淆棋子的攻击快照");
                    displayed.Add(id);
                    feedback.MarkDisplayed(id, now);
                }
                nextDisplay += 0.12f;
            }
        }
        Require(displayed.Count == 40, capacity + " 容量下，连续固定顺序齐射的 40 颗棋子应全部轮到显示；实际 " + displayed.Count);
        Require(displayed.Contains(40), "最后落下的一星棋子不能永久饿死");
    }

    /// <summary>口号每颗只保留最新一条；弹道保留最多四发连射，但不能被一颗高攻速棋子塞满。</summary>
    private static void VerifyMergeAndBurst()
    {
        var words = new FairFeedbackBuffer<int>(12, 1, 0.5f);
        words.Offer(7, 10, 0f);
        words.Offer(7, 20, 0.1f);
        Require(words.Count == 1, "同源口号应合并为一条");
        Require(words.TryPeek(0.2f, 0f, out int snapshot, out int id) && snapshot == 20 && id == 7, "口号应显示最新的真实攻击快照");
        var projectiles = new FairFeedbackBuffer<int>(24, 4, 0.5f);
        for (int i = 1; i <= 10; i++)
        {
            projectiles.Offer(1, i, i * 0.01f);
        }
        Require(projectiles.Count == 4, "单颗棋子的弹道连射候选最多四份");
        projectiles.Offer(2, 100, 0.1f);
        Require(projectiles.Count == 5, "其他棋子仍能进入弹道队列");
        projectiles.Clear();
        for (int i = 0; i < 120; i++)
        {
            projectiles.Offer(0, i, 0.2f);
        }
        Require(projectiles.Count == 24, "没有攻击者编号的旧事件仍可齐射，但必须受总量上限约束");
    }

    /// <summary>
    /// 用同一组固定输入比较“旧式满队拒收”与真实新缓冲。
    /// 旧侧只是十二容量 FIFO 入队/过期规则的摘要模拟，不是旧 BoardView 的完整复刻；
    /// 不涉及其对象池、异步加载、UI 布局或最终渲染，因此只用来证明固定顺序入队的饥饿机制。
    /// 新侧直接调用真实 FairFeedbackBuffer，避免把修复算法复制到测试中自证正确。
    /// </summary>
    private static void VerifyOldFifoAdmissionBaseline()
    {
        var oldFifo = new Queue<(int Id, float CreatedAt)>();
        var current = new FairFeedbackBuffer<int>(12, 1, 0.5f);
        var oldDisplayed = new HashSet<int>();
        var newDisplayed = new HashSet<int>();
        int oldPeak = 0;
        int newPeak = 0;
        // 整数毫秒安排事件，避免小数累计误差意外改变齐射与显示的先后顺序。
        // 每 625ms：二十来源按 1→20 齐射；每 120ms：最多接受一份显示；候选最多等半秒。
        for (int milliseconds = 0; milliseconds <= 10000; milliseconds++)
        {
            float now = milliseconds * 0.001f;
            while (oldFifo.Count > 0 && now - oldFifo.Peek().CreatedAt > 0.5f)
            {
                oldFifo.Dequeue();
            }
            if (milliseconds % 625 == 0)
            {
                for (int id = 1; id <= 20; id++)
                {
                    // 摘录旧队列的关键约束：满十二份后直接拒收后续来源，不根据已显示历史换位。
                    if (oldFifo.Count < 12)
                    {
                        oldFifo.Enqueue((id, now));
                    }
                    current.Offer(id, id, now);
                    oldPeak = Math.Max(oldPeak, oldFifo.Count);
                    newPeak = Math.Max(newPeak, current.Count);
                }
            }
            if (milliseconds % 120 == 0)
            {
                if (oldFifo.Count > 0)
                {
                    oldDisplayed.Add(oldFifo.Dequeue().Id);
                }
                if (current.TryPeek(now, 0.5f, out _, out int id))
                {
                    newDisplayed.Add(id);
                    current.MarkDisplayed(id, now);
                }
            }
        }
        Require(oldPeak == 12 && newPeak == 12, "旧摘要与真实新缓冲使用相同十二容量，不能靠扩容掩盖饥饿");
        Require(oldDisplayed.Count <= 12 && !oldDisplayed.Contains(20), "旧式固定顺序满队拒收应能复现后段来源饥饿");
        Require(newDisplayed.Count == 20 && newDisplayed.Contains(20), "相同二十来源/0.625秒输入下，真实新缓冲应让所有来源轮到显示");
        Console.WriteLine("FIFO admission summary baseline (not full old BoardView): " + oldDisplayed.Count + "/20 sources; real FairFeedbackBuffer: " + newDisplayed.Count + "/20 sources (0.625s volleys, 10s).");
    }

    /// <summary>预算暂停时只查看不消费；超时后丢弃旧战况，Clear 同时重置候选和公平历史。</summary>
    private static void VerifyPauseExpiryAndClear()
    {
        var feedback = new FairFeedbackBuffer<int>(12, 1, 0.5f);
        feedback.Offer(1, 11, 0f);
        Require(feedback.TryPeek(0.1f, 0.5f, out int first, out int source), "预算满时仍可查看候选");
        Require(feedback.TryPeek(0.4f, 0.5f, out int second, out int secondSource), "短暂预算暂停不应丢掉候选");
        Require(first == second && source == secondSource && feedback.Count == 1, "未成功预约表现前不能消费快照");
        Require(!feedback.TryPeek(0.51f, 0.5f, out _, out _) && feedback.Count == 0, "超过半秒不补播旧战况");
        feedback.Offer(1, 12, 1f);
        Require(feedback.TryPeek(1f, 0.5f, out _, out _), "新的战况可重新显示");
        feedback.MarkDisplayed(1, 1f);
        feedback.Offer(1, 13, 1.1f);
        feedback.Offer(2, 21, 1.1f);
        Require(feedback.TryPeek(1.1f, 0.5f, out _, out int ready) && ready == 2, "同源冷却不能阻塞其他棋子");
        feedback.Clear();
        Require(feedback.Count == 0, "退出回合必须清空候选");
        feedback.Offer(1, 14, 1.1f);
        Require(feedback.TryPeek(1.1f, 0.5f, out _, out _), "Clear 必须清掉上一回合的来源冷却");
    }

    private static void Require(bool condition, string message)
    {
        _assertions++;
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
