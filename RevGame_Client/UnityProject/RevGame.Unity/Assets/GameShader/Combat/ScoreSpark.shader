// 单个小四边形画星核、脉冲与余辉；不用额外纹理、GrabPass、全屏透明层或材质实例。
Shader "RevGame/Combat/ScoreSpark"
{
    Properties { [PerRendererData] _MainTex ("UI白纹理", 2D) = "white" {} }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"
            struct appdata { float4 vertex:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.uv; o.color=v.color; return o; }
            fixed4 frag(v2f i):SV_Target
            {
                float2 p=(i.uv-.5)*2;
                float core=saturate(1-length(p)*3.8);
                float diamond=saturate(1-(abs(p.x)*.65+abs(p.y)*2.8))*saturate(1-length(p));
                float rays=saturate(1-abs(p.x)*24)*saturate(1-abs(p.y)*1.4)
                    +saturate(1-abs(p.y)*24)*saturate(1-abs(p.x)*1.4);
                float halo=saturate(1-length(p))*saturate(1-length(p))*.28;
                float pulse=.88+.12*sin(_Time.y*16+p.x*5);
                float alpha=saturate(core+diamond*.65+rays*.8+halo)*i.color.a*pulse;
                return fixed4(lerp(i.color.rgb,fixed3(1,1,.95),core*.8),alpha);
            }
            ENDCG
        }
    }
}
