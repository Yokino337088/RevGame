// ============================================================
// EndlessShieldZone —— 盾战防御区（新手先读这段）
//
// 一整块覆盖 9×9 棋盘的面片，1 次绘制。_Zone[81] 是每格是否在防御区内（0/1，下标 y*9+x），
// 由表现层在防御区形状变化时用 MaterialPropertyBlock 上传一次；多块防御区在 C# 里已合并成并集，
// 所以这里画出来天然就是“合并后的整片区域”，重叠处不会更亮。
// 外观要求柔和：淡蓝底色 + 缓慢流动的六边形微光 + 只在区域外边缘出现的柔光描边（区内相邻格之间没有线）。
// ============================================================
Shader "RevGame/Endless/ShieldZone"
{
    Properties
    {
        _Tint ("区域颜色", Color) = (.45, .75, 1, 1)
        _Fill ("底色透明度", Range(0, .4)) = .1
        _Edge ("边缘透明度", Range(0, 1)) = .5
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
            float _Zone[81];
            fixed4 _Tint;
            float _Fill, _Edge;
            struct app { float4 vertex:POSITION; };
            struct v2f { float4 vertex:SV_POSITION; float2 world:TEXCOORD0; };
            v2f vert(app v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xy;
                return o;
            }
            // 越界格视为不在区内，这样棋盘边缘也会画出描边。
            float zoneAt(int x, int y)
            {
                if (x < 0 || x > 8 || y < 0 || y > 8)
                {
                    return 0;
                }
                return _Zone[y * 9 + x];
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // 无尽棋盘第 N 格的中心在坐标 N，格子边界在 N±0.5。
                int cx = (int)floor(i.world.x + .5);
                int cy = (int)floor(i.world.y + .5);
                float inside = zoneAt(cx, cy);
                if (inside < .5)
                {
                    return 0;
                }
                float2 local = i.world - float2(cx, cy);
                // 只在“这一侧邻格不在区内”时才有边：区内相邻格之间不画线，合并后是一整片。
                float edge = 0;
                edge = max(edge, (1 - zoneAt(cx - 1, cy)) * smoothstep(-.32, -.5, local.x));
                edge = max(edge, (1 - zoneAt(cx + 1, cy)) * smoothstep(.32, .5, local.x));
                edge = max(edge, (1 - zoneAt(cx, cy - 1)) * smoothstep(-.32, -.5, local.y));
                edge = max(edge, (1 - zoneAt(cx, cy + 1)) * smoothstep(.32, .5, local.y));
                // 六边形微光：两组斜向条纹交错，缓慢流动；幅度很小，保持“淡”。
                float t = _Time.y * .35;
                float hex = abs(sin((i.world.x * 1.732 + i.world.y) * 4.2 + t)) * abs(sin((i.world.x * 1.732 - i.world.y) * 4.2 - t)) * abs(sin(i.world.y * 8.4 + t * .7));
                float shimmer = pow(1 - hex, 8) * .12;
                float pulse = .85 + .15 * sin(_Time.y * 2);
                float alpha = _Fill + shimmer + edge * _Edge * pulse;
                fixed3 color = lerp(_Tint.rgb, fixed3(.85, .95, 1), edge * .6);
                return fixed4(color, saturate(alpha));
            }
            ENDCG
        }
    }
}
