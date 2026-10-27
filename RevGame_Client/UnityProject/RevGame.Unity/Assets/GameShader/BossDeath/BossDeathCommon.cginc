// 图集内局部UV与程序噪声/SDF工具；每章独立shader定义死亡算法。无GrabPass或附加纹理。
#ifndef REV_BOSS_DEATH_COMMON
#define REV_BOSS_DEATH_COMMON
#include "UnitySprites.cginc"
float4 _FrameUV;
float _Progress, _Overlay, _Intensity, _Detail, _RimWidth;
float4 _Primary, _Secondary;
float DeathHash(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
float DeathNoise(float2 p) { float2 i=floor(p),f=frac(p);f=f*f*(3-2*f);return lerp(lerp(DeathHash(i),DeathHash(i+float2(1,0)),f.x),lerp(DeathHash(i+float2(0,1)),DeathHash(i+1),f.x),f.y); }
float2 DeathLocal(float2 uv) { return (uv-_FrameUV.xy)/max(_FrameUV.zw,.0001); }
fixed4 DeathSource(v2f i) { return SampleSpriteTexture(i.texcoord)*i.color; }
float3 DeathWarp(v2f i,float2 local,float2 warp) {float2 q=clamp(local+warp,.002,.998);return SampleSpriteTexture(_FrameUV.xy+q*_FrameUV.zw).rgb*i.color.rgb;}
// Inspector边缘宽度统一控制光环粗细，程序特效在矩形边缘淡出，避免透明帧外出现硬方框。
float Ring(float2 p,float radius,float width) { width*=max(.2,_RimWidth/.03);return 1-smoothstep(width,width*2,abs(length(p)-radius)); }
float Envelope(float t) { return smoothstep(0,.12,t)*(1-smoothstep(.78,1,t)); }
fixed4 DeathFinish(float3 rgb,float alpha) { alpha=saturate(alpha)*(1-step(.9999,_Progress));return fixed4(rgb*alpha,alpha); }
// 完整矩形网格留出光晕，但其透明边缘必须柔和收束；避免冰光/十字形成一块硬边方片。
fixed4 DeathOverlay(float3 rgb,float alpha,float2 uv) { float edge=1-smoothstep(.36,.5,max(abs(uv.x-.5),abs(uv.y-.5)));return DeathFinish(rgb,alpha*edge); }
#endif
