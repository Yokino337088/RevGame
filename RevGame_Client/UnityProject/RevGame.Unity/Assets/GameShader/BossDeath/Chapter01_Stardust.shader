// 六章Boss专属死亡：星尘消解。仅当前Boss在演出时借共享材质，原图Alpha定义身体轮廓。
Shader "RevGame/BossDeath/Chapter01_Stardust"
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
// 全身星云采用域扭曲：两层噪声驱动采样坐标和云带，星尘跟随同一流场移动。
// 扭曲只占局部UV的几个百分点；身体Alpha仍取原帧，保留剑、王冠及序列帧轮廓。
float envelope=Envelope(t);
float2 q=DeathRotate(p,t*4.8+length(p)*2.8);
float2 field=float2(DeathNoise(q*5+float2(t*2,0)),DeathNoise(q*5+float2(7,-t*1.4)))-.5;
float2 flow=q+field*.32;
float cloud=DeathNoise(flow*9+float2(t*1.8,-t));
float bands=.5+.5*sin(flow.y*27+cloud*5+t*9);
float2 warp=(field*.07+(q-p)*.16)*envelope;
float3 moved=DeathBodyFlow(i,uv,warp,source);
float2 starsUv=flow*30;
float2 cell=floor(starsUv),starPoint=frac(starsUv)-.5;
float star=pow(saturate(1-length(starPoint)*4),10)*step(.86,DeathHash(cell));
// 外层不画旋涡贴片；色彩和星点全部被真实棋子轮廓裁切。
if(_Overlay>.5){return fixed4(0,0,0,0);}
float fieldDissolve=DeathNoise(uv*_Detail)*.65+uv.y*.35;
float keep=1-smoothstep(fieldDissolve-.07,fieldDissolve+.07,lerp(-.15,1.2,t));
float3 stardust=lerp(_Primary.rgb,_Secondary.rgb,bands)*(.10+cloud*.17)+star*.42;
float3 color=lerp(source.rgb,moved,.8*envelope)+stardust*envelope*_Intensity;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
