// 六章Boss专属死亡：阳光普照。仅当前Boss在演出时借共享材质，原图Alpha定义身体轮廓。
Shader "RevGame/BossDeath/Chapter02_Sunlight"
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
// 阳光普照以肩部上方为光源：放射光束与穿过身体的金色扫光同步爆发，不绘制闭合圆环。
// 整体强度仍由共享材质调节；高亮集中光束和消散前沿，原图颜色与Alpha轮廓继续作为主体。
float envelope=Envelope(t);
float field=uv.y+DeathNoise(uv*_Detail)*.12;
float front=lerp(-.15,1.2,t);
float keep=1-smoothstep(field-.06,field+.06,front);
float2 lightPos=p-float2(0,.22);
float angle=atan2(lightPos.y,lightPos.x);
float rays=pow(saturate(.5+.5*sin(angle*14-t*4)),7);
float reach=exp(-length(lightPos)*2.4);
float shafts=pow(saturate(.5+.5*sin((p.x+p.y*.28)*25-t*7)),7);
float sunlight=(.35+shafts*.65)*smoothstep(-.5,.35,p.y);
float rim=exp(-abs(field-front)*23);
if(_Overlay>.5)
{
 // 外部金光恢复明显的放射感，但Alpha封顶55%，避免把弓、耳朵和表情整片刷白。
 float beam=(1-smoothstep(.06,.45,abs(p.x)))*sunlight;
 float core=exp(-dot(lightPos,lightPos)*25);
 float glow=(beam*.42+core*.26+rays*reach*.36)*envelope*_Intensity;
 return DeathOverlay(_Primary.rgb*1.4+_Secondary.rgb*.45,min(.55,glow),uv);
}
float3 color=source.rgb+(_Primary.rgb*.14+_Secondary.rgb*(sunlight*.18+rays*reach*.24+rim*.50))*envelope*_Intensity;
return DeathFinish(color,source.a*keep);

 }
 ENDCG
 }
 }
}
