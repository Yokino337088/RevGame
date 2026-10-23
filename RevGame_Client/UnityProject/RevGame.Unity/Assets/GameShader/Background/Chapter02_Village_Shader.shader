// 魔法村落专用：明朗日照、草地迷幻光带与漂浮萤火。房屋边框不参加液态扭曲。
Shader "RevGame/Background/Chapter02_Village"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第二章 · 魔法村落原画", 2D) = "white" {}
        _Strength ("效果总强度（0=原画）", Range(0,1)) = 0.8
        _Speed ("光带与萤火速度", Range(0,2)) = 0.48
        _Distortion ("草地微风扰动", Range(0,0.01)) = 0.002
        _Glow ("魔法柔光", Range(0,1.5)) = 0.8
        _Particles ("萤火密度", Range(0,1)) = 0.6
        _Daylight ("明朗日照", Range(0,0.25)) = 0.1
        _RibbonWidth ("迷幻光带宽度", Range(0.05,0.8)) = 0.4
        _DreamColor ("淡紫迷幻光", Color) = (0.76,0.62,1,1)
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("温暖阳光", Color) = (1,0.86,0.46,1)
        _Secondary ("薄荷魔法光", Color) = (0.46,1,0.75,1)
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
            #pragma fragment VillageFrag
            #include "ChapterBackgroundCommon.cginc"
            float _Daylight, _RibbonWidth;
            float4 _DreamColor;

            float VillageFireflies(float2 p, float time)
            {
                float2 q = p * 17.0 + float2(sin(time * 0.35) * 0.25, -time * 0.2);
                float2 cell = floor(q);
                float h = ChapterHash(cell);
                float2 center = 0.2 + 0.6 * float2(ChapterHash(cell + 7), ChapterHash(cell + 17));
                float2 moteOffset = frac(q) - center;
                moteOffset.x += sin(time + h * 20.0) * 0.12;
                float core = exp(-dot(moteOffset, moteOffset) * 360.0);
                float halo = exp(-dot(moteOffset, moteOffset) * 65.0) * 0.22;
                return (core + halo) * step(h, _Particles * 0.7) * (0.6 + 0.4 * sin(time * 1.2 + h * 25.0));
            }

            fixed4 VillageFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = ChapterPlane(uv);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                // 原图四周是房屋与树冠。仅开阔草地接受轻微呼吸位移，建筑保持稳定。
                float ground = smoothstep(0.12, 0.3, uv.x) * (1.0 - smoothstep(0.7, 0.88, uv.x));
                ground *= smoothstep(0.08, 0.27, uv.y) * (1.0 - smoothstep(0.7, 0.88, uv.y));
                float wind = sin(p.y * 5.0 + time * 0.7) * cos(p.x * 3.0 - time * 0.45);
                float2 warp = float2(wind, sin(p.x * 5.0 + time * 0.45) * 0.35) * _Distortion * ground;
                float3 art = ChapterArt(uv, warp, amount);
                float ribbonPhase = p.y * 9.0 + sin(p.x * 4.0 + time * 0.45) * 1.5 - time * 0.65;
                float ribbons = pow(saturate(1.0 - abs(sin(ribbonPhase)) / max(_RibbonWidth, 0.05)), 2.0);
                float colorPhase = sin(p.x * 2.5 + time * 0.3) * 0.5 + 0.5;
                float3 dream = lerp(_Secondary.rgb, _DreamColor.rgb, colorPhase);
                float rayAxis = uv.x + (1.0 - uv.y) * 0.48 + sin(time * 0.3) * 0.035;
                float rays = pow(saturate(sin(rayAxis * 19.0) * 0.5 + 0.5), 9.0) * smoothstep(0.15, 0.95, uv.y);
                float motes = VillageFireflies(p, time);
                // 日照抬高中间调，保持明朗；柔和彩光叠加在原画上，不把整屏染暗紫。
                float3 color = art + (1.0 - art) * _Daylight * amount;
                color += (dream * ribbons * (0.04 + ground * 0.06) + _Accent.rgb * (rays * 0.13 + motes * 0.55)) * _Glow * amount;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
