// 魔法湖泊专用：双向水流扰动、镜面反射碎光、移动焦散与交错涟漪。
// 原画本身就是俯视水面，反射取原画的柔化镜像；不凭空镜像天空或复制建筑。
Shader "RevGame/Background/Chapter03_Lake"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第三章 · 魔法湖泊原画", 2D) = "white" {}
        _Strength ("水面效果总强度", Range(0,1)) = 0.85
        _Speed ("流水速度", Range(0,2)) = 0.7
        _Distortion ("水流折射幅度", Range(0,0.04)) = 0.009
        _Glow ("波光亮度", Range(0,1.5)) = 0.85
        _Particles ("细碎闪光密度", Range(0,1)) = 0.35
        _ReflectionStrength ("水面镜像反射", Range(0,0.65)) = 0.24
        _ReflectionStretch ("反射横向拉伸", Range(0.2,2)) = 0.8
        _FlowDirection ("水流方向 xy", Vector) = (0.8,0.25,0,0)
        _WaterRegion ("水面区域 xy起点 zw尺寸", Vector) = (0,0,1,1)
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("银蓝反光", Color) = (0.67,0.95,1,1)
        _Secondary ("湖水焦散", Color) = (0.25,0.72,1,1)
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
            #pragma fragment LakeFrag
            #include "ChapterBackgroundCommon.cginc"
            float _ReflectionStrength, _ReflectionStretch;
            float4 _FlowDirection, _WaterRegion;

            float LakeRipple(float2 p, float2 center, float time)
            {
                float radius = length((p - center) * float2(1.0, 1.22));
                return sin(radius * 68.0 - time * 3.3) * exp(-radius * 3.8);
            }

            fixed4 LakeFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = ChapterPlane(uv);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float2 distanceToWaterEdge = min(uv - _WaterRegion.xy, _WaterRegion.xy + _WaterRegion.zw - uv);
                float water = smoothstep(0.0, 0.045, min(distanceToWaterEdge.x, distanceToWaterEdge.y));
                float2 flow = _FlowDirection.xy * time * 0.22;
                float2 q = p * float2(15.0, 24.0) - flow;
                float ripple = LakeRipple(p, float2(-0.58, 0.24), time) + LakeRipple(p, float2(0.56, -0.22), time + 1.6);
                float2 normal = float2(sin(q.y + sin(q.x * 0.5) + time * 0.6), cos(q.x * 0.8 - q.y * 0.4 - time * 0.8));
                normal += ripple * float2(0.45, 0.18);
                float2 warp = normal * _Distortion * water;
                float3 art = ChapterArt(uv, warp, amount);
                // 少量原画镜像在横向碎波里显现，形成真实纹理反射，而非只画一层发光条纹。
                float2 mirrorUV = float2(0.5 + (uv.x - 0.5) * _ReflectionStretch, 1.0 - uv.y);
                mirrorUV += normal * _Distortion * 1.35;
                float3 reflected = tex2Dgrad(_ChapterTex, clamp(mirrorUV, 0.002, 0.998), ddx(uv), ddy(uv)).rgb;
                float glint = pow(saturate(sin(p.y * 115.0 + normal.x * 1.3 - time * 1.8) * 0.5 + 0.5), 16.0);
                glint *= 0.35 + 0.65 * pow(saturate(sin(p.x * 5.0 - time * 0.2) * 0.5 + 0.5), 3.0);
                float cells = sin(q.x * 1.8 + sin(q.y) * 1.7) + sin(q.y * 1.45 + cos(q.x * 0.7) * 1.8 + time);
                float caustic = pow(saturate(1.0 - abs(cells) * 1.8), 4.0);
                float crest = pow(saturate(ripple * 0.55), 5.0);
                float3 color = lerp(art, reflected * float3(0.85, 0.98, 1.06), _ReflectionStrength * (0.25 + glint * 0.75) * amount * water);
                color += (_Accent.rgb * (glint * 0.28 + crest * 0.12) + _Secondary.rgb * caustic * 0.09) * _Glow * amount * water;
                color += _Accent.rgb * pow(saturate(caustic * glint), 2.0) * _Particles * amount * water * 0.45;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
