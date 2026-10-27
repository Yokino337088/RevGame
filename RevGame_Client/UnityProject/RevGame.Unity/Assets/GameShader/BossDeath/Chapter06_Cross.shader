// 六章Boss专属死亡：十字神罚。仅当前Boss在演出时借共享材质，原图Alpha定义身体轮廓。
Shader "RevGame/BossDeath/Chapter06_Cross"
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
// 十字圣光恢复明确的纵横爆发；高亮只集中细光柱与消散边缘，身体其余区域仍清晰。
float envelope=Envelope(t);
float vertical=exp(-abs(p.x)*60),horizontal=exp(-abs(p.y-.1)*60);
float cross=saturate(vertical+horizontal);
float n=DeathNoise(uv*_Detail+float2(0,t));
float field=n*.4+abs(p.x)*.8+abs(p.y)*.5;
float keep=1-smoothstep(field-.05,field+.05,lerp(-.2,1.1,t));
if(_Overlay>.5)
{
 float glow=exp(-abs(p.x)*18)+exp(-abs(p.y-.1)*18);
 return DeathOverlay(_Secondary.rgb,(cross*.58+glow*.13)*envelope,uv);
}
float blessing=pow(saturate(n),6);
float rim=exp(-abs(field-lerp(-.2,1.1,t))*30);
float3 color=source.rgb+(_Primary.rgb*rim*.55+_Secondary.rgb*(cross*.95+blessing*.22))*envelope;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
