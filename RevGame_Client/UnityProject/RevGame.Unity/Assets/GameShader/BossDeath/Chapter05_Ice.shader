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
 
float ice=smoothstep(uv.y-.08,uv.y+.08,t*1.7+DeathNoise(uv*_Detail)*.12);
float cracks=pow(saturate(1-abs(sin(uv.x*29+sin(uv.y*15)*3))),12);float facets=.45+.55*DeathNoise(floor(uv*13));
float keep=1-smoothstep(.78,1,t);float spark=Ring(p,.18+t*.35,.012);
if(_Overlay>.5){float shard=pow(saturate(.5+.5*cos(atan2(p.y,p.x)*8-length(p)*20)),15);return DeathOverlay(_Primary.rgb*.75+_Secondary.rgb*.9,(spark*.65+shard*.35*exp(-length(p)*2))*Envelope(t)*_Intensity,uv);}
float3 frozen=source.rgb*.35+lerp(_Primary.rgb,_Secondary.rgb,facets)*.62+cracks*.35;
return DeathFinish(lerp(source.rgb,frozen,ice*.9)+_Secondary.rgb*cracks*ice*.5*_Intensity,source.a*keep);

 }
 ENDCG
 }
 }
}
