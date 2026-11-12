Shader "RevGame/Pvp/Duel Arena Energy"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            struct Attributes
            {
                float4 position : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 settings : TEXCOORD1;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 settings : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.position);
                output.color = input.color;
                output.uv = input.uv;
                output.settings = input.settings;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                float2 xy = uv * 2.0 - 1.0;
                float phase = saturate(input.settings.y);
                float shape = input.settings.x;
                half core;
                half glow;
                if (shape < 0.5)
                {
                    // 结界两侧是缓慢流动的法纹。触边后法纹从脚边向上扫过。
                    float band = exp2(-30.0 * abs(uv.y - phase));
                    float fade = smoothstep(0.0, 0.13, uv.y) * (1.0 - smoothstep(0.68, 1.0, uv.y));
                    float drift = abs(frac(uv.y * 12.0 - _Time.y * 1.3) - 0.5);
                    float diamond = abs(xy.x) + drift * 2.0;
                    core = (1.0 - smoothstep(0.035, 0.16, abs(xy.x))) * fade;
                    core += (1.0 - smoothstep(0.02, 0.12, abs(diamond - 0.55))) * band * fade;
                    glow = exp2(-5.0 * abs(xy.x)) * fade * (0.28 + band);
                }
                else if (shape < 1.5)
                {
                    // 刀光头部亮且窄，尾迹向后逐步散成暗色。
                    float taper = smoothstep(0.0, 0.7, uv.x) * (1.0 - smoothstep(0.88, 1.0, uv.x));
                    float width = lerp(0.08, 0.43, uv.x);
                    core = (1.0 - smoothstep(width * 0.32, width, abs(xy.y))) * taper;
                    glow = exp2(-5.0 * abs(xy.y)) * taper * 0.75;
                }
                else
                {
                    // 环形冲击由里向外传播，到达图形边缘之前自然消隐。
                    float radius = lerp(0.12, 0.91, phase);
                    float distance = abs(length(xy) - radius);
                    core = (1.0 - smoothstep(0.015, 0.077, distance)) * (1.0 - phase);
                    glow = exp2(-15.0 * distance) * (1.0 - phase) * 0.55;
                }
                half opacity = saturate(core + glow * 0.66) * input.color.a;
                half3 light = input.color.rgb * (0.75 + 0.42 * glow) + 0.48 * core;
                return half4(light * opacity, opacity * 0.74);
            }
            ENDCG
        }
    }
}
