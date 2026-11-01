Shader "RevGame/Endless/Impact"
{
    SubShader
    {
        Tags { "Queue"="Transparent+30" "RenderType"="Transparent" }
        Blend SrcAlpha One Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct app {float4 vertex:POSITION;float2 uv:TEXCOORD0;fixed4 color:COLOR;};
            struct v2f {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;fixed4 color:COLOR;};
            v2f vert(app v){v2f o;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color;return o;}
            fixed4 frag(v2f i):SV_Target
            {
                float2 p=i.uv*2-1;
                float r=length(p);
                float rim=pow(saturate(1-abs(r-.75)*5),2);
                float beam=pow(saturate(1-abs(p.y)),4)*.6;
                return float4(i.color.rgb*(1+rim),i.color.a*saturate(rim+beam));
            }
            ENDCG
        }
    }
}
