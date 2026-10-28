// 蓝束命中端：扩散环、十字电芒与白蓝冲击核，仅表现不追加伤害。
Shader "RevGame/Helper/BeamImpact"
{
 Properties
 {
  [HDR]_Tint("蓝色能量",Color)=(.08,.55,1,1)
  _Intensity("亮度",Range(0,8))=3.5
  _Speed("扩散速度",Range(.1,8))=3
 }
 SubShader
 {
  Tags {"Queue"="Transparent+32" "RenderType"="Transparent"}
  Cull Off ZWrite Off Blend SrcAlpha One
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct input {float4 vertex:POSITION;float2 uv:TEXCOORD0;};
   struct output {float4 pos:SV_POSITION;float2 uv:TEXCOORD0;};
   float4 _Tint;float _Intensity,_Speed;
   output vert(input i){output o;o.pos=UnityObjectToClipPos(i.vertex);o.uv=i.uv;return o;}
   half4 frag(output i):SV_Target
   {
    float2 p=i.uv*2-1;float r=length(p);float t=_Time.y*_Speed;
    float ring=1-smoothstep(.015,.08,abs(r-(.35+frac(t)*.5)));
    float second=1-smoothstep(.02,.075,abs(r-(.3+frac(t+.5)*.48)));
    float core=exp(-r*r*25);
    float rays=(exp(-abs(p.x)*38)+exp(-abs(p.y)*38))*exp(-r*2);
    float a=saturate(core+ring*.6+second*.35+rays*.5)*(1-smoothstep(.85,1,r));
    return half4((_Tint.rgb*(ring+second+rays)+float3(.85,.98,1)*core*1.7)*_Intensity,a);
   }
   ENDCG
  }
 }
}
