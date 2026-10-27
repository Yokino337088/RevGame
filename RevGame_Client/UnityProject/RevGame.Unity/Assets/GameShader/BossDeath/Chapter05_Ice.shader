// 六章Boss专属死亡：逐渐冰封。仅当前Boss在演出时借共享材质，原图Alpha定义身体轮廓。
Shader "RevGame/BossDeath/Chapter05_Ice"
{
 Properties {
 _Color ("精灵颜色",Color)=(1,1,1,1)
 [PerRendererData] _MainTex ("当前Boss帧",2D)="white"{}
 [PerRendererData] _FrameUV ("帧图集矩形",Vector)=(0,0,1,1)
 [PerRendererData] _Progress ("死亡演出进度",Range(0,1))=0
 _Overlay ("外部魔法光晕",Float)=0
 _Intensity ("特效强度",Range(0,3))=1.4
 _Detail ("噪声/纹理密度",Range(2,32))=12
 _RimWidth ("边缘宽度",Range(.005,.1))=.03
 _Primary ("主色",Color)=(.3,.65,1,1)
 _Secondary ("高光",Color)=(1,1,1,1)
 [HideInInspector] _RendererColor ("RendererColor",Color)=(1,1,1,1)
 [HideInInspector] _Flip ("Flip",Vector)=(1,1,1,1)
 [PerRendererData] _AlphaTex ("ExternalAlpha",2D)="white"{}
 [PerRendererData] _EnableExternalAlpha ("ExternalAlphaEnabled",Float)=0
 }
 SubShader {
 Tags {"Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True"}
 Cull Off ZWrite Off Lighting Off Blend One OneMinusSrcAlpha
 Pass {
 CGPROGRAM
 #pragma target 3.0
 #pragma vertex SpriteVert
 #pragma fragment Frag
 #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
 #include "BossDeathCommon.cginc"
 fixed4 Frag(v2f i):SV_Target {
 float2 uv=DeathLocal(i.texcoord);float2 p=uv-.5;float t=saturate(_Progress);fixed4 source=DeathSource(i);
// 退场按t正向冻结，登场按t逆向融冰；同一表面进度保证不是一层雪花贴片冒充冰壳。
float envelope=Envelope(t);
float frostNoise=DeathNoise(uv*_Detail);
float meltFront=uv.y*.62+frostNoise*.23;
float ice=smoothstep(meltFront-.10,meltFront+.10,t*1.35);
float cracks=pow(saturate(1-abs(sin(uv.x*29+sin(uv.y*15)*3))),12);
float facets=.45+.55*DeathNoise(floor(uv*13));
float keep=1-smoothstep(.80,1,t);
if(_Overlay>.5)
{
 // 雪花维持同体型和22%透明度；视觉冲击来自身体冰壳融化，不再把大雪花压在脸上。
 float snow=Snowflake(p*.8);
 return DeathOverlay(lerp(_Primary.rgb,_Secondary.rgb,.45),snow*envelope*.22,uv);
}
// 冰壳带冷蓝折射、晶面和细裂纹；仍保留原图45%的色彩细节，不画不透明白色覆盖片。
float3 frozen=source.rgb*.45+lerp(_Primary.rgb*.65,_Secondary.rgb*.9,facets)*.55;
float3 color=lerp(source.rgb,frozen,ice*.90);
float thawEdge=exp(-abs(meltFront-t*1.35)*24)*envelope;
color+=_Secondary.rgb*(cracks*ice*.16+thawEdge*.32)*envelope;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
