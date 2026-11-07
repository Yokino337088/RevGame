using System;
using RevGame.Pvp.Agent;

namespace RevGame.Pvp
{
    public sealed partial class PvpService
    {
        /// <summary>设置归服务而非面板所有；关闭设置不清Key，由启动入口恢复本地加密配置。棋谱与网络协议不包含凭据。</summary>
        public PvpAgentSettings AgentSettings
        {
            get;
        } = new PvpAgentSettings();
        public bool AgentControlsHumanSeat
        {
            get;
            private set;
        }
        public string AgentStatus
        {
            get;
            private set;
        } = "Agent 未启用";
        private IGoAgentRequest _agentRequest;
        private GoAgentPosition _agentPosition;
        private int _agentGeneration;
        private int _requestGeneration;
        private GoSide _requestSide;
        private double _requestStarted;
        // 工程通信护栏，防平台HTTP超时未回调；不改变导表的对局计时规则。
        private const double DecisionDeadlineSeconds = 60;
        private double _decisionBudget;
        private int _consecutiveFailures;
        private double _cooldownUntil;
        public string AgentLastFailure
        {
            get;
            private set;
        } = "";
        public double AgentLastSeconds
        {
            get;
            private set;
        }

        /// <summary>一个时钟预算内只尝试一次；连续失败进入冷却，避免每手都付出完整等待。</summary>
        private void AgentFailure(string reason)
        {
            AgentLastSeconds = Math.Max(0, _clock.Elapsed.TotalSeconds - _requestStarted);
            AgentLastFailure = reason;
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
            {
                _cooldownUntil = _clock.Elapsed.TotalSeconds + 60;
            }
            AgentStatus = reason + "（" + AgentLastSeconds.ToString("F1") + "秒），本手使用本地策略";
            Revolution.RevLog.Warn("Agent 序号=" + Core.Sequence + " 原因=" + reason + " 用时秒=" + AgentLastSeconds.ToString("F1"), "PvpAgent");
            _agentRequest?.Dispose();
            _agentRequest = null;
            _fallbackSequence = Core.Sequence;
            _fallbackSide = _requestSide;
        }
        // 失败仅回退当前一手，下个回合可重新请求。序号避免每帧重复付费重试。
        private int _fallbackSequence = -1;
        private GoSide _fallbackSide;
        internal Func<PvpAgentSettings, GoAgentPosition, IGoAgentRequest> AgentRequestFactory = DeepSeekGoRequest.Start;
        private bool UseCloudOpponent => IsComputerGame && AgentSettings.Enabled;

        /// <summary>只有联机自己的座位可托管；本地人机玩家永远手动，电脑只控制白方。</summary>
        public bool SetAgentControl(bool enabled)
        {
            if (enabled && (Mode == PvpConnectionMode.Local || Core == null || Core.Phase == GoPhase.Finished || !AgentSettings.Ready))
            {
                AgentStatus = "托管仅支持联机对局；请先在 Agent 设置保存 Key、模型并授权云端调用";
                return false;
            }
            CancelAgent();
            AgentControlsHumanSeat = enabled;
            AgentStatus = enabled ? "Agent 已接管本方；可随时停止" : "已停止托管，恢复手动落子";
            Revision++;
            return true;
        }

        /// <summary>配置变更必须使在途结果过期，避免使用玩家刚撤销的授权。</summary>
        public bool ConfigureAgent(string key, string model, bool enabled)
        {
            if (!AgentSettings.TrySet(key, model, enabled, out string reason))
            {
                AgentStatus = reason;
                return false;
            }
            CancelAgent();
            _consecutiveFailures = 0;
            _cooldownUntil = 0;
            if (!enabled)
            {
                AgentControlsHumanSeat = false;
            }
            AgentStatus = enabled ? "云端对手已启用；本地人机玩家保持手动" : "云端调用已关闭";
            Revision++;
            return true;
        }

        /// <summary>生产设置入口：先验证和写入磁盘，成功后才替换运行状态，避免显示“已保存”但重启丢失。</summary>
        public bool ConfigureSavedAgent(string key, string model, bool enabled)
        {
            var candidate = new PvpAgentSettings();
            if (!candidate.TrySet(key, model, enabled, out string reason))
            {
                AgentStatus = reason;
                return false;
            }
            try
            {
                PvpAgentSettingsStore.Save(candidate);
            }
            catch (Exception)
            {
                AgentStatus = "本地保存失败：当前平台需支持Windows凭据保护，原配置未变更";
                return false;
            }
            ConfigureAgent(candidate.Key, candidate.Model, candidate.Enabled);
            AgentStatus = enabled ? "已加密保存到本机，重启后自动恢复" : "云端已关闭，本地Key已清除";
            return true;
        }

        /// <summary>只由正式Bootstrap装配后调用；隔离棋局不自动读取玩家凭据，测试不会触发真实云端。</summary>
        public void RestoreSavedAgent()
        {
            if (!PvpAgentSettingsStore.Load(out PvpAgentSettings restored, out string reason))
            {
                AgentStatus = reason;
                return;
            }
            ConfigureAgent(restored.Key, restored.Model, restored.Enabled);
            if (restored.Ready)
            {
                AgentStatus = "已恢复本地加密Agent配置";
            }
        }

        private void CancelAgent()
        {
            _agentGeneration++;
            _agentRequest?.Dispose();
            _agentRequest = null;
            _agentPosition = null;
            _fallbackSequence = -1;
        }

        /// <summary>
        /// 与网络轮询共用PVP域的主线程Tick，不创建线程或另一套异步调度器。
        /// 每手仅一项HTTP操作；退出、切控制方式、同步换盘后，旧结果不能再次进入提交入口。
        /// </summary>
        private void PollAgent()
        {
            GoSide side = IsComputerGame ? GoSide.White : MySide;
            bool active = UseCloudOpponent || AgentControlsHumanSeat;
            if (active && Core != null && Core.Phase == GoPhase.Finished)
            {
                AgentStatus = "对局已结束：" + Core.EndReason + "，空格不代表必须继续落子";
            }
            if (!active || Core == null || _leaving || _disposed || (Mode != PvpConnectionMode.Local && !Connected) || Core.Phase == GoPhase.Finished ||
                (Core.Phase == GoPhase.Playing && Core.Turn != side) ||
                (Core.Phase == GoPhase.Scoring && Core.HasConfirmed(side)))
            {
                if (_agentRequest != null)
                {
                    CancelAgent();
                }
                return;
            }
            if (_agentRequest != null && !_agentPosition.Matches(Core))
            {
                CancelAgent();
            }
            if (_agentRequest != null && _clock.Elapsed.TotalSeconds - _requestStarted >= _decisionBudget)
            {
                AgentFailure("达到本手等待预算");
            }
            // Fantasy心跳会短暂设置Busy，不能把传输等待当成撤销授权，否则长思考会被反复取消并计费。
            // 保留同一请求，等传输空闲后再消费响应；只有真正断线/换盘/退出才取消。
            if (!CanAct)
            {
                return;
            }
            // 联机数子不是轮流落子。若双方同时按同一序号确认，会各自提交第六手并分叉。
            // 自动方固定黑先白后；白方等待已同步的黑方确认，仍允许人工先行确认。
            if (Mode != PvpConnectionMode.Local && Core.Phase == GoPhase.Scoring && side == GoSide.White && !Core.HasConfirmed(GoSide.Black))
            {
                AgentStatus = "双方已停一手，数子阶段：等待黑方确认后白方确认";
                return;
            }
            // 思考照常计时。不能让无响应的网络阻止本地电脑超时结算。
            if (Core.Phase == GoPhase.Playing && _goClock.IsTimedOut(side, _clock.Elapsed.TotalSeconds - _turnStarted))
            {
                CancelAgent();
                SubmitActor(GoAction.Timeout, side);
                return;
            }
            if (_fallbackSequence == Core.Sequence && _fallbackSide == side)
            {
                ApplyAgentFallback(side);
                return;
            }
            try
            {
                if (_agentRequest == null)
                {
                    if (_clock.Elapsed.TotalSeconds < _cooldownUntil)
                    {
                        AgentStatus = "云端连续失败冷却中，使用本地合法策略";
                        ApplyAgentFallback(side);
                        return;
                    }
                    _agentPosition = GoAgentPosition.Create(Core, side);
                    _requestStarted = _clock.Elapsed.TotalSeconds;
                    // 留出提交与同步余量。读秒较短时主动回退，不能因扩大网络等待而白送超时负。
                    _decisionBudget = Core.Phase == GoPhase.Playing
                        ? Math.Min(DecisionDeadlineSeconds, Math.Max(.05, _goClock.PeekRemaining(side, _requestStarted - _turnStarted) * .8))
                        : DecisionDeadlineSeconds;
                    _requestGeneration = _agentGeneration;
                    _requestSide = side;
                    _agentRequest = AgentRequestFactory(AgentSettings, _agentPosition);
                    AgentStatus = "Agent 正在思考（计时继续）";
                }
                if (!_agentPosition.Matches(Core) || _requestGeneration != _agentGeneration || _requestSide != side)
                {
                    CancelAgent();
                    return;
                }
                if (!_agentRequest.IsDone)
                {
                    return;
                }
                string content = _agentRequest.ReadContent();
                if (!_agentPosition.TryReadAction(content, out GoAgentAction selected))
                {
                    throw new GoAgentRequestException("模型返回非法动作或空白JSON");
                }
                _agentRequest.Dispose();
                _agentRequest = null;
                AgentLastSeconds = _clock.Elapsed.TotalSeconds - _requestStarted;
                AgentLastFailure = "";
                _consecutiveFailures = 0;
                bool accepted = SubmitActor(selected.Kind, side, selected.X, selected.Y);
                AgentStatus = accepted ? "Agent 动作已提交" : "Agent 动作被拒绝：" + Status;
                if (!accepted)
                {
                    _fallbackSequence = Core.Sequence;
                    _fallbackSide = side;
                }
            }
            catch (Exception error)
            {
                // 仅输出受控分类；普通异常正文可能包含供应商响应或请求信息，不能直接进入日志。
                AgentFailure(error is GoAgentRequestException ? error.Message : "请求或响应处理失败");
            }
        }

        private void ApplyAgentFallback(GoSide side)
        {
            if (Core.Phase == GoPhase.Scoring)
            {
                SubmitActor(GoAction.ConfirmScore, side);
                return;
            }
            bool passed = _moves.Count > 0 && _moves[_moves.Count - 1].kind == (int)GoAction.Pass;
            int count = passed ? 0 : Core.GetHints(side, _computerHints);
            SubmitActor(count > 0 ? GoAction.Play : GoAction.Pass, side,
                count > 0 ? _computerHints[0].X : -1, count > 0 ? _computerHints[0].Y : -1);
        }
    }
}
