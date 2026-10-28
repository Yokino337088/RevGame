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
 _BeamWidth ("十字光核半宽（局部UV）",Range(.008,.12))=.045
 _BeamGlowWidth ("十字辉光宽度（局部UV）",Range(.025,.25))=.14
 _BeamBrightness ("十字亮度",Range(.2,5))=2.8
 _BeamLength ("十字半长（局部UV）",Range(.2,.5))=.47
 _AuraScale ("圣光范围（身体倍率）",Range(1,2.5))=1.8
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
 float _BeamWidth, _BeamGlowWidth, _BeamBrightness, _BeamLength;
 fixed4 Frag(v2f i):SV_Target {
 float2 uv=DeathLocal(i.texcoord);float2 p=uv-.5;float t=saturate(_Progress);fixed4 source=DeathSource(i);
// 光核宽度、外层辉光与亮度独立，避免只能调一个总强度、光柱却始终细如线。
float envelope=Envelope(t);
float vertical=1-smoothstep(_BeamWidth*.65,_BeamWidth,abs(p.x));
float horizontal=1-smoothstep(_BeamWidth*.65,_BeamWidth,abs(p.y-.1));
float cross=saturate(vertical+horizontal);
float n=DeathNoise(uv*_Detail+float2(0,t));
float field=n*.4+abs(p.x)*.8+abs(p.y)*.5;
float keep=1-smoothstep(field-.05,field+.05,lerp(-.2,1.1,t));
if(_Overlay>.5)
{
 // 仅白色光核接近不透明，金色辉光柔和扩散；全画面仍保留Boss原图与透明区域。
 float glowV=exp(-pow(abs(p.x)/max(.001,_BeamGlowWidth),2)*3);
 float glowH=exp(-pow(abs(p.y-.1)/max(.001,_BeamGlowWidth),2)*3);
 float glow=saturate(glowV+glowH);
 float span=1-smoothstep(_BeamLength-.07,_BeamLength,max(abs(p.x),abs(p.y)));
 float strength=_BeamBrightness*_Intensity;
 float3 light=lerp(_Primary.rgb,_Secondary.rgb,cross)*strength;
 float alpha=saturate(cross*1.25+glow*.48)*envelope*span;
 return DeathOverlay(light,alpha,uv);
}
float blessing=pow(saturate(n),6);
float rim=exp(-abs(field-lerp(-.2,1.1,t))*30);
// 身体只加少量神圣刻纹，强光由外层承担，不能把整个Boss染成失去细节的白色剪影。
float3 color=source.rgb+(_Primary.rgb*rim*.45+_Secondary.rgb*(cross*.42+blessing*.18))*envelope*_Intensity;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
