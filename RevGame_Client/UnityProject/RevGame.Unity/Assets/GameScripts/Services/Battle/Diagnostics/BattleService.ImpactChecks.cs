// 开发态固定输入：生产包不包含造局入口；测试驱动同一战斗服务/状态机，不复制玩法算法。
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using RevGame.Battle;
using RevGame.Core;
using RevGame.Data;
using RevGame.Pieces;
using Revolution;

namespace RevGame.Services
{
    public sealed partial class BattleService
    {
        /// <summary>只供自动化建立高生命展示棋盘；动画测试不会依赖随机手牌或先后死亡。</summary>
        public bool LoadImpactScenario()
        {
            if (!StartRound(1,1)) { return false; }
            StopEnemyAI();
            _board.Reset(); _playerPieces.Clear(); _enemyPieces.Clear();
            PieceKind[] ranged = { PieceKind.Archer,PieceKind.Crossbow,PieceKind.MageWater,PieceKind.MageFire,PieceKind.Priest };
            BoardPos[] positions = { new BoardPos(0,0),new BoardPos(4,0),new BoardPos(8,0),new BoardPos(0,2),new BoardPos(8,2) };
            for (int i=0;i<ranged.Length;i++) { AddImpactPiece(GameSide.Player,ranged[i],positions[i]); }
            PieceKind[] melee = { PieceKind.Swordman,PieceKind.Shield,PieceKind.HeavySword,PieceKind.Assassin };
            for (int i=0;i<melee.Length;i++)
            {
                AddImpactPiece(GameSide.Player,melee[i],new BoardPos(1+i*2,4));
                AddImpactPiece(GameSide.Enemy,PieceKind.Shield,new BoardPos(1+i*2,5));
            }
            AddImpactPiece(GameSide.Enemy,PieceKind.MageFire,new BoardPos(4,8));
            RefreshGoState();
            RevEvent.DispatchEvent<int>(Events.PhaseEnterPrepare,Round);
            return true;
        }

        private Piece AddImpactPiece(GameSide side, PieceKind kind, BoardPos pos)
        {
            var piece = new Piece(_nextPieceId++,PieceCatalog.Get(kind,StarTier.One),side,hpScale:1000f);
            if (!_board.Place(piece,pos)) { throw new InvalidOperationException("特效夹具占格冲突"); }
            (side == GameSide.Player ? _playerPieces : _enemyPieces).Add(piece);
            return piece;
        }

        /// <summary>读取实际AI出手次数与真实掉血；固定输入相同，改Excel攻速后预期会按配置重新计算。</summary>
        public static void VerifyImpactService(Action<bool,string> check)
        {
            var service = new BattleService(false,524);
            service.Chapter=1; service.Round=1; service.Phase=GamePhase.Battle;
            Piece player = service.AddImpactPiece(GameSide.Player,PieceKind.Shield,new BoardPos(3,3));
            Piece enemy = service.AddImpactPiece(GameSide.Enemy,PieceKind.Swordman,new BoardPos(3,4));
            service.RefreshGoState();
            int attacks=0;
            object owner=new object();
            RevEvent.AddEventListener<PieceAttackInfo>(Events.PieceAttacked,info=> { if(info.PieceId==enemy.Id) { attacks++; } },owner:owner);
            try
            {
                EnemyAI ai=service.EnsureEnemyAI(enemy);
                int hp=player.Hp;
                const int steps=160;
                for(int i=0;i<steps;i++) { service._battleElapsed+=Balance.BattleSimulationStepSeconds; ai.Tick(Balance.BattleSimulationStepSeconds); }
                int expected=(int)(steps*Balance.BattleSimulationStepSeconds/Balance.BattleTickSeconds*Balance.AttackSpeedScale*Balance.EnemyAttackRate);
                check(Math.Abs(attacks-expected)<=1,"真实敌方FSM出手符合Excel倍率：实际"+attacks+"，预期"+expected);
                check(player.Hp<hp,"攻击事件伴随真实扣血，不是只播放特效");
                service.Phase=GamePhase.Settle;
                ai.Tick(10f);
                check(Math.Abs(attacks-expected)<=1,"结算期不会继续出手");
            }
            finally { RevEvent.RemoveAllByOwner(owner); service.StopEnemyAI(); }

            service=new BattleService(false,525);
            service.Chapter=1; service.Round=1; service.Phase=GamePhase.Battle;
            service.AddImpactPiece(GameSide.Player,PieceKind.Archer,new BoardPos(0,0));
            service.AddImpactPiece(GameSide.Player,PieceKind.MageFire,new BoardPos(4,0));
            service.AddImpactPiece(GameSide.Player,PieceKind.Priest,new BoardPos(8,0));
            service.AddImpactPiece(GameSide.Enemy,PieceKind.Shield,new BoardPos(4,8));
            service.RefreshGoState();
            service._battleElapsed=Balance.RangedFormationSeconds;
            int moved=0;
            RevEvent.AddEventListener<int>(Events.PieceFormationMoved,id=>moved++,owner:owner);
            try
            {
                service.TryArrangeRangedFormation();
                check(moved==2,"服务提交两颗散落远程并发送两次整队事实");
                foreach(Piece piece in service._playerPieces)
                { check(service.Board.GetPiece(piece.Pos)==piece && !piece.IsLocked,"整队后真实占格和气完整"); }
                service.TryArrangeRangedFormation();
                check(moved==2,"冷却未到不重复整队");
                service._battleElapsed+=Balance.RangedFormationSeconds;
                service.TryArrangeRangedFormation();
                check(moved==2,"已形成相连阵型保持稳定");
                Piece manual = service._playerPieces[1];
                check(service.HandleBoardClick(manual.Pos) && service.HandleBoardClick(new BoardPos(8,0)),"通过玩家点击入口手动拆开远程阵型");
                service._nextFormationAt = service._battleElapsed;
                service.TryArrangeRangedFormation();
                check(manual.Pos == new BoardPos(8,0),"自动整队尊重刚提交的玩家手动指令");
            }
            finally { RevEvent.RemoveAllByOwner(owner); service.StopEnemyAI(); }
        }
    }
}
#endif
