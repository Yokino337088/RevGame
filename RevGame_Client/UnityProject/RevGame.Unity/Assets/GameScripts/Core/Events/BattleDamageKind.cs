namespace RevGame.Core
{
    /// <summary>
    /// 真实扣血的来源，决定Excel弹幕样式及击杀归属。不能由“数字很大”猜暴击。
    /// Boss段与BossSkillKind保持顺序，普通技能、环境事件和普攻不会混用同一颜色。
    /// </summary>
    public enum BattleDamageKind
    {
        Normal, Critical, RegionPulse, EnergyBomb, Artillery, Execution,
        Helper, HelperBeam,
        BossCleave = 16, BossSnipe, BossBombard, BossBulletRing, BossBlinkBlast,
        BossLaserSpin, BossSoulDrain, BossSummon, BossEnrage, BossMeteorShower,
        BossCrossLaser, BossFrostNova, BossChainLightning, BossPoisonMarsh,
        BossFlameWall, BossIceLance, BossGravityWell, BossDoomSigil,
        BossStarStorm, BossBlackHole, BossSolarFlare, BossSunBrand, BossTidalSurge, BossMaelstrom, BossSwampMaw, BossMireChains, BossFrozenPrison, BossGlacialAegis, BossDivineCross, BossSanctuary,
        BossCometRails, BossAstralShards, BossSolarSpears, BossEmberFan, BossMagmaRings, BossTidalLances, BossFoamBurst, BossUndertow, BossReefSpikes, BossBogSpines, BossVenomFan, BossMireRipples, BossBlightSeeds, BossSnowCross, BossHailBarrage, BossIceFault, BossJudgementLances, BossHaloPulse, BossRunicGrid, BossEventHorizon, BossConstellationSeal, BossPhoenixDescent, BossSolarEclipse, BossLeviathanWake, BossAbyssalCage, BossPlagueBloom, BossHydraFangs, BossAbsoluteZero, BossAvalanche, BossHeavenfall, BossFinalEdict
    }
}
