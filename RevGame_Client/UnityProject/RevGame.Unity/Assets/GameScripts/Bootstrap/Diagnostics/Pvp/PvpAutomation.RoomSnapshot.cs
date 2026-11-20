#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using System.Collections.Generic;
using Fantasy;
using RevGame.Pvp.Agent;
using RevGame.Pvp.Networking;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>房间快照的只读查询与定向入站验收，由 PvP 自动化统一注册和清理。</summary>
    public static partial class PvpAutomation
    {
        /// <summary>隔离输入里的旧房身份。</summary>
        private const string FixtureRoomA = "fixture-room-a";
        /// <summary>换房后当前会话的房间身份。</summary>
        private const string FixtureRoomB = "fixture-room-b";
        /// <summary>本轮夹具当前的无 Socket 会话，离场时关闭。</summary>
        private static FantasyGoTransport _roomFixture;
        /// <summary>保留换房前排队的回调，用于在新房就绪后重放。</summary>
        private static Action<GoRelayState> _oldRoomReceiver;
        /// <summary>本轮暂时禁用自动云分析的服务实例，收尾只还原这一份。</summary>
        private static PvpService _roomFixtureService;
        /// <summary>建夹具前的云分析工厂，退房提交之后还原。</summary>
        private static Func<PvpAgentSettings, GoAgentPosition, IGoAgentRequest> _previousRoomAnalysis;

        /// <summary>解析有限的验收阶段。调用方无法指定地址、协议字段或任意载荷。</summary>
        [Serializable]
        private sealed class RoomFixtureArgs
        {
            /// <summary>当前要投递的固定阶段，或清理此次会话。</summary>
            public string step;
        }

        /// <summary>注册房间共享快照与开发态固定输入，采用 PvP 自己的 owner 回收。</summary>
        private static void RegisterRoomSnapshots()
        {
            RevRuntimeAutomation.Register("pvp.room.snapshot", "读取 HUD 同源的房间显示、动作许可及实际绑定结果", Empty,
                _ => RoomSnapshotState(), readOnly: true, owner: Owner);
            const string schema = "{\"type\":\"object\",\"properties\":{\"step\":{\"type\":\"string\",\"enum\":["
                + "\"room-a\",\"room-b\",\"late-a\",\"missing-names\",\"missing-seat\",\"restore\",\"cleanup\"]}},"
                + "\"required\":[\"step\"],\"additionalProperties\":false}";
            RevRuntimeAutomation.Register("pvp.room.fixture", "隔离 Play 在真实接收入口投递换房、迟到、缺昵称或缺座位的固定回包",
                schema, json => ApplyRoomFixture(JsonUtility.FromJson<RoomFixtureArgs>(json).step), owner: Owner);
        }

        /// <summary>序列化和 HUD 同源的只读快照，并附上实际落屏版本供测试对照。</summary>
        private static Dictionary<string, object> RoomSnapshotState()
        {
            PvpService.PvpRoomSnapshot room = Service.RoomSnapshot;
            var actions = new Dictionary<string, object>();
            foreach (PvpRoomAction action in Enum.GetValues(typeof(PvpRoomAction)))
            {
                actions[action.ToString()] = new Dictionary<string, object>
                {
                    ["enabled"] = room.Allows(action),
                    ["disabledReason"] = room.DisabledReason(action)
                };
            }
            return new Dictionary<string, object>
            {
                ["roomId"] = room.RoomId,
                ["revision"] = room.Revision,
                ["phase"] = room.Phase?.ToString() ?? "None",
                ["sequence"] = room.Sequence,
                ["stateHash"] = room.StateHash?.ToString() ?? string.Empty,
                ["hasGame"] = room.HasGame,
                ["missingPlayerNames"] = room.MissingPlayerNames,
                ["missingCriticalState"] = room.MissingCriticalState,
                ["blackName"] = room.BlackName,
                ["whiteName"] = room.WhiteName,
                ["title"] = room.Title,
                ["black"] = room.BlackText,
                ["white"] = room.WhiteText,
                ["turn"] = room.TurnText,
                ["result"] = room.ResultText,
                ["serverStatus"] = room.ServerStatusText,
                ["queue"] = room.QueueText,
                ["matchLabel"] = room.MatchLabel,
                ["modeLabel"] = room.ModeLabel,
                ["hintLabel"] = room.HintLabel,
                ["agentLabel"] = room.AgentLabel,
                ["actions"] = actions,
                ["hud"] = RevUI.Get<PvpPanel>()?.RoomDisplayEvidence(room)
            };
        }

        /// <summary>只在准备好的隔离 PvP 入口运行。回包经过真实传输回调、业务接收门禁和 HUD 刷新。</summary>
        private static object ApplyRoomFixture(string step)
        {
            if (step == "cleanup")
            {
                StopRoomSnapshotFixture();
                return Result(true, "夹具已回收");
            }
            if (!Ready(out string reason))
            {
                return Result(false, reason);
            }
            if (step == "room-a")
            {
                if (_roomFixture != null || Service.Core != null || Service.Connected || Service.Lan != null || FantasyGoTransport.Active != null)
                {
                    return Result(false, "请从未开局的围棋入口开始房间验收");
                }
                // 只隔离自动云分析工厂，避免验收触发真实 API。正常落子、时钟和房间快照仍按服务运行。
                _roomFixtureService = Service;
                _previousRoomAnalysis = _roomFixtureService.AnalysisRequestFactory;
                _roomFixtureService.AnalysisRequestFactory = null;
                _roomFixture = _roomFixtureService.BeginRoomSnapshotFixture("127.0.0.1:20101");
                _oldRoomReceiver = _roomFixture.RoomFixtureReceiver();
                _oldRoomReceiver(FixtureState(FixtureRoomA, 0, "旧房黑方", "旧房白方"));
            }
            else
            {
                if (_roomFixture == null || (step == "room-b" ? Service.RoomId != FixtureRoomA : Service.RoomId != FixtureRoomB))
                {
                    return Result(false, "房间验收输入顺序错误");
                }
                if (step == "room-b")
                {
                    _roomFixture = Service.BeginRoomSnapshotFixture("127.0.0.1:20102");
                }
                Action<GoRelayState> receive = _roomFixture.RoomFixtureReceiver();
                if (step == "late-a")
                {
                    // 旧会话即使回了当前房 ID 也要拒绝。当前会话的错房和过期大厅状态同样不能动 RPC 路由。
                    _oldRoomReceiver(FixtureState(FixtureRoomB, 3, "旧会话黑方", "旧会话白方"));
                    receive(FixtureState(FixtureRoomA, 3, "旧房黑方", "旧房白方"));
                    receive(new GoRelayState { Phase = "queued", Reason = "旧排队回包" });
                }
                else
                {
                    bool missingNames = step == "missing-names";
                    bool missingSeat = step == "missing-seat";
                    GoRelayState reply = FixtureState(FixtureRoomB, missingSeat ? 3 : 2,
                        missingNames ? null : "新房黑方", missingNames ? null : "新房白方");
                    if (missingSeat)
                    {
                        reply.MySide = (int)GoSide.Empty;
                    }
                    receive(reply);
                }
            }
            return new Dictionary<string, object>
            {
                ["accepted"] = true,
                ["route"] = _roomFixture.RoomFixtureRoute,
                ["room"] = RoomSnapshotState()
            };
        }

        /// <summary>生成与当前规则一致的合法棋谱，通过真实指纹验证，缺字段由单个用例显式注入。</summary>
        private static GoRelayState FixtureState(string roomId, int count, string black, string white)
        {
            var state = new GoRelayState
            {
                RoomId = roomId,
                Phase = "playing",
                MySide = (int)GoSide.Black,
                CoreVersion = PvpService.ProtocolVersion,
                RulesJson = Service.Config.RulesJson,
                BlackName = black,
                WhiteName = white
            };
            GoCore board = GoCore.CreateMatch(Service.Config.komiHalfPoints);
            for (int i = 0; i < count; i++)
            {
                GoSide actor = board.Turn;
                string previous = board.StateHash.ToString();
                int x = 3 + i;
                if (!board.TryPlay(actor, x, 4, out _))
                {
                    throw new InvalidOperationException("房间验收固定棋谱无法重放");
                }
                state.Commands.Add(new GoRelayCommand
                {
                    Sequence = board.Sequence,
                    Side = (int)actor,
                    Kind = (int)GoAction.Play,
                    X = x,
                    Y = 4,
                    PreviousHash = previous,
                    ResultHash = board.StateHash.ToString()
                });
            }
            return state;
        }

        /// <summary>提交真实退房，再还原云分析工厂和迟到回调。已切走的服务只恢复被本轮修改的工厂。</summary>
        private static void StopRoomSnapshotFixture()
        {
            if (_roomFixtureService != null)
            {
                _roomFixtureService.EndRoomSnapshotFixture(_roomFixture);
                if (_roomFixtureService.AnalysisRequestFactory == null)
                {
                    _roomFixtureService.AnalysisRequestFactory = _previousRoomAnalysis;
                }
            }
            _roomFixture = null;
            _oldRoomReceiver = null;
            _roomFixtureService = null;
            _previousRoomAnalysis = null;
        }
    }
}
#endif
