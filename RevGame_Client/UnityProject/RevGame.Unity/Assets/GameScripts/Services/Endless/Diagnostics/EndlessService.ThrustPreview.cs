#if UNITY_EDITOR || DEVELOPMENT_BUILD || REV_AUTOMATION
using System;
using RevGame.Core;
using UnityEngine;

namespace RevGame.Endless
{
    public sealed partial class EndlessService
    {
        /// <summary>
        /// 美术验收隔离夹具：只允许Isolated局，建立横向四格目标，再调用玩家正在使用的真实突刺。
        /// 不直接写伤害期望或调用Shader；重播以新目标和新动作形成快照，验证实际命中和长剑气。
        /// </summary>
        public object PreviewThrust()
        {
            if (!Isolated || Run == null)
            {
                throw new InvalidOperationException("突刺视觉夹具仅允许隔离测试局");
            }
            EndlessUnit assassin = null;
            foreach (EndlessUnit actor in Units)
            {
                if (actor.Piece.Side == GameSide.Player && actor.Piece.Def.Kind == PieceKind.Assassin)
                {
                    assassin = actor;
                    break;
                }
            }
            if (assassin == null)
            {
                throw new InvalidOperationException("请选择含刺客的炎环棋阵");
            }
            Vector2 from = Position(assassin.Piece);
            Horde.Clear();
            for (int i = 0; i < 4; i++)
            {
                Horde[i] = new EndlessHorde { active = true, id = Run.nextId++, hp = 10000, maxHp = 10000, x = from.x + i + 1, y = from.y };
            }
            int serial = ImpactSerial;
            AssassinThrust(assassin);
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["actualDamage"] = Horde[0].hp < 10000, ["newReceipt"] = ImpactSerial > serial,
                ["from"] = from, ["length"] = V("AssassinRange"), ["fixture"] = "隔离美术预览，真实突刺结算"
            };
        }
    }
}
#endif
