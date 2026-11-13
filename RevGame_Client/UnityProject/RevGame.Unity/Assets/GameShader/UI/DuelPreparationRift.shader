Shader "RevGame/UI/Duel Preparation Rift"
{
    Properties
    {
        [PerRendererData] _MainTex ("Backdrop sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct Input
            {
                float4 position : POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
            };

            struct Pixels
            {
                float4 position : SV_POSITION;
                fixed4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 local : TEXCOORD1;
                float4 screen : TEXCOORD2;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;

            // 保留 Image 的图集 UV。片元阶段把屏幕中的热流方向换算成当前图集里的采样偏移。
            Pixels vert(Input input)
            {
                Pixels output;
                output.position = UnityObjectToClipPos(input.position);
                output.color = input.color * _Color;
                output.uv = input.uv;
                output.local = input.position.xy;
                output.screen = ComputeScreenPos(output.position);
                return output;
            }

            // 每个网格点得到稳定的 0~1 随机值，供火焰和火星共用，避免再采样一张噪声贴图。
            float CellValue(float2 cell)
            {
                float3 seed = frac(float3(cell.xyx) * .1031);
                seed += dot(seed, seed.yzx + 33.33);
                return frac((seed.x + seed.y) * seed.z);
            }

            // 对网格四角的随机值做平滑插值，跨格子时火舌形状连续变化。
            float FlowNoise(float2 position)
            {
                float2 cell = floor(position);
                float2 blend = frac(position);
                blend = blend * blend * (3.0 - 2.0 * blend);
                float lower = lerp(CellValue(cell), CellValue(cell + float2(1, 0)), blend.x);
                float upper = lerp(CellValue(cell + float2(0, 1)), CellValue(cell + float2(1, 1)), blend.x);
                return lerp(lower, upper, blend.y);
            }

            // 依据两股相向的流场重新采样背景。原图纹理本身随热流卷动，标题和立绘仍在自己的固定坐标上。
            fixed4 frag(Pixels input) : SV_Target
            {
                float2 xy = input.screen.xy / input.screen.w * 2.0 - 1.0;
                float inward = 1.0 - abs(xy.x);
                float time = _Time.y + step(0.0, xy.x) * 3.6;

                float2 flow = float2(inward * 7.0, (xy.y - inward * .58) * 4.5);
                float roll = FlowNoise(flow * .72 - float2(time * 1.48, time * .32));
                float fuel = FlowNoise(flow * float2(1.7, 2.25) - float2(time * 3.05, time * .72) + float2(0, roll * 2.15));
                float wing = smoothstep(.10, .42, abs(xy.x));
                float outer = smoothstep(.04, .28, inward) * smoothstep(.04, .27, 1.0 - abs(xy.y));
                float warp = wing * outer;
                float direction = xy.x < 0.0 ? 1.0 : -1.0;
                float2 displacement = float2(direction * (roll - .48) * .25, (fuel - .50) * .14 - .027) * warp;
                // 偏移在屏幕边缘与中缝归零。限制偏移留在当前可见的 Sprite 内，避免采样到邻接图集。
                displacement = clamp(displacement, -.80 * (xy + 1.0), .80 * (1.0 - xy));
                float2 uvRight = ddx(input.uv) / max(abs(ddx(xy.x)), 1e-5);
                float2 uvUp = ddy(input.uv) / (ddy(xy.y) < 0.0 ? min(ddy(xy.y), -1e-5) : max(ddy(xy.y), 1e-5));
                float2 advected = input.uv + uvRight * displacement.x + uvUp * displacement.y;
                half4 baseArt = tex2D(_MainTex, advected) * input.color;

                // 原图已有的红蓝亮纹决定燃烧位置。暗部只跟着扭动，避免生成盖住原画的大块光带。
                half chroma = max(baseArt.r, max(baseArt.g, baseArt.b));
                half heat = wing * smoothstep(.20h, .81h, chroma) * (.24h + .76h * smoothstep(.27, .84, fuel));
                half rim = heat * smoothstep(.53, .89, fuel + roll * .23);
                float2 grid = float2(inward * 38.0 - time * 9.0, (xy.y - inward * .68) * 24.0 - time * 4.6);
                float random = CellValue(floor(grid));
                float2 particle = (frac(grid) - float2(.22 + random * .54, .5)) * float2(.72, 1.6);
                half sparks = (1.0 - smoothstep(.03, .20, length(particle))) * step(.84, random) * heat;
                half3 hotColor = lerp(half3(1, .55, .18), half3(.22, .82, 1), step(0.0, xy.x));
                half3 movingArt = baseArt.rgb * (.83h + .49h * heat);
                baseArt.rgb = lerp(movingArt, hotColor, saturate(rim * .56h + sparks * .75h));
                #ifdef UNITY_UI_CLIP_RECT
                baseArt.a *= UnityGet2DClipping(input.local, _ClipRect);
                #endif
                return baseArt;
            }
            ENDCG
        }
    }
}
