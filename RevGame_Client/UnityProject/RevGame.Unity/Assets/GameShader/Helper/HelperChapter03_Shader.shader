// 帮手专属菱形晶体：几何切面、内核符文、外圈魔纹与光晕。每章共享独立材质，不占棋子弹幕资源。
Shader "RevGame/Helper/HelperChapter03"
{
 Properties
 {
  _MainTex("粒子采样（默认白色）",2D)="white"{}
  [HDR]_Tint("主题色",Color)=(0.16,0.75,1,1)
  _Intensity("亮度",Range(0,6))=2
  _Speed("流动速度",Range(0,8))=2
  _Softness("光核宽度",Range(1,12))=5
 }
 SubShader
 {
  Tags {"Queue"="Transparent+30" "RenderType"="Transparent"}
  Blend SrcAlpha One
  ZWrite Off Cull Off
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct input {float4 vertex:POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   struct output {float4 pos:SV_POSITION;float2 uv:TEXCOORD0;float4 color:COLOR;};
   sampler2D _MainTex;float4 _Tint;float _Intensity,_Speed,_Softness;
   output vert(input v){output o;o.pos=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color;return o;}
   float4 frag(output i):SV_Target
   {
    float2 p=i.uv*2-1;float t=_Time.y*_Speed;
    float d=abs(p.x)+abs(p.y);
    float diamond=1-smoothstep(.52,.61,d);
    float edge=1-smoothstep(.015,.055,abs(d-.55));
    float facets=step(0,p.x*p.y);
    float inner=1-smoothstep(.1,.45,d);
    float glyph=exp(-abs(p.x)*70)*step(abs(p.y),.38);
    glyph+=exp(-abs(abs(p.y)-.14)*70)*step(abs(p.x),.17);
    float ring=1-smoothstep(.008,.035,abs(d-.82));
    float dots=pow(saturate(sin(atan2(p.y,p.x)*8+t)),14)*ring;
    float aura=exp(-abs(d-.57)*12)*.28;
    float pulse=.78+.22*sin(t*3+p.y*5);
    float3 baseColor=_Tint.rgb*lerp(.58,1.25,facets);
    float3 color=baseColor*diamond+float3(1,.88,.4)*(edge*.65+glyph*.35);
    color+=_Tint.rgb*(inner*.65+ring*.35+dots*.8+aura);
    float alpha=saturate(diamond+edge+glyph*.3+ring*.65+aura)*pulse;
    return float4(color*_Intensity*i.color.rgb,alpha*i.color.a);
   }
   ENDCG
  }
 }
}
