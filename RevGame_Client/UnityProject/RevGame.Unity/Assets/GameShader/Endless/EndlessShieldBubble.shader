// ============================================================
// EndlessShieldBubble —— 防御区内棋子的半透明淡蓝护盾（新手先读这段）
//
// 所有受保护棋子的护盾画在一张动态网格里，一枚棋子一个方块，整场 1 次绘制。
//   uv (0~1)    方块内坐标，中心 (0.5, 0.5)；
//   uv2.x       相位（0~1），让各护盾的流光错开；
//   color.a     格挡强度（0~1）：防御区刚替这枚棋子挡下一次伤害时为 1，表现层在 0.35 秒内衰减到 0。
// 平时：边缘菲涅尔亮、中间几乎透明的淡蓝圆罩 + 很淡的六边形纹。
// 格挡：整圈提亮，一道波纹从中心向外扩散到边缘，罩子轻微鼓起——就是“挡住了”的反馈。
// ============================================================
Shader "RevGame/Endless/ShieldBubble"
{
    Properties
    {
        _Tint ("护盾颜色", Color) = (.55, .85, 1, 1)
        _Base ("平时透明度", Range(0, .6)) = .32
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Tint;
            float _Base;
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; float2 data:TEXCOORD1; fixed4 color:COLOR; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; float2 data:TEXCOORD1; fixed4 color:COLOR; };
            v2f vert(app v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.data = v.data;
                o.color = v.color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float hit = i.color.a;
                float2 p = (i.uv * 2 - 1) / (1 + hit * .06);
                float r = length(p);
                if (r > 1)
                {
                    return 0;
                }
                // 菲涅尔：越靠边越亮，中间几乎透明，不挡棋子本体。
                float fresnel = pow(saturate(r), 3.5);
                float outline = 1 - smoothstep(.0, .06, 1 - r);
                // 淡淡的六边形纹，随相位缓慢流动。
                float t = _Time.y * .6 + i.data.x * 6.28;
                float hex = abs(sin(p.x * 9 + t)) * abs(sin((p.x * .5 + p.y * .866) * 9 - t)) * abs(sin((p.x * .5 - p.y * .866) * 9 + t * .5));
                float pattern = pow(1 - hex, 10) * .25 * fresnel;
                // 格挡波纹：从中心扩到边缘（hit 从 1 衰减到 0，波纹半径从 0.25 走到 1）。
                float wave = (1 - smoothstep(0, .08, abs(r - lerp(1, .25, hit)))) * hit;
                float alpha = (_Base * fresnel + outline * .45 + pattern) * (1 + hit * 1.2) + wave * .8;
                fixed3 color = lerp(_Tint.rgb, fixed3(1, 1, 1), saturate(outline * .4 + wave + hit * .3));
                return fixed4(color, saturate(alpha));
            }
            ENDCG
        }
    }
}
