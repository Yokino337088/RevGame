// 六章Boss专属死亡：水流吞噬。仅当前Boss在演出时借共享材质，原图Alpha定义身体轮廓。
Shader "RevGame/BossDeath/Chapter03_Water"
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
 
float angle=atan2(p.y,p.x);float spiral=sin(angle*4+length(p)*32-t*20);
float n=DeathNoise(uv*_Detail);float field=uv.y+n*.15+spiral*.035;
float front=lerp(-.2,1.25,t);float keep=1-smoothstep(field-.04,field+.04,front);
float rim=exp(-abs(field-front)*24)*Envelope(t);float2 warp=float2(sin(uv.y*30-t*16)*.07,t*.14)*t;
if(_Overlay>.5){float rings=Ring(p,.12+t*.48,.02)+Ring(p,.06+t*.32,.009);float flow=pow(saturate(spiral),6)*exp(-length(p)*3);return DeathOverlay(_Primary.rgb+_Secondary.rgb*2,(rings+flow*.6)*Envelope(t)*_Intensity,uv);}
float3 color=lerp(DeathWarp(i,uv,warp),source.rgb*_Primary.rgb,t*.55)+_Secondary.rgb*rim*3*_Intensity;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
