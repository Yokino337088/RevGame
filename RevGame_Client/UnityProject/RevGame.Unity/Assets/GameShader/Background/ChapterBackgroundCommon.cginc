// 仅共享精灵顶点、原画采样、时钟和棋盘保护；每章的效果算法在各自 Shader 中独立实现。
// 第2~6章原画使用独立 Texture2D，第一章 SpriteAtlas 由专属 Chapter01_Tutorial Shader 处理。
#ifndef REV_GAME_CHAPTER_BACKGROUND_COMMON
#define REV_GAME_CHAPTER_BACKGROUND_COMMON
#include "UnityCG.cginc"

struct ChapterVertex { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
struct ChapterFragment { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 screen : TEXCOORD1; fixed4 color : COLOR; };
sampler2D _ChapterTex;
float4 _ChapterTex_TexelSize, _BoardRect, _Tint, _Accent, _Secondary;
float _Strength, _Speed, _Distortion, _Glow, _Particles;
float _Aspect, _EffectTime, _PreviewTime, _Transition, _BoardProtection;

ChapterFragment ChapterVert(ChapterVertex input)
{
    ChapterFragment output;
    output.position = UnityObjectToClipPos(input.vertex);
    output.screen = ComputeScreenPos(output.position);
    output.uv = input.uv;
    output.color = input.color;
    return output;
}

float ChapterHash(float2 p)
{
    float3 q = frac(float3(p.xyx) * 0.1031);
    q += dot(q, q.yzx + 33.33);
    return frac((q.x + q.y) * q.z);
}

float ChapterNoise(float2 p)
{
    float2 cell = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(ChapterHash(cell), ChapterHash(cell + float2(1, 0)), f.x),
        lerp(ChapterHash(cell + float2(0, 1)), ChapterHash(cell + 1), f.x), f.y);
}

float ChapterClock()
{
    // Inspector 预览沿用 Unity 时钟；游戏写入不受暂停影响的场景时钟；测试可固定时间。
    float clock = _EffectTime >= 0.0 ? _EffectTime : _Time.y;
    return (_PreviewTime >= 0.0 ? _PreviewTime : clock) * _Speed;
}

float ChapterAmount(ChapterFragment input)
{
    float2 screen = input.screen.xy / max(input.screen.w, 0.0001);
    float2 edge = min(screen - _BoardRect.xy, _BoardRect.xy + _BoardRect.zw - screen);
    float inside = smoothstep(-0.025, 0.025, min(edge.x, edge.y));
    // 只减弱特效，不给棋盘盖黑色；外围保留完整环境动态。
    return saturate(_Strength) * (1.0 - inside * _BoardProtection);
}

float2 ChapterPlane(float2 uv)
{
    return (uv - 0.5) * float2(_Aspect, 1.0);
}

float3 ChapterArt(float2 uv, float2 warp, float amount)
{
    float2 edge = min(uv, 1.0 - uv);
    float edgeSafe = smoothstep(0.0, 0.065, min(edge.x, edge.y));
    float2 texel = max(_ChapterTex_TexelSize.xy * 0.5, float2(0.0001, 0.0001));
    float2 sampleUV = clamp(uv + warp * amount * edgeSafe, texel, 1.0 - texel);
    // 固定原画梯度，避免流动放大 mip/导数变化；边缘收敛到零，原图不会露出接缝。
    return tex2Dgrad(_ChapterTex, sampleUV, ddx(uv), ddy(uv)).rgb * _Tint.rgb;
}

fixed4 ChapterOutput(float3 color, ChapterFragment input, float amount)
{
    float arrival = (1.0 - saturate(_Transition)) * exp(-length(ChapterPlane(input.uv)) * 3.5) * 0.035;
    return fixed4(saturate(color + _Accent.rgb * arrival * amount) * input.color.rgb, 1.0);
}
#endif
