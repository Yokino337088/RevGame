using System;
using System.Collections.Generic;
using Revolution;
using UnityEngine;

namespace RevGame.Pvp
{
    /// <summary>九路围棋独立配置快照。仅进入PvP时加载；配置或服务器故障都不能阻断原有单机启动。</summary>
    [Serializable]
    public sealed class PvpConfig
    {
        public int komiHalfPoints;
        public int mainTimeSeconds;
        public int byoyomiSeconds;
        public int byoyomiCount;
        // 旧房间握手仍包含该字段，落子建议已改成不限次数。
        public int hintCount;
        public int territoryRevealFrom;
        public int territoryRevealEvery;
        public int reconnectWindowSeconds;
        public int matchTimeoutSeconds;
        public int discoveryPort;
        public float announceSeconds;
        public float peerExpirySeconds;
        public float invitationTimeoutSeconds;
        public float connectTimeoutSeconds;
        public bool rookie;
        // 速战和试探建议只属于本地模式，不进入旧联机握手的 JSON。
        [NonSerialized] public int localPlacementLimit;
        [NonSerialized] public int localBaitEvery;

        /// <summary>同一份规则JSON用于云端匹配和局域网握手，参数不同的两端拒绝开局，避免各算各的。</summary>
        public string RulesJson { get { return JsonUtility.ToJson(this); } }

        public static PvpConfig Read(bool rookie)
        {
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var row in RevDataTableManager.Get<PvpRuleTable>().All)
            {
                if (string.IsNullOrEmpty(row.Key) || values.ContainsKey(row.Key) || float.IsNaN(row.Value) || float.IsInfinity(row.Value) || row.Value <= 0)
                {
                    throw new InvalidOperationException("九路围棋配置存在重复、空键或非法数值：" + row.Key);
                }
                values.Add(row.Key, row.Value);
            }
            var result = new PvpConfig
            {
                komiHalfPoints = Integer(values, "KomiHalfPoints"),
                mainTimeSeconds = Integer(values, "MainTimeSeconds"),
                byoyomiSeconds = Integer(values, "ByoyomiSeconds"),
                byoyomiCount = Integer(values, "ByoyomiCount"),
                hintCount = Integer(values, rookie ? "HintCountRookie" : "HintCountAdvanced"),
                territoryRevealFrom = Integer(values, "TerritoryRevealFrom"),
                territoryRevealEvery = Integer(values, "TerritoryRevealEvery"),
                reconnectWindowSeconds = Integer(values, "ReconnectWindowSeconds"),
                matchTimeoutSeconds = Integer(values, "MatchTimeoutSeconds"),
                discoveryPort = Integer(values, "DiscoveryPort"),
                announceSeconds = Get(values, "AnnounceSeconds"),
                peerExpirySeconds = Get(values, "PeerExpirySeconds"),
                invitationTimeoutSeconds = Get(values, "InvitationTimeoutSeconds"),
                connectTimeoutSeconds = Get(values, "ConnectTimeoutSeconds"),
                rookie = rookie,
                localPlacementLimit = (int)Math.Ceiling(Integer(values, "LocalReferenceMoves19") * (double)GoCore.PointCount / (19 * 19)),
                localBaitEvery = Integer(values, "LocalBaitEvery")
            };
            if (result.discoveryPort > 65535 || result.peerExpirySeconds <= result.announceSeconds || result.komiHalfPoints > 162)
            {
                throw new InvalidOperationException("九路围棋端口、广播有效期或贴目越界");
            }
            if (result.localPlacementLimit < 1 || result.localPlacementLimit > GoCore.PointCount * 2)
            {
                throw new InvalidOperationException("本地速战落子预算超出棋盘容量约定");
            }
            // 两个提示次数均校验，未选中的场次也不能藏着坏配置。
            Integer(values, "HintCountRookie");
            Integer(values, "HintCountAdvanced");
            if (values.Count != 17)
            {
                throw new InvalidOperationException("九路围棋配置包含未知参数，请核对参数名");
            }
            return result;
        }
        private static float Get(Dictionary<string, float> values, string key)
        {
            if (!values.TryGetValue(key, out float value))
            {
                throw new InvalidOperationException("九路围棋配置缺少：" + key);
            }
            return value;
        }
        private static int Integer(Dictionary<string, float> values, string key)
        {
            float value = Get(values, key);
            if (value != Math.Floor(value) || value > int.MaxValue)
            {
                throw new InvalidOperationException("九路围棋配置必须为整数：" + key);
            }
            return (int)value;
        }
    }
}
