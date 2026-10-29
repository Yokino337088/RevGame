// 导弹命中的程序化蘑菇云：上升烟柱、展开伞盖、底部冲击环。单个共享Quad，无逐帧纹理分配。
Shader "RevGame/Helper/MushroomImpact"
{
 Properties
 {
  [HDR]_Fire("炽热火光",Color)=(1,.4,.035,1)
  _Smoke("烟云轮廓",Color)=(.14,.07,.055,1)
  _Intensity("火光强度",Range(.5,5))=2.7
  [PerRendererData]_Progress("爆炸进度",Range(0,1))=0
 }
 SubShader
 {
  Tags {"Queue"="Transparent+35" "RenderType"="Transparent"}
  Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata {float4 vertex:POSITION;float2 uv:TEXCOORD0;};
   struct v2f {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;};
   float4 _Fire,_Smoke;float _Intensity,_Progress;
   v2f vert(appdata v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;return o;}
   float ellipse(float2 p,float2 radius){return length(p/radius)-1;}
   float4 frag(v2f i):SV_Target
   {
    float p=saturate(_Progress);float2 uv=i.uv;float x=uv.x-.5;
    float grow=saturate(p*5);float rise=.29+grow*.35+p*.06;
    float crown=ellipse(float2(x,uv.y-rise),float2(.12+.29*grow,.08+.12*grow));
    float scallop=sin(x*56+p*15)*.055+sin(x*29-p*12)*.045;
    crown+=scallop;
    float stem=ellipse(float2(x+sin(uv.y*18+p*4)*.016,uv.y-(.19+rise)*.5),float2(.046+.04*grow,(rise-.15)*.5));
    float cloud=min(crown,stem);
    float body=1-smoothstep(-.05,.05,cloud);
    float rim=1-smoothstep(-.24,.03,cloud);
    float swirl=.5+.5*sin(x*29+sin(uv.y*31+p*9)*2-p*18);
    float fire=saturate(1-p*1.25)*(.55+.45*swirl);
    float shock=1-smoothstep(.012,.032,abs(length(float2(x,(uv.y-.16)*3))-(.1+.34*grow)));
    shock*=1-smoothstep(.18,.65,p);
    float fade=1-smoothstep(.65,1,p);
    float3 color=lerp(_Smoke.rgb,_Fire.rgb*_Intensity,fire*rim);
    color+=pow(saturate(swirl),5)*fire*float3(1,.6,.09)*_Intensity;
    color=lerp(color,float3(1,.76,.23)*_Intensity,shock);
    return float4(color,saturate(body*.95+shock)*fade*grow);
   }
   ENDCG
  }
 }
}
