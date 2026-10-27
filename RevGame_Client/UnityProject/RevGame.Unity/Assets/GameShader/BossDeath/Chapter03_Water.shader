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
// 镜湖反射纹理随全身流场卷动：低频扭转产生龙卷感，高频细波形成湖面闪光。
// RGB采样轻微位移，Alpha保留原角色；没有置于角色前面的水环或漩涡平面。
float envelope=Envelope(t);
float2 q=DeathRotate(p,t*4.2+length(p)*3.2);
float ripple=sin(q.y*32+t*13+DeathNoise(q*7)*3);
float2 current=float2(sin(q.y*19+t*9),cos(q.x*16-t*8));
float2 warp=((q-p)*.13+current*.025)*envelope;
float3 moved=DeathBodyFlow(i,uv,warp,source);
float mirror=pow(saturate(.5+.5*sin((q.y+q.x*.18)*49+current.x*2-t*6)),10);
float field=uv.y+DeathNoise(uv*_Detail)*.13+ripple*.035;
float keep=1-smoothstep(field-.06,field+.06,lerp(-.2,1.3,t));
if(_Overlay>.5){return fixed4(0,0,0,0);}
float3 water=lerp(_Primary.rgb,_Secondary.rgb,mirror)*(.05+mirror*.13);
float3 color=lerp(source.rgb,moved,.8*envelope)+water*envelope;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
