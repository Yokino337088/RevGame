// 风暴序列帧：图集局部UV驱动电流扫光与脉冲，保留原图透明轮廓，不采样相邻Sprite。
Shader "RevGame/Helper/Storm"
{
 Properties
 {
  [PerRendererData]_MainTex("当前序列帧图集",2D)="white"{}
  _Color("原图色",Color)=(1,1,1,1)
  _Glow("电流色",Color)=(.2,.85,1,1)
  _Intensity("电流强度",Range(0,3))=1.4
  [PerRendererData]_EffectKind("0弹体 1火爆 2水母爆炸",Float)=0
  [PerRendererData]_FrameUV("帧UV",Vector)=(0,0,1,1)
  [HideInInspector]_RendererColor("RendererColor",Color)=(1,1,1,1)
  [HideInInspector]_Flip("Flip",Vector)=(1,1,1,1)
  [PerRendererData]_AlphaTex("ExternalAlpha",2D)="white"{}
  [PerRendererData]_EnableExternalAlpha("ExternalAlpha",Float)=0
 }
 SubShader
 {
  Tags {"Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True"}
  Cull Off Lighting Off ZWrite Off Blend One OneMinusSrcAlpha
  Pass
  {
   CGPROGRAM
   #pragma vertex SpriteVert
   #pragma fragment frag
   #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
   #include "UnitySprites.cginc"
   float4 _FrameUV,_Glow;
   float _Intensity,_EffectKind;
   half4 frag(v2f i):SV_Target
   {
    half4 source=SampleSpriteTexture(i.texcoord)*i.color;
    float2 uv=(i.texcoord-_FrameUV.xy)/max(_FrameUV.zw,.00001);
    float wave=sin(uv.y*30+uv.x*12-_Time.y*18);
    float band=pow(saturate(wave),12);
    float pulse=.55+.45*sin(_Time.y*9+uv.y*6);
    float3 color=source.rgb+_Glow.rgb*(band*.65+pulse*.18)*_Intensity;
    // 水母命中借用同一组爆炸帧的亮度和透明轮廓，再着成蓝紫魔法爆炸；不额外打包一套贴图。
    if(_EffectKind>1.5)
    {
     float luminance=dot(source.rgb,float3(.299,.587,.114));
     color=lerp(float3(.2,.045,.5),float3(.35,1,1.2),luminance)+_Glow.rgb*band*_Intensity*.35;
    }
    return half4(color*source.a,source.a);
   }
   ENDCG
  }
 }
}
