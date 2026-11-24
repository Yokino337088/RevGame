#if UNITY_EDITOR
using System;
using System.Collections;
using RevGame.Pvp;
using Revolution;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace RevGame.Bootstrap
{
    internal static partial class MathAbScenarios
    {
        /// <summary>固定黑方输入的交叉点。数组下标是第几轮黑方落子，格号为 y×9+x。</summary>
        private static readonly int[] PvpBlackPoints = { 40, 16, 64, 24, 56, 30, 50, 10 };

        /// <summary>旧版同一组黑方输入的正式电脑回手，逐手检查预告和实际落点。</summary>
        private static readonly int[] PvpWhitePoints = { 20, 21, 33, 57, 60, 34, 48, 59 };

        /// <summary>旧版最后一轮白方提交后的完整棋规指纹。时钟延迟作为独立指标统计。</summary>
        private const ulong PvpExpectedFinalHash = 11122832533821899427UL;

        /// <summary>从本机动作到落子反馈的最长允许等待，单位秒。超时判定为失败，不纳入统计样本。</summary>
        private const int PvpDeadlineSeconds = 6;

        /// <summary>棋盘实际着色器上用于提示即将落子的格号。</summary>
        private static readonly int PvpPreviewProperty = Shader.PropertyToID("_ComputerPreview");

        /// <summary>棋盘实际着色器上用于触发棋子落下动画的格号。</summary>
        private static readonly int PvpLandingProperty = Shader.PropertyToID("_ComputerLand");

        /// <summary>在已进入的空白本地人机盘重复固定棋谱。通过真实服务提交黑子，由原游戏的轮询和棋盘渲染完成白子。</summary>
        internal static IEnumerator Pvp(MathAbRun run)
        {
            var service = RevServices.GetRequired<PvpService>();
            var panel = RevUI.Get<PvpPanel>();
            var view = panel?.BoardForCheck;
            if (service.Mode != PvpConnectionMode.Local || !service.IsComputerGame || service.AgentSettings.Enabled ||
                service.Core?.Phase != GoPhase.Playing || service.Core.Sequence != 0 || service.Core.KomiHalfPoints != 15 ||
                panel?.BattleReady != true || view == null || !view.isActiveAndEnabled)
            {
                throw new InvalidOperationException("九路对照需要已打开的空白本地人机棋盘、七目半贴目和关闭的云端对手");
            }

            // 在开始计时前取预制体真正绑定的渲染器，不在游戏画面添加额外控件。
            MeshRenderer renderer;
            using (var serialized = new SerializedObject(view))
            {
                renderer = serialized.FindProperty("boardRenderer")?.objectReferenceValue as MeshRenderer;
            }
            if (renderer == null || !renderer.enabled)
            {
                throw new InvalidOperationException("九路棋盘实际渲染器不可用");
            }
            var block = new MaterialPropertyBlock();

            // 让面板过场之后的第一帧结束，计时窗只覆盖已进入的对局。
            yield return null;
            run.BeginFrames();
            try
            {
                for (int i = 0; i < PvpBlackPoints.Length; i++)
                {
                    int black = PvpBlackPoints[i];
                    int white = PvpWhitePoints[i];
                    int blackSequence = i * 2 + 1;
                    long started = Stopwatch.GetTimestamp();
                    if (service.Core.Turn != GoSide.Black ||
                        !service.Submit(GoAction.Play, black % GoCore.BoardSize, black / GoCore.BoardSize) ||
                        service.Core.Sequence != blackSequence)
                    {
                        throw new InvalidOperationException("九路固定黑子未被提交，轮次 " + i + " " + service.Status);
                    }

                    // 只从基线定义的白棋预告格收反馈，落子前的固定节奏继续由服务推进。
                    long deadline = started + PvpDeadlineSeconds * Stopwatch.Frequency;
                    while (service.ComputerPreviewPoint != white || !PvpRendererHasPoint(renderer, block, PvpPreviewProperty, white))
                    {
                        if (Stopwatch.GetTimestamp() >= deadline || service.Core?.Sequence != blackSequence)
                        {
                            throw new InvalidOperationException("九路电脑预告格或反馈等待不一致，轮次 " + i);
                        }
                        yield return null;
                    }
                    run.RecordFeedback("黑棋提交到白棋预告写入渲染器", started);
                    int usedPartitions = service.TacticalSearchPartitions;

                    // 确认白棋已经按原节奏提交，并且棋盘实际读到了该格的落子动画参数。
                    while (service.ComputerLandingPoint != white || !PvpRendererHasPoint(renderer, block, PvpLandingProperty, white))
                    {
                        if (Stopwatch.GetTimestamp() >= deadline || service.Core?.Sequence > blackSequence + 1)
                        {
                            throw new InvalidOperationException("九路电脑落子或表现提交不一致，轮次 " + i);
                        }
                        yield return null;
                    }
                    run.RecordFeedback("黑棋提交到白棋落子写入渲染器", started);
                    if (service.Core.Sequence != blackSequence + 1 || service.Core.Turn != GoSide.Black ||
                        service.Moves.Count != blackSequence + 1)
                    {
                        throw new InvalidOperationException("九路棋谱序号、回合或招数不一致，轮次 " + i);
                    }

                    PvpMove blackMove = service.Moves[blackSequence - 1];
                    PvpMove whiteMove = service.Moves[blackSequence];
                    // 实际耗时不属于棋谱一致性。留存两手各自的动作及前后指纹，同时核对累计提子。
                    run.RecordRule($"九路{PvpMoveIdentity(blackMove)}|{PvpMoveIdentity(whiteMove)}|盘面={service.Core.PositionHash}|黑提={service.Core.BlackCaptures}|白提={service.Core.WhiteCaptures}|分组={usedPartitions}");
                    yield return null;
                }
                if (service.Core.StateHash != PvpExpectedFinalHash)
                {
                    throw new InvalidOperationException("九路固定棋谱最后一手的棋规指纹与基线不一致");
                }
            }
            finally
            {
                run.EndFrames();
            }
        }

        /// <summary>复用一个属性块看棋盘渲染命令的参数，确认反馈已提交到 Unity 渲染器。</summary>
        private static bool PvpRendererHasPoint(MeshRenderer renderer, MaterialPropertyBlock block, int propertyId, int point)
        {
            if (renderer == null || !renderer.enabled)
            {
                return false;
            }
            renderer.GetPropertyBlock(block);
            return block.HasFloat(propertyId) && block.GetFloat(propertyId) == point;
        }

        /// <summary>构造稳定棋谱身份，保留决定规则结果的字段，排除每局实际经过的时间。</summary>
        private static string PvpMoveIdentity(PvpMove move)
        {
            return $"{move.sequence},{move.side},{move.kind},{move.x},{move.y},{move.before},{move.after}";
        }
    }
}
#endif
