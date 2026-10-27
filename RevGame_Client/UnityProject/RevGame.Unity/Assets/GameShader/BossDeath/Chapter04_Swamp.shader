// 六章Boss专属死亡：沼泽吞噬。仅当前Boss在演出时借共享材质，原图Alpha定义身体轮廓。
Shader "RevGame/BossDeath/Chapter04_Swamp"
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
// 沼泽毒液从脚下漫上身体，低频噪声使泥面翻动；原画保留大部分颜色与边线。
float envelope=Envelope(t);
float n=DeathNoise(uv*_Detail+float2(t*.4,-t*.7));
float2 sinkUv=uv+float2(sin(uv.y*17+t*7)*.012*t,t*t*.30);
float inside=step(0,sinkUv.x)*step(sinkUv.x,1)*step(0,sinkUv.y)*step(sinkUv.y,1);
fixed4 sinking=SampleSpriteTexture(_FrameUV.xy+clamp(sinkUv,.001,.999)*_FrameUV.zw)*i.color;
float mudHeight=lerp(-.2,1.25,t)+sin(uv.x*20+t*3)*.035+n*.08;
float keep=smoothstep(mudHeight-.055,mudHeight+.055,uv.y);
float coating=(1-smoothstep(mudHeight+.025,mudHeight+.19,uv.y))*envelope;
if(_Overlay>.5)
{
 float bottom=exp(-pow((uv.y-.25-n*.025)/.07,2));
 float span=1-smoothstep(.22,.43,abs(p.x)+n*.06);
 return DeathOverlay(_Secondary.rgb*.7,bottom*span*envelope*.18,uv);
}
float poison=pow(saturate(n),5)*coating;
float3 color=lerp(sinking.rgb,sinking.rgb*.8+_Secondary.rgb*.15,coating*.42)+_Secondary.rgb*poison*.16;
return DeathFinish(color,sinking.a*inside*keep);

 }
 ENDCG
 }
 }
}
