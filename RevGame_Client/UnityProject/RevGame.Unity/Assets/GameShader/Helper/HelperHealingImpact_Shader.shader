// 空心治疗环与升腾加号。数学形状没有低分辨率贴图，放大时保持清楚；不是影响范围判定。
Shader "RevGame/Helper/HealingImpact"
{
 Properties
 {
  [HDR]_Tint("加号绿光",Color)=(.15,1,.38,1)
  _Intensity("亮度",Range(.5,5))=2.2
  _Speed("升腾速度",Range(.1,4))=1.1
 }
 SubShader
 {
  Tags {"Queue"="Transparent+7" "RenderType"="Transparent"}
  Blend SrcAlpha OneMinusSrcAlpha ZWrite Off Cull Off
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata {float4 vertex:POSITION;float2 uv:TEXCOORD0;};
   struct v2f {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;};
   float4 _Tint;float _Intensity,_Speed;
   v2f vert(appdata v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;return o;}
   float crossShape(float2 p)
   {
    float d=min(max(abs(p.x)-.066,abs(p.y)-.018),max(abs(p.x)-.018,abs(p.y)-.066));
    return 1-smoothstep(-.006,.006,d);
   }
   float4 frag(v2f i):SV_Target
   {
    float2 p=i.uv-.5;float t=_Time.y*_Speed;
    float r=length(float2(p.x,p.y*1.25));
    float ring=1-smoothstep(.008,.027,abs(r-(.32+.018*sin(t*5))));
    float shadow=1-smoothstep(.028,.05,abs(r-.32));
    float plus=0;
    [unroll]for(int n=0;n<3;n++)
    {
     float phase=frac(t*.55+n*.3333);
     float2 c=float2((n-1)*.25,-.27+phase*.65);
     plus=max(plus,crossShape(p-c)*sin(phase*3.14159));
    }
    float alpha=saturate(shadow*.6+plus);
    float3 color=lerp(float3(.003,.1,.045),_Tint.rgb*_Intensity,saturate(ring+plus));
    color+=plus*float3(.65,.9,.6);
    return float4(color,alpha);
   }
   ENDCG
  }
 }
}
