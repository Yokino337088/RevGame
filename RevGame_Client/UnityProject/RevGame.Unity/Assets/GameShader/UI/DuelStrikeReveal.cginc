#ifndef REV_GAME_DUEL_STRIKE_REVEAL_INCLUDED
#define REV_GAME_DUEL_STRIKE_REVEAL_INCLUDED

// 局部四边形坐标独立于图集 UV。前沿略带波纹，扫过后保留原图的透明边缘。
half2 DuelStrikeReveal(float4 local)
{
    float radius = length((local.xy - 0.5) * 1.46);
    float crest = lerp(local.x + (local.y - 0.5) * 0.14, radius, local.w);
    crest += sin(local.y * 31.0 + local.z * 9.0) * 0.027;
    float front = lerp(-0.18, 1.27, local.z);
    half visible = 1.0h - smoothstep(front - 0.10, front + 0.03, crest);
    half edge = exp2(-20.0 * abs(crest - front + 0.06)) * visible;
    return half2(visible, edge);
}

#endif
