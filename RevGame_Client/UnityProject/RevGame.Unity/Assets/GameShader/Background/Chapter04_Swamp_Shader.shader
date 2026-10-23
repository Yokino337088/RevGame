// 死亡沼泽专用：低速域扭曲、浓稠泥浆褶皱、油膜与缓慢生灭的泥泡。
Shader "RevGame/Background/Chapter04_Swamp"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第四章 · 死亡沼泽原画", 2D) = "white" {}
        _Strength ("泥浆动态总强度", Range(0,1)) = 0.85
        _Speed ("黏稠流速", Range(0,2)) = 0.22
        _Distortion ("泥浆位移幅度", Range(0,0.04)) = 0.018
        _Glow ("油膜与泥泡反光", Range(0,1.5)) = 0.65
        _Particles ("泥泡分布密度", Range(0,1)) = 0.55
        _Viscosity ("泥浆浓稠褶皱", Range(0.3,2)) = 1.3
        _BubbleSize ("泥泡半径", Range(0.06,0.3)) = 0.2
        _MudDepth ("褶皱阴影深度", Range(0,0.25)) = 0.1
        _WaterRegion ("泥面区域 xy起点 zw尺寸", Vector) = (0,0,1,1)
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("棕金油膜", Color) = (0.74,0.57,0.26,1)
        _Secondary ("腐沼微光", Color) = (0.42,0.57,0.23,1)
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
            #pragma fragment SwampFrag
            #include "ChapterBackgroundCommon.cginc"
            float _Viscosity, _BubbleSize, _MudDepth;
            float4 _WaterRegion;

            float2 SwampCurrent(float2 p, float time)
            {
                float2 q = p * 3.2;
                q += float2(sin(q.y * 1.4 + time * 0.6), cos(q.x * 1.2 - time * 0.45)) * 0.8;
                q += float2(sin(q.y * 1.8 - time * 0.3), cos(q.x * 1.7 + time * 0.35)) * 0.48;
                return float2(sin(q.y + q.x * 0.4 + time * 0.4), cos(q.x - q.y * 0.5 - time * 0.3));
            }

            fixed4 SwampFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = ChapterPlane(uv);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float2 waterEdge = min(uv - _WaterRegion.xy, _WaterRegion.xy + _WaterRegion.zw - uv);
                float mud = smoothstep(0.0, 0.035, min(waterEdge.x, waterEdge.y));
                float2 current = SwampCurrent(p, time);
                float2 warp = current * _Distortion * mud;
                float3 art = ChapterArt(uv, warp, amount);
                float folds = sin(p.x * 9.0 + p.y * 6.0 + current.x * 3.1 * _Viscosity + time * 0.2);
                float rim = pow(saturate(folds * 0.5 + 0.5), 10.0);
                float slick = pow(saturate(sin(p.y * 14.0 + current.y * 3.5 - time * 0.35) * 0.5 + 0.5), 13.0);
                float2 q = (p + current * 0.015) * 5.0;
                float2 cell = floor(q);
                float h = ChapterHash(cell + 1.8);
                float2 center = 0.24 + 0.52 * float2(ChapterHash(cell + 4.2), ChapterHash(cell + 6.5));
                float age = frac(time * 0.25 + h);
                float grow = smoothstep(0.02, 0.7, age);
                float alive = (1.0 - smoothstep(0.78, 0.98, age)) * step(h, _Particles * 0.8);
                float radius = length(frac(q) - center);
                float size = 0.045 + grow * _BubbleSize;
                float bubbleRim = exp(-abs(radius - size) * 80.0) * alive;
                float bulb = (1.0 - smoothstep(0.0, size, radius)) * alive;
                // 只给褶皱和鼓泡局部加阴影，不用全屏黑色遮罩压暗原图。
                float3 color = art * (1.0 - ((1.0 - folds) * 0.4 + bulb) * _MudDepth * amount * mud);
                float3 film = lerp(_Accent.rgb, _Secondary.rgb, sin(p.x * 4.0 + time * 0.4) * 0.5 + 0.5);
                color += (film * (rim * 0.14 + slick * 0.09) + _Accent.rgb * bubbleRim * 0.26) * _Glow * amount * mud;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
