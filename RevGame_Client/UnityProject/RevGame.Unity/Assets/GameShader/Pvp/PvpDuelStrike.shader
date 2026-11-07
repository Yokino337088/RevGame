Shader "RevGame/Pvp/DuelStrike"
{
    Properties
    {
        [PerRendererData] _MainTex ("招式图层", 2D) = "white" {}
        _Tint ("招式颜色", Color) = (1,1,1,1)
        _Phase ("表现进度", Range(0,1)) = 0
        _Power ("冲击强度", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha One
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Tint;
            float _Phase, _Power;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }
            fixed4 frag(Output i) : SV_Target
            {
                float2 delta = i.uv - .5;
                float radius = length(delta);
                float angle = atan2(delta.y, delta.x);
                // 扩散波环与角向撕裂全部用表现进度推进，不读取服务器时钟。
                float ring = 1 - smoothstep(.009, .054, abs(radius - (.09 + _Phase * .39)));
                float rays = pow(saturate(.5 + .5 * sin(angle * 13 + _Phase * 15 + radius * 9)), 7);
                float2 warped = i.uv + delta * sin(radius * 43 - _Phase * 15) * ring * .09 * _Power;
                fixed4 art = tex2D(_MainTex, saturate(warped));
                float envelope = (1 - smoothstep(.42, 1, _Phase)) * (1 - smoothstep(.43, .50, radius));
                float bloom = ring * (.35 + rays * .65) * _Power;
                return fixed4(art.rgb * _Tint.rgb + bloom * _Tint.rgb, saturate((art.a + bloom) * envelope));
            }
            ENDCG
        }
    }
}
