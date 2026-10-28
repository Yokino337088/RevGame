// CC0魔法符号作为独立共享叠层，六章核心几何变色；贴图只在一个包中，避免六章重复依赖图片。
Shader "RevGame/Helper/Rune"
{
 Properties
 {
  _MainTex("魔法符号",2D)="white"{}
  [HDR]_Tint("古金符文",Color)=(1,.72,.24,1)
  _Intensity("亮度",Range(0,4))=1.1
 }
 SubShader
 {
  Tags {"Queue"="Transparent+31" "RenderType"="Transparent"}
  Cull Off ZWrite Off Blend SrcAlpha One
  Pass
  {
   CGPROGRAM
   #pragma vertex vert
   #pragma fragment frag
   #include "UnityCG.cginc"
   struct appdata {float4 vertex:POSITION;float2 uv:TEXCOORD0;};
   struct v2f {float4 pos:SV_POSITION;float2 uv:TEXCOORD0;};
   sampler2D _MainTex;float4 _Tint;float _Intensity;
   v2f vert(appdata i){v2f o;o.pos=UnityObjectToClipPos(i.vertex);o.uv=i.uv;return o;}
   half4 frag(v2f i):SV_Target
   {
    float a=tex2D(_MainTex,i.uv).a;
    return half4(_Tint.rgb*_Intensity,a*(.6+.4*sin(_Time.y*6)));
   }
   ENDCG
  }
 }
}
