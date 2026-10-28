// 帮手身体金色反光：保留当前帧纹理/透明轮廓，局部扫光与稀疏闪点单Pass表现，不整张染白。
Shader "RevGame/Helper/GoldReflect"
{
 Properties
 {
  [PerRendererData]_MainTex("当前帧图集",2D)="white"{}
  _Color("原图颜色",Color)=(1,1,1,1)
  _GoldTint("金色反射",Color)=(1,.74,.2,1)
  _GoldStrength("金色覆盖",Range(0,1))=.32
  _Reflection("反光强度",Range(0,2))=.8
  _SweepSpeed("扫光速度",Range(.05,3))=.55
  _BandWidth("反光带宽度",Range(.02,.4))=.13
  _Sparkle("闪点亮度",Range(0,1))=.32
  [PerRendererData]_FrameUV("当前帧UV矩形",Vector)=(0,0,1,1)
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
   #pragma target 3.0
   #pragma vertex SpriteVert
   #pragma fragment frag
   #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
   #include "UnitySprites.cginc"
   float4 _FrameUV,_GoldTint;
   float _GoldStrength,_Reflection,_SweepSpeed,_BandWidth,_Sparkle;
   float hash(float2 p){return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453);}
   half4 frag(v2f i):SV_Target
   {
    half4 source=SampleSpriteTexture(i.texcoord)*i.color;
    float2 uv=(i.texcoord-_FrameUV.xy)/max(_FrameUV.zw,.00001);
    float luminance=dot(source.rgb,float3(.299,.587,.114));
    float sweep=frac(_Time.y*_SweepSpeed)*1.6-.3;
    float reflection=1-smoothstep(_BandWidth*.3,_BandWidth,abs(uv.x+uv.y*.32-sweep));
    float facet=.5+.5*sin(uv.x*9-uv.y*7);
    float2 cell=floor(uv*18), local=frac(uv*18)-.5;
    float blink=pow(saturate(sin(_Time.y*2+hash(cell)*30)),14);
    float star=exp(-abs(local.x)*36-abs(local.y)*9)+exp(-abs(local.y)*36-abs(local.x)*9);
    float sparkle=step(.93,hash(cell))*blink*star*_Sparkle;
    // 暗眼与脸部线条保留；金色在原图亮处反射更强，不在透明格或邻图上冒方框。
    float3 gold=_GoldTint.rgb*(.25+luminance*.75);
    float3 color=lerp(source.rgb,gold,_GoldStrength*saturate(luminance*1.5));
    color+=_GoldTint.rgb*reflection*_Reflection*(.12+.35*luminance)*(.6+.4*facet);
    color+=float3(1,.92,.55)*sparkle;
    return half4(color*source.a,source.a);
   }
   ENDCG
  }
 }
}
