// 新版 Loading 满幅动效。仅为原画增加青金拱内光、向上飘动的光尘和柔雾。
Shader "RevGame/UI/LoadingPortal"
{
    Properties
    {
        // RawImage 按等比裁切传入原画和采样坐标。此贴图不进入 Sprite Atlas。
        [PerRendererData] _MainTex ("加载底图", 2D) = "white" {}
        // 共享材质保持白色，面板透明度来自 uGUI 顶点色。
        _Color ("整体颜色", Color) = (1,1,1,1)
        // 青色光纹用于贴合拱门的主弧和门内薄雾。
        _TealLight ("青色光纹", Color) = (0.16,0.88,0.71,1)
        // 琥珀色用于与主弧反向游动的飞光和漂浮光尘。
        _GoldLight ("琥珀飞光", Color) = (1,0.68,0.28,1)
        // 光效合成的总倍率，0 只显示原画及其中心的对比度调整。
        _EffectStrength ("奥术光效强度", Range(0,1)) = 0.8
        // Unity 时间的倍率，0 固定于首帧姿态，各光效保持同一时钟。
        _Speed ("奥术光效速度", Range(0,2)) = 1
        // UGUI 蒙版和剪裁沿用默认属性名，动画不改变点击与 UI 层级。
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="False" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
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
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            // 面板绘制使用一张图和一个共享材质。仅靠统一计时器更新效果。
            sampler2D _MainTex;
            half4 _Color;
            half4 _TealLight;
            half4 _GoldLight;
            half _EffectStrength;
            float _Speed;
            float4 _ClipRect;

            // RawImage 传入已裁切的图片 UV 与继承的透明度。
            struct PortalVertex
            {
                float4 position : POSITION;
                float2 artUv : TEXCOORD0;
                half4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // 视口坐标独立保护文字区域，两个旋转向量只在顶点阶段随时间计算。
            struct PortalVaryings
            {
                float4 position : SV_POSITION;
                float2 artUv : TEXCOORD0;
                float4 screen : TEXCOORD1;
                float2 localPosition : TEXCOORD2;
                half4 orbit : TEXCOORD3;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            // 保持原画构图的图片 UV 与视口 UV 分离，超宽裁剪时光纹仍贴着原画拱门。
            PortalVaryings Vert(PortalVertex vertex)
            {
                PortalVaryings output;
                UNITY_SETUP_INSTANCE_ID(vertex);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.position = UnityObjectToClipPos(vertex.position);
                output.artUv = vertex.artUv;
                output.screen = ComputeScreenPos(output.position);
                output.localPosition = vertex.position.xy;
                output.color = vertex.color * _Color;
                float t = _Time.y * _Speed;
                float tealSin, tealCos, goldSin, goldCos;
                sincos(t * 0.92 + 0.55, tealSin, tealCos);
                sincos(t * -1.28 + 2.70, goldSin, goldCos);
                output.orbit = half4(tealCos, tealSin, goldCos, goldSin);
                return output;
            }

            // 从格号产生分布固定的光尘位置，避免额外噪声图、实例和逐像素三角函数。
            float3 DustSeed(float2 cell)
            {
                float3 seed = frac(cell.xyx * float3(0.1031, 0.11369, 0.13787));
                seed += dot(seed, seed.yzx + 19.19);
                return frac((seed.xxy + seed.yzz) * seed.zyx);
            }

            // 按原画 UV 由下向上卷动，格内留边保证尘粒经过格界时不瞬间跳变。
            half FloatingDust(float2 artUv, float time)
            {
                float2 grid = artUv * float2(78.0, 46.0) - float2(time * 0.48, time * 1.85);
                float3 seed = DustSeed(floor(grid));
                float2 offset = frac(grid) - (0.19 + 0.62 * seed.xy);
                offset.y *= 0.62;
                float pulse = frac(time * 0.45 + seed.z);
                half twinkle = (half)(1.0 - abs(2.0 * pulse - 1.0));
                half dotLight = (half)saturate(1.0 - dot(offset, offset) * lerp(42.0, 128.0, seed.y));
                return dotLight * dotLight * twinkle * (half)step(0.79, seed.z);
            }

            // 青金光纹走原画拱面，中心文字区用视口坐标保护。最后乘入 uGUI 透明度，沿用原过场的淡出。
            half4 Frag(PortalVaryings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 art = tex2D(_MainTex, input.artUv);
                float2 viewport = input.screen.xy / max(input.screen.w, 0.0001);
                float2 center = abs(viewport - float2(0.50, 0.48));
                float safeZone = smoothstep(0.22, 0.36, center.x) + smoothstep(0.17, 0.33, center.y);
                half outsideTitle = (half)saturate(safeZone);

                // 原画内缘在两侧约 x=0.19/0.81，顶端 y=0.78。地面以上将弧线渐隐。
                float2 ellipse = (input.artUv - float2(0.5, 0.405)) * float2(3.25, 2.63);
                float radius = sqrt(max(dot(ellipse, ellipse), 0.00001));
                float2 direction = ellipse / radius;
                float cutoff = smoothstep(0.145, 0.255, input.artUv.y);
                half tealHead = (half)smoothstep(0.18, 0.98, dot(direction, input.orbit.xy));
                half goldHead = (half)smoothstep(0.56, 0.997, dot(direction, input.orbit.zw));
                float edgeDistance = abs(radius - 0.975);
                half lightCore = (half)(1.0 - smoothstep(0.002, 0.016, edgeDistance));
                half lightHalo = (half)(1.0 - smoothstep(0.012, 0.112, edgeDistance));
                half secondLine = (half)(1.0 - smoothstep(0.003, 0.020, abs(radius - 0.918 - input.orbit.y * 0.012)));
                half rimWeight = outsideTitle * (half)cutoff;
                float time = _Time.y * _Speed;
                half dust = FloatingDust(input.artUv, time) * lightHalo;
                half teal = (lightCore * (0.16h + 0.62h * tealHead) + 0.22h * lightHalo + 0.32h * secondLine * tealHead) * rimWeight;
                half gold = (lightCore * goldHead * 0.57h + secondLine * goldHead * 0.28h + dust * 0.95h) * rimWeight;

                // 雾只做低频明暗，不在标题后面绘制尖锐轮廓。
                float valley = abs(input.artUv.y - 0.29 + input.orbit.x * 0.08 + ellipse.x * 0.09);
                half veil = (half)((1.0 - smoothstep(0.025, 0.40, valley)) * (1.0 - smoothstep(0.55, 0.93, radius)));
                half fog = veil * (0.035h + 0.11h * outsideTitle);
                art.rgb = saturate(art.rgb * (1.0h - 0.12h * (1.0h - outsideTitle)) +
                    (_TealLight.rgb * (teal + fog) + _GoldLight.rgb * gold) * _EffectStrength);
                half4 result = art * input.color;
                #ifdef UNITY_UI_CLIP_RECT
                result.a *= UnityGet2DClipping(input.localPosition, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(result.a - 0.001h);
                #endif
                return result;
            }
            ENDCG
        }
    }
    Fallback "UI/Default"
}
