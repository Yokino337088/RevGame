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

            // UI 矩形与世界 Quad 都提供位置和 UV。颜色由原画及材质控制。
            struct Input
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            // 投影后仍保留 0~1 的原画坐标，方便灯光对齐构图。
            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // 把物体坐标变成 GPU 的裁剪空间。屏幕尺寸改变时，特效与画面仍使用同一套 UV。
            Varyings vert(Input input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            // 圆心在原画 UV 中。按横版宽高比修正距离，让光晕不会被画面拉成横条。
            float Glow(float2 uv, float2 center, float radius)
            {
                return 1 - smoothstep(0, radius, length((uv - center) * float2(1.7778, 1)));
            }

            // 擂台顶部两盏灯缓慢扫向中央，绳索附近扫过金光。保留角色交手区域的原画对比度。
            float3 BoxingLights(float2 uv, float time)
            {
                float height = saturate(1 - uv.y);
                float leftAxis = .22 + height * (.25 + .13 * sin(time * 1.5));
                float rightAxis = .78 - height * (.25 + .13 * sin(time * 1.5 + 1.8));
                float beamWidth = .025 + height * .11;
                float left = 1 - smoothstep(.01, beamWidth, abs(uv.x - leftAxis));
                float right = 1 - smoothstep(.01, beamWidth, abs(uv.x - rightAxis));
                float upper = smoothstep(.30, .78, uv.y);
                float ropes = 1 - smoothstep(.01, .11, abs(uv.y - .40));
                float glint = pow(.5 + .5 * sin(uv.x * 18 - time * 3), 9) * ropes;
                return (left * float3(.28, .10, .51) + right * float3(.45, .19, .09)) * upper
                    + glint * float3(.32, .18, .045);
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
                half4 art = tex2D(_MainTex, input.uv);
                float3 light = _Style < .5 ? BoxingLights(input.uv, _Time.y) : TempleLights(input.uv, _Time.y);
                art.rgb += light * (.42 + .58 * art.rgb) * _Strength;
                return art;
            }
            ENDCG
        }
    }
}
