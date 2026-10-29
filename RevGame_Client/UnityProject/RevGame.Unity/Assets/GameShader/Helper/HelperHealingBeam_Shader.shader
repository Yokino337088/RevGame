// 治疗束由白绿光芯、深绿轮廓和沿长度流动的加号组成；深轮廓保证雪地/神殿背景上仍有边界。
Shader "RevGame/Helper/HealingBeam"
{
 Properties
 {
  [HDR]_Tint("治疗绿光",Color)=(.12,1,.35,1)
  _Outline("深色轮廓",Color)=(.005,.13,.055,1)
  _Intensity("光芯亮度",Range(0,5))=2.6
  _BeamWidth("射线世界宽度",Range(.2,1.5))=.62
  _Speed("能量流速",Range(.1,5))=1.3
  _PlusSize("加号大小",Range(.05,.35))=.19
  [PerRendererData]_BeamLength("长度宽度比",Float)=8
 }
 SubShader
 {
  Tags {"Queue"="Transparent+36" "RenderType"="Transparent"}
  Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata {float4 vertex:POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   struct v2f {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   float4 _Tint,_Outline;float _Intensity,_Speed,_PlusSize,_BeamLength;
   v2f vert(appdata v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color;return o;}
   float crossShape(float2 p,float size)
   {
    float horizontal=max(abs(p.x)-size,abs(p.y)-size*.28);
    float vertical=max(abs(p.x)-size*.28,abs(p.y)-size);
    return 1-smoothstep(-.013,.013,min(horizontal,vertical));
   }
   float4 frag(v2f i):SV_Target
   {
    float t=_Time.y*_Speed;
    float2 p=i.uv;
    float center=p.y-.5;
    float y=abs(center);
    float outer=1-smoothstep(.4,.48,y);
    float rail=1-smoothstep(.025,.07,abs(y-.3));
    float core=exp(-center*center*95);
    float ribbonOffset=(center-sin(p.x*23-t*8)*.12)*14;
    float ribbon=exp(-ribbonOffset*ribbonOffset);
    float2 cell=float2(frac(p.x*max(2,_BeamLength*.48)-t*1.25)-.5,center);
    float plus=crossShape(cell,_PlusSize);
    float pulse=.85+.15*sin(p.x*55-t*17);
    float endpoints=smoothstep(0,.02,p.x)*smoothstep(1,.98,p.x);
    float3 color=lerp(_Outline.rgb,_Tint.rgb, saturate(core*.5+ribbon*.65+rail*.6));
    color+=_Tint.rgb*(core*.48+ribbon*.2+rail*.25)*_Intensity*pulse;
    color=lerp(color,float3(.9,1,.88)*_Intensity,plus);
    return float4(color*i.color.rgb,outer*endpoints*.94*i.color.a);
   }
   ENDCG
  }
 }
}
