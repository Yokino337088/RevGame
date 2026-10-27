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
// 将坐标折叠到六个60度扇区，用线段距离绘制主枝和两对侧枝；抗锯齿宽度沿用材质参数。
float SegmentDistance(float2 p,float2 a,float2 b)
{
 float2 ab=b-a;
 return length(p-a-ab*saturate(dot(p-a,ab)/max(dot(ab,ab),.00001)));
}
float Snowflake(float2 p)
{
 float angle=atan2(p.y,p.x);
 float folded=fmod(angle+6.806784,1.047198)-.523599;
 float2 q=length(p)*float2(cos(folded),sin(folded));
 float d=SegmentDistance(q,float2(0,0),float2(.32,0));
 d=min(d,SegmentDistance(q,float2(.14,0),float2(.22,.085)));
 d=min(d,SegmentDistance(q,float2(.14,0),float2(.22,-.085)));
 d=min(d,SegmentDistance(q,float2(.23,0),float2(.29,.06)));
 d=min(d,SegmentDistance(q,float2(.23,0),float2(.29,-.06)));
 float width=max(.003,_RimWidth*.22);
 return 1-smoothstep(width,width+.004,d);
}
// 同一域扭曲坐标驱动纹理、云带和星点，避免一张静态Boss上叠独立前景图。
float2 DeathRotate(float2 p,float angle)
{
 float sn=sin(angle),cs=cos(angle);
 return float2(cs*p.x-sn*p.y,sn*p.x+cs*p.y);
}
float3 DeathBodyFlow(v2f i,float2 uv,float2 warp,fixed4 source)
{
 float2 q=clamp(uv+warp,.002,.998);
 fixed4 moved=SampleSpriteTexture(_FrameUV.xy+q*_FrameUV.zw)*i.color;
 // 位移撞到透明留白时回退原像素，避免透明像素RGB在角色边缘形成黑洞或白边。
 return lerp(source.rgb,moved.rgb,smoothstep(.05,.4,moved.a));
}
float Envelope(float t) { return smoothstep(0,.12,t)*(1-smoothstep(.78,1,t)); }
fixed4 DeathFinish(float3 rgb,float alpha) { alpha=saturate(alpha)*(1-step(.9999,_Progress));return fixed4(rgb*alpha,alpha); }
// 完整矩形网格留出光晕，但其透明边缘必须柔和收束；避免冰光/十字形成一块硬边方片。
fixed4 DeathOverlay(float3 rgb,float alpha,float2 uv) { float edge=1-smoothstep(.36,.5,max(abs(uv.x-.5),abs(uv.y-.5)));return DeathFinish(rgb,alpha*edge); }
#endif
