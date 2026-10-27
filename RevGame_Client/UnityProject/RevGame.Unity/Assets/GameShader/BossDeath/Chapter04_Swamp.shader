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
 
// 从底部吞入泥浆：采样向上偏移会让原身体向下沉；Alpha也采同一点，不能留白色旧轮廓。
float n=DeathNoise(uv*_Detail+float2(t*.4,-t*.7));
float2 sinkUv=uv+float2(sin(uv.y*17+t*7)*.015*t,t*t*.38);
float inside=step(0,sinkUv.x)*step(sinkUv.x,1)*step(0,sinkUv.y)*step(sinkUv.y,1);
fixed4 sinking=SampleSpriteTexture(_FrameUV.xy+clamp(sinkUv,.001,.999)*_FrameUV.zw)*i.color;
float mudHeight=lerp(-.2,1.25,t)+sin(uv.x*20+t*3)*.045+n*.10;
float keep=smoothstep(mudHeight-.055,mudHeight+.055,uv.y);
float coating=(1-smoothstep(mudHeight+.025,mudHeight+.19,uv.y))*Envelope(t);
if(_Overlay>.5)
{
 // 外层只画身体脚下的实心泥浆与随机翻涌，不画任何Ring/椭圆描边。
 float bottom=exp(-pow((uv.y-.27-n*.028)/.095,2));
 float span=1-smoothstep(.24,.43,abs(p.x)+n*.08);
 float churn=DeathNoise(float2(uv.x*23,uv.y*16-t*1.4));
 float sludge=bottom*span*(.65+churn*.35)*Envelope(t)*_Intensity;
 return DeathOverlay(_Primary.rgb*(.5+churn*.7)+_Secondary.rgb*pow(churn,6)*.22,sludge,uv);
}
float3 mud=sinking.rgb*.32+_Primary.rgb*(.55+n*.35);
float3 color=lerp(sinking.rgb,mud,saturate(coating*.9+t*.25));
return DeathFinish(color,sinking.a*inside*keep);

 }
 ENDCG
 }
 }
}
