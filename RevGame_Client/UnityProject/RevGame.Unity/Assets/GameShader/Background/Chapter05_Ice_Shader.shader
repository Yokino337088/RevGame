// 冰雪极地专用：冰面保持固态，裂纹反光、边缘霜雾与三层不同风速的雪花形成清冷空间。
Shader "RevGame/Background/Chapter05_Ice"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第五章 · 冰封极地原画", 2D) = "white" {}
        _Strength ("冰雪总强度", Range(0,1)) = 0.85
        _Speed ("雪与寒雾速度", Range(0,2)) = 0.65
        _Distortion ("边缘寒气微扰", Range(0,0.01)) = 0.001
        _Glow ("冰面扫光强度", Range(0,1.5)) = 0.7
        _Particles ("飘雪密度", Range(0,1)) = 0.8
        _SnowWind ("雪花横向风速", Range(-1,1)) = 0.35
        _Frost ("边缘冰霜厚度", Range(0,1)) = 0.65
        _ColdTone ("清冷蓝白色调", Range(0,1)) = 0.55
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("雪白冰光", Color) = (0.8,0.96,1,1)
        _Secondary ("寒雾颜色", Color) = (0.45,0.74,1,1)
        _BoardProtection ("棋盘可读性保护", Range(0,1)) = 0.82
        [HideInInspector] _BoardRect ("棋盘区域", Vector) = (0,0,0,0)
        [HideInInspector] _Aspect ("原画比例", Float) = 1.777778
        [HideInInspector] _EffectTime ("场景时钟", Float) = -1
        [HideInInspector] _PreviewTime ("测试时钟", Float) = -1
        [HideInInspector] _Transition ("入场进度", Float) = 1
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Opaque" "CanUseSpriteAtlas"="False" }
        Cull Off ZWrite Off ZTest LEqual Blend Off
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex ChapterVert
            #pragma fragment IceFrag
            #include "ChapterBackgroundCommon.cginc"
            float _SnowWind, _Frost, _ColdTone;

            float IceSnow(float2 p, float time, float scale, float fall, float size, float seed)
            {
                float2 q = p * scale + float2(-time * _SnowWind * fall, time * fall);
                float2 cell = floor(q);
                float h = ChapterHash(cell + seed);
                float2 center = 0.18 + 0.64 * float2(ChapterHash(cell + seed + 7), ChapterHash(cell + seed + 13));
                float2 snowOffset = frac(q) - center;
                snowOffset.x += sin(time * 0.65 + h * 17.0) * 0.12;
                float radius = length(snowOffset);
                float snow = 1.0 - smoothstep(size * 0.2, size, radius);
                return snow * step(h, _Particles * 0.82) * (0.72 + h * 0.28);
            }

            fixed4 IceFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = ChapterPlane(uv);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float2 margin = min(uv, 1.0 - uv);
                float edgeDistance = min(margin.x, margin.y);
                float grain = ChapterNoise(p * 9.0 + float2(time * 0.025, 0));
                float frost = (1.0 - smoothstep(0.015, 0.08 + grain * 0.08, edgeDistance)) * _Frost;
                float fog = ChapterNoise(p * float2(3.5, 6.0) + float2(time * 0.16, -time * 0.06));
                float2 warp = float2(sin(p.y * 8.0 + time * 0.35), 0) * _Distortion * frost;
                float3 art = ChapterArt(uv, warp, amount);
                float sweep = exp(-pow((uv.x + uv.y * 0.55 - 0.82 - sin(time * 0.27) * 0.7) * 12.0, 2.0));
                // 从原画提取明亮裂纹，只令既有裂纹闪过冰光，不让固态冰面像水一样流动。
                float cracks = smoothstep(0.68, 0.96, min(art.r, min(art.g, art.b)));
                float farSnow = IceSnow(p, time * 0.55, 36.0, 0.65, 0.055, 2.1);
                float middleSnow = IceSnow(p, time, 23.0, 0.9, 0.068, 7.7);
                float nearSnow = IceSnow(p + 3.7, time, 13.0, 1.15, 0.085, 19.2);
                float3 color = lerp(art, art * float3(0.9, 0.98, 1.08), amount * _ColdTone);
                color = lerp(color, _Accent.rgb, frost * fog * amount * 0.15);
                color += (_Accent.rgb * (farSnow * 0.35 + middleSnow * 0.55 + nearSnow * 0.8 + sweep * cracks * 0.18)
                    + _Secondary.rgb * frost * fog * 0.07) * _Glow * amount;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
