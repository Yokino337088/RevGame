// 助阵帮手章节粒子：加法混合与软核，强度/速度/宽度可以在共享材质上调节。
Shader "RevGame/Helper/HelperBlueBeam"
{
 Properties
 {
  _MainTex("粒子采样（默认白色）",2D)="white"{}
  [HDR]_Tint("主题色",Color)=(0.06,0.48,1,1)
  _Intensity("亮度",Range(0,8))=3.8
  _BeamWidth("光束世界宽度",Range(.2,2))=1.05
  _Pulse("束宽脉动",Range(0,.3))=.14
  _CoreWidth("白蓝光核宽度",Range(.03,.6))=.2
  _ImpactScale("命中冲击直径",Range(.5,4))=1.8
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
   sampler2D _MainTex;float4 _Tint;float _Intensity,_Speed,_Softness,_CoreWidth;
   output vert(input v){output o;o.pos=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color;return o;}
   float4 frag(output i):SV_Target
   {
    float2 p=i.uv;float t=_Time.y*_Speed;
    float y=abs(p.y-.5)*2;
    float core=1-smoothstep(_CoreWidth*.45,_CoreWidth,y);
    float halo=exp(-y*y*3.5);
    float pattern=.78+.22*sin(p.x*70-t*7+sin(p.y*34+t)*3);
    float strand=1-smoothstep(.025,.065,abs(p.y-.5-sin(p.x*24-t*5)*.21));
    float strand2=1-smoothstep(.015,.05,abs(p.y-.5+sin(p.x*19-t*6)*.31));
    float edge=smoothstep(0,.08,p.x)*smoothstep(1,.85,p.x);
    float source=tex2D(_MainTex,p.yx).a;
    float alpha=saturate(core+halo*.45+strand*.75+strand2*.55)*edge;
    float3 col=(_Tint.rgb*(halo*.7+strand+strand2*.7)+float3(.78,.97,1)*core*(1.2+pattern*.3))*_Intensity;
    col*=.85+.15*source;
    return float4(col*i.color.rgb,alpha*i.color.a);
   }
   ENDCG
  }
 }
}
