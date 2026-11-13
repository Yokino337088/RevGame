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

            // 图集 UV 只采样原画。火流使用屏幕坐标，重打图集或宽屏裁切都不会改变汇聚点。
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

            // 在同一全屏材质中画出两股镜像火流、细亮边和上升火星。中央仅保留少量交汇余光。
            fixed4 frag(Pixels input) : SV_Target
            {
                fixed4 baseArt = tex2D(_MainTex, input.uv) * input.color;
                float2 xy = input.screen.xy / input.screen.w * 2.0 - 1.0;
                float inward = 1.0 - abs(xy.x);
                float time = _Time.y;

                // 横轴从两侧边缘走向中心。纵轴剪切把火舌倾向斜上方，三层噪声各用不同速度和尺度。
                float2 flow = float2(inward * 6.0, (xy.y - inward * .68) * 3.8);
                float warp = FlowNoise(flow * .64 - float2(time * .62, time * .20));
                float fuel = FlowNoise(flow - float2(time * 1.80, time * .37) + float2(0, warp * 1.65));
                float detail = FlowNoise(flow * float2(2.1, 2.7) - float2(time * 3.20, time * .66));
                float strand = 1.0 - abs(sin((flow.y * 1.48 + warp * 1.34 + fuel * .44 + detail * .23) * 3.14159));
                float body = smoothstep(.35, .93, fuel * .72 + detail * .24 + strand * .28);
                // 亮边随细噪声断开，形成局部翻卷的火舌，避免整屏出现等亮度的长线。
                float rim = smoothstep(.75, .99, strand) * smoothstep(.42, .83, fuel * .72 + detail * .38);

                // 斜向亮带从两角开始，在中央操作区前逐渐收窄并降低亮度。
                float lower = 1.0 - smoothstep(.13, .54, abs(xy.y + .66 - inward * .79));
                float upper = 1.0 - smoothstep(.08, .44, abs(xy.y - .77 + inward * .64));
                float wing = smoothstep(.13, .54, abs(xy.x));
                float heat = wing * (.19 + .61 * lower + .37 * upper);
                float contact = (1.0 - smoothstep(0.0, .20, abs(xy.x))) *
                    (1.0 - smoothstep(.06, .42, abs(xy.y - .10)));

                // 火星跟随火流斜向上浮。只点亮少量网格，保留立绘与读数的轮廓。
                float2 grid = float2(inward * 29.0 - time * 5.3, (xy.y - inward * .69) * 18.0 - time * 2.8);
                float random = CellValue(floor(grid));
                float2 particle = (frac(grid) - float2(.30 + random * .37, .5)) * float2(.78, 1.75);
                float sparks = (1.0 - smoothstep(.025, .17, length(particle))) * step(.90, random) * wing;

                float side = smoothstep(-.12, .12, xy.x);
                float3 flameColor = lerp(float3(1.0, .095, .018), float3(.025, .55, 1.0), side);
                float3 coreColor = lerp(float3(1.0, .74, .32), float3(.38, .96, 1.0), side);
                float3 light = flameColor * (heat * (.23 + .80 * body) + contact * body * .19);
                light += coreColor * (heat * rim * .55 + sparks * .68);
                // 屏幕混合让高光保留原画纹理，低火量处稍压暗以拉开明暗节奏。
                float3 shaded = baseArt.rgb * (1.0 - heat * (1.0 - body) * .23);
                float3 lit = 1.0 - (1.0 - shaded) * (1.0 - saturate(light));
                baseArt.rgb = lerp(baseArt.rgb, lit, input.color.a);
                #ifdef UNITY_UI_CLIP_RECT
                baseArt.a *= UnityGet2DClipping(input.local, _ClipRect);
                #endif
                return baseArt;
            }
            ENDCG
        }
    }
}
