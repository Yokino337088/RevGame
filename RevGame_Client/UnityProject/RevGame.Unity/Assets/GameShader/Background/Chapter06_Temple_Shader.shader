// 魔法神殿专用：沿原画法阵中心展开慢旋螺纹、呼吸环与符文，营造柔和催眠感。
// 建筑不旋转、不扭动；只在地面法阵内部施加很小的折射，避免整张神殿像液体。
Shader "RevGame/Background/Chapter06_Temple"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第六章 · 魔法神殿原画", 2D) = "white" {}
        _Strength ("神殿魔法总强度", Range(0,1)) = 0.85
        _Speed ("催眠慢旋速度", Range(0,2)) = 0.42
        _Distortion ("法阵局部折射", Range(0,0.01)) = 0.0015
        _Glow ("法阵与符文柔光", Range(0,1.5)) = 0.95
        _Particles ("魔力星尘密度", Range(0,1)) = 0.55
        _Focus ("原画法阵中心 xy", Vector) = (0.5,0.51,0,0)
        _RingRadius ("法阵半径", Range(0.1,0.6)) = 0.3
        _SpiralTurns ("催眠螺纹层次", Range(1,6)) = 3
        _TranceStrength ("迷幻螺纹强度", Range(0,1)) = 0.72
        _RuneColor ("淡紫符文光", Color) = (0.79,0.63,1,1)
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("象牙金魔力", Color) = (1,0.84,0.4,1)
        _Secondary ("柔白圣光", Color) = (1,0.95,0.78,1)
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
            #pragma fragment TempleFrag
            #include "ChapterBackgroundCommon.cginc"
            float4 _Focus, _RuneColor;
            float _RingRadius, _SpiralTurns, _TranceStrength;

            fixed4 TempleFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = (uv - _Focus.xy) * float2(_Aspect, 1.0);
                float radius = length(p);
                float angle = atan2(p.y, p.x);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float floorMask = 1.0 - smoothstep(_RingRadius * 1.2, _RingRadius * 1.85, radius);
                float breathe = 0.72 + 0.28 * sin(time * 0.7);
                float2 tangent = float2(-p.y, p.x) / max(radius, 0.04);
                float2 warp = tangent * sin(radius * 25.0 - time * 0.5) * _Distortion * floorMask;
                float3 art = ChapterArt(uv, warp, amount);
                // 角度乘整数，跨越 atan2 接缝仍连续；频率缓慢，不做刺眼的全屏闪烁。
                float spiral = sin(angle * 3.0 + radius * 22.0 * _SpiralTurns - time * 0.65);
                float bands = pow(saturate(spiral * 0.5 + 0.5), 7.0) * floorMask;
                float ring = exp(-abs(radius - _RingRadius * (1.0 + sin(time * 0.45) * 0.025)) * 100.0);
                float outer = exp(-abs(radius - _RingRadius * 1.25) * 120.0);
                float runes = pow(saturate(sin(angle * 18.0 - time * 0.22) * 0.5 + 0.5), 6.0) * outer;
                float rays = pow(saturate(sin((uv.x + (1.0 - uv.y) * 0.28) * 24.0 + sin(time * 0.2) * 0.35) * 0.5 + 0.5), 12.0);
                rays *= smoothstep(0.12, 0.95, uv.y);
                float2 dustUV = ChapterPlane(uv) * 23.0 + float2(sin(time * 0.35) * 0.15, -time * 0.18);
                float2 cell = floor(dustUV);
                float2 center = 0.2 + 0.6 * float2(ChapterHash(cell + 2), ChapterHash(cell + 12));
                float2 d = frac(dustUV) - center;
                float dust = exp(-dot(d, d) * 420.0) * step(ChapterHash(cell), _Particles * 0.7);
                float3 trance = lerp(_RuneColor.rgb, _Accent.rgb, sin(radius * 17.0 - time * 0.3) * 0.5 + 0.5);
                float3 color = art + (trance * bands * _TranceStrength * 0.13 + _Accent.rgb * (ring * 0.18 + runes * 0.25)
                    + _Secondary.rgb * (rays * 0.16 + dust * 0.55)) * _Glow * amount * breathe;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
