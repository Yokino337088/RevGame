Shader "RevGame/Pvp/ArenaBackdrop"
{
    Properties
    {
        // 决斗 Image 传入擂台图，围棋背景网格从自己的材质读取无尽神殿原画。
        _MainTex ("背景原画", 2D) = "white" {}
        // 0 为拳击擂台，1 为无尽神殿。每个场景用一份共享材质设置。
        [Enum(BoxingRing,0,EndlessTemple,1)] _Style ("场景风格", Float) = 0
        // 光效强度，0 时显示原画，1 时使用美术设定的完整效果。
        _Strength ("光效强度", Range(0,1)) = 1
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            // 共用一张底图和 Unity 表现时钟，既不分配材质，也不依赖帧同步逻辑帧。
            sampler2D _MainTex;
            float _Style, _Strength;

            // UI 矩形和世界 Quad 共用采样，保留各自的顶点色。
            struct Input
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                half4 tint : COLOR;
            };

            // 投影后仍保留 0~1 的原画坐标，方便灯光对齐构图。
            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 tint : COLOR;
            };

            // 把物体坐标变成 GPU 的裁剪空间。屏幕尺寸改变时，特效与画面仍使用同一套 UV。
            Varyings vert(Input input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                output.tint = input.tint;
                return output;
            }

            // 圆心在原画 UV 中。按横版宽高比修正距离，让光晕不会被画面拉成横条。
            float Glow(float2 uv, float2 center, float radius)
            {
                return 1 - smoothstep(0, radius, length((uv - center) * float2(1.7778, 1)));
            }

            // 擂台用整张长图。只让天空、旗帜和两侧的光纹轻微流动，脚下砖缝与晶柱底座保持稳定。
            float2 ArenaFlow(float2 uv, float time)
            {
                float flags = smoothstep(.57, .78, uv.y) * (1 - smoothstep(.92, 1, uv.y));
                float ripples = sin(uv.x * 43 + uv.y * 13 - time * 1.62) + .4 * sin(uv.x * 87 - time * 2.8);
                return saturate(uv + float2(ripples * .003, sin(uv.x * 49 + time * 1.4) * .005) * flags);
            }

            // 阳光和晶柱脉冲作用于同一次原画采样。天空穿过薄雾，低处只有少量扬尘。
            half3 BoxingLights(float2 uv, float time, half3 art)
            {
                float sky = smoothstep(.46, .83, uv.y);
                float mist = smoothstep(.26, .90, .5 + .5 * sin(uv.x * 24 - uv.y * 11 + sin(uv.y * 16 - time * .5) * 1.5 - time * .65));
                float shafts = pow(.5 + .5 * sin(uv.x * 41 + uv.y * 16 - time * .35), 4);
                float pillars = Glow(uv, float2(.173, .36), .17) + Glow(uv, float2(.827, .36), .17);
                float wave = .38 + .62 * pow(.5 + .5 * sin(uv.y * 25 - time * 2.5), 3);
                float2 grid = uv * float2(92, 28) - time * float2(2.1, 1.4);
                float seed = frac(sin(dot(floor(grid), float2(27.31, 41.17))) * 13758.545);
                float mote = (1 - smoothstep(.035, .15, length(frac(grid) - float2(.21 + .57 * seed, .52)))) * step(.82, seed);
                float dust = mote * smoothstep(.19, .34, uv.y) * (1 - smoothstep(.53, .74, uv.y));
                half warm = saturate(art.r * 1.15);
                return sky * (mist * .42 + shafts * .32) * half3(.30h, .22h, .10h)
                    + pillars * wave * half3(.065h, .42h, .32h)
                    + dust * warm * half3(.58h, .39h, .13h);
            }

            // 拱顶投下金色光束，两侧柱廊映出紫色魔力和上升的星火。棋盘中心由棋盘自身遮盖。
            float3 TempleLights(float2 uv, float time)
            {
                float2 sun = uv - float2(.50, 1.14);
                float rays = pow(.5 + .5 * sin(atan2(sun.x, -sun.y) * 25 + time * .85), 5);
                float sky = smoothstep(.32, .84, uv.y);
                float pulse = .65 + .21 * sin(time * 2.5) + .12 * sin(time * 5.4);
                float colonnade = Glow(uv, float2(.19, .45), .32) + Glow(uv, float2(.81, .45), .32);

                // 以 UV 网格给每颗火星一个固定种子。时间只移动网格，GPU 不用创建游戏对象。
                float2 drift = uv * float2(42, 25) - float2(.12, time * 1.85);
                float seed = frac(sin(dot(floor(drift), float2(21.23, 71.91))) * 43758.54);
                float2 sparkCenter = float2(.25 + seed * .5, .5);
                float spark = 1 - smoothstep(.03, .15, length(frac(drift) - sparkCenter));
                float wings = smoothstep(.18, .36, abs(uv.x - .5));
                float embers = spark * step(.75, seed) * wings * smoothstep(.18, .41, uv.y);

                return rays * sky * float3(.42, .25, .10) + colonnade * pulse * float3(.29, .07, .50)
                    + embers * float3(.65, .44, .18);
            }

            // 只采样一次原画。亮度随原画本身抬升，暗处保留层次，透明度交回原始渲染流程。
            half4 frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                half4 art;
                if (_Style < .5)
                {
                    art = tex2D(_MainTex, lerp(uv, ArenaFlow(uv, _Time.y), _Strength));
                    half3 light = BoxingLights(uv, _Time.y, art.rgb);
                    art.rgb = 1 - (1 - art.rgb) * (1 - saturate(light * _Strength));
                }
                else
                {
                    art = tex2D(_MainTex, uv);
                    art.rgb += TempleLights(uv, _Time.y) * (.42 + .58 * art.rgb) * _Strength;
                }
                return art * input.tint;
            }
            ENDCG
        }
    }
}
