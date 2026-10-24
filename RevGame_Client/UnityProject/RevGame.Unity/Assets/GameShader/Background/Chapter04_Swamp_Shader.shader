// 死亡沼泽：借用湖泊的双相流动采样，以缓慢回旋、厚泥褶皱、鼓泡和腐败毒瘴表达高黏度。
// 液面高光随坡度变化，毒气从鼓泡位置升起；单Pass、两次原画采样，不增加粒子对象、RT或运行时材质。
Shader "RevGame/Background/Chapter04_Swamp"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第四章 · 死亡沼泽原画", 2D) = "white" {}
        _ArtUVRect ("原画有效UV（去透明留边）", Vector) = (0,0.007751938,1,0.99224806)
        _Strength ("死亡沼泽总强度", Range(0,1)) = 0.95
        _Speed ("厚泥回流与毒气速度", Range(0,2)) = 0.72
        _Distortion ("液面褶皱折射（UV）", Range(0,0.04)) = 0.018
        _FlowDistance ("黏稠回流幅度（UV）", Range(0,0.15)) = 0.085
        _Glow ("湿润油膜反光", Range(0,1.5)) = 1.05
        _Particles ("腐泡与排气口密度", Range(0,1)) = 0.65
        _Viscosity ("厚泥褶皱强度", Range(0.3,2)) = 1.65
        _BubbleSize ("腐泡最大半径（格内）", Range(0.06,0.3)) = 0.24
        _BubbleStrength ("鼓泡与破裂强度", Range(0,2)) = 1.2
        _MudDepth ("褶皱与泡坑深度", Range(0,0.6)) = 0.32
        _GasStrength ("腐烂毒瘴浓度", Range(0,2)) = 1.25
        _GasHeight ("毒气升腾高度（画面比例）", Range(0.02,0.3)) = 0.18
        _GasColor ("黄绿腐败毒气", Color) = (0.52,0.65,0.16,1)
        _WaterRegion ("泥面区域 xy起点 zw尺寸", Vector) = (0,0,1,1)
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("暗金湿泥反光", Color) = (0.78,0.66,0.28,1)
        _Secondary ("腐绿油膜", Color) = (0.32,0.48,0.10,1)
        _BoardProtection ("棋盘可读性保护", Range(0,1)) = 0.72
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
            float _Viscosity, _BubbleSize, _BubbleStrength, _MudDepth, _FlowDistance, _GasStrength, _GasHeight;
            float4 _WaterRegion, _ArtUVRect, _GasColor;

            // 衰减让漩涡只拖拽附近泥水；反向回旋避免整张原画匀速平移。
            float2 SwampVortex(float2 p, float2 center, float spin)
            {
                float2 d = p - center;
                return float2(-d.y, d.x) * exp(-dot(d, d) * 5.5) * spin;
            }

            float2 SwampCurrent(float2 p, float time)
            {
                float2 q = p * 3.0;
                q += float2(sin(q.y * 1.7 + time * 0.28), cos(q.x * 1.4 - time * 0.21)) * 0.65;
                float2 drift = float2(sin(q.y + time * 0.23), cos(q.x - time * 0.17)) * 0.28;
                return drift + SwampVortex(p, float2(-0.48, 0.16), 2.8) + SwampVortex(p, float2(0.48, -0.20), -2.5);
            }

            // 排气口使用连续画面坐标而非取整格子，雾扩散时不会在格边被切出方形接缝。
            // 固定种子错峰循环：鼓起→破裂→毒气上升→消散；返回泡膜/凹坑/毒气/余波。
            float4 SwampVent(float2 p, float2 center, float time, float seed, float curl)
            {
                float enabled = step(seed, _Particles);
                float age = frac(time * 0.14 + seed);
                float grow = smoothstep(0.0, 0.62, age);
                float alive = smoothstep(0.0, 0.10, age) * (1.0 - smoothstep(0.60, 0.70, age));
                float2 d = (p - center) * float2(1.0, 1.45);
                float radius = max(length(d), 0.001);
                float size = 0.009 + grow * _BubbleSize * 0.20;
                float edge = exp(-abs(radius - size) * 290.0);
                float lighting = saturate(dot(d / radius, float2(-0.6, 0.8)) * 0.5 + 0.5);
                float cap = exp(-dot(d - float2(-size * 0.32, size * 0.36), d - float2(-size * 0.32, size * 0.36)) / max(size * size * 0.12, 0.00001));
                float rim = (edge * (0.08 + lighting * 0.65) + cap * 0.32) * alive;
                float crater = (1.0 - smoothstep(size * 0.4, size, radius)) * alive;
                float burstAge = saturate((age - 0.63) / 0.37);
                float burst = exp(-abs(radius - size - burstAge * 0.085) * 230.0);
                burst *= smoothstep(0.62, 0.70, age) * (1.0 - smoothstep(0.74, 1.0, age));
                // 气柱向上走并逐渐变宽，噪声卷出边缘；不使用整齐的椭圆贴片。
                float gasAge = saturate((age - 0.48) / 0.52);
                float gasLife = smoothstep(0.48, 0.65, age) * (1.0 - smoothstep(0.82, 1.0, age));
                float2 plume = p - center - float2(sin(time * 0.6 + seed * 20.0) * gasAge * 0.04, gasAge * _GasHeight);
                plume.x += (curl - 0.5) * 0.06;
                float2 spread = plume / float2(0.025 + gasAge * 0.08, 0.045 + gasAge * 0.12);
                float gas = exp(-dot(spread, spread) * 2.0) * gasLife * (0.55 + curl * 0.65);
                return float4(rim, crater, gas, burst) * enabled;
            }

            fixed4 SwampFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = ChapterPlane(uv);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float2 waterEdge = min(uv - _WaterRegion.xy, _WaterRegion.xy + _WaterRegion.zw - uv);
                float mud = smoothstep(0.0, 0.035, min(waterEdge.x, waterEdge.y));
                float effect = amount * mud;
                float2 current = SwampCurrent(p, time);
                // 双相采样交接权重，不积累UV，也不会周期性跳回原位。
                float phase = frac(time * 0.085);
                float phaseB = frac(phase + 0.5);
                float weight = 1.0 - abs(phase * 2.0 - 1.0);
                float2 folded = p * float2(12.0, 18.0) + current * (3.0 * _Viscosity);
                float foldA = folded.x + sin(folded.y * 0.6 - time * 0.35) * 2.2;
                float foldB = folded.y * 0.7 + sin(folded.x * 0.55 + time * 0.23) * 1.8;
                float folds = sin(foldA) * 0.65 + sin(foldB) * 0.35;
                float2 slope = float2(cos(foldA), cos(foldB)) * _Viscosity * 0.32;
                float2 refract = slope * _Distortion;
                float3 artA = ChapterArtRect(uv, (current * ((phase - 0.5) * _FlowDistance) + refract) * mud, amount, _ArtUVRect);
                float3 artB = ChapterArtRect(uv, (current * ((phaseB - 0.5) * _FlowDistance) + refract) * mud, amount, _ArtUVRect);
                float3 art = lerp(artB, artA, weight);
                // 厚泥用宽钝反光与深褶皱，区别于清澈湖水的银白高光与蓝色焦散。
                float3 normal = normalize(float3(-slope, 1.0));
                float specular = pow(saturate(dot(normal, normalize(float3(-0.35, 0.5, 1.0)))), 24.0);
                float ridge = pow(saturate(folds * 0.5 + 0.5), 7.0);
                float oily = ChapterNoise(p * 4.2 + current * 0.9 - time * float2(0.08, 0.03));
                float3 film = lerp(_Accent.rgb, _Secondary.rgb, oily);
                float3 color = lerp(art, art * float3(0.68, 0.82, 0.47), effect * (0.2 + oily * 0.30) * _MudDepth);
                color *= 1.0 - saturate(-folds * 0.7 + 0.2) * _MudDepth * effect;
                color += film * (specular * 0.30 + ridge * 0.17) * _Glow * effect;
                // 固定九处排气口，无动态数组/循环搜索；连续噪声在所有气柱间共享。
                float curl = ChapterNoise(p * float2(18.0, 22.0) + float2(sin(p.y * 10.0) * 0.5, -time * 0.65));
                float2 ventUV = p + current * 0.006;
                float4 vent = SwampVent(ventUV, float2(-0.66, 0.25), time, 0.08, curl)
                    + SwampVent(ventUV, float2(0.66, 0.24), time, 0.23, curl)
                    + SwampVent(ventUV, float2(-0.68, -0.25), time, 0.39, curl)
                    + SwampVent(ventUV, float2(0.64, -0.28), time, 0.52, curl)
                    + SwampVent(ventUV, float2(-0.34, 0.01), time, 0.17, curl)
                    + SwampVent(ventUV, float2(0.38, 0.02), time, 0.61, curl)
                    + SwampVent(ventUV, float2(-0.23, -0.36), time, 0.32, curl)
                    + SwampVent(ventUV, float2(0.23, 0.35), time, 0.46, curl)
                    + SwampVent(ventUV, float2(0.10, -0.20), time, 0.04, curl);
                color *= 1.0 - vent.y * _MudDepth * _BubbleStrength * effect;
                color += (_Accent.rgb * vent.x * 0.50 + _Secondary.rgb * vent.w * 0.24) * _BubbleStrength * _Glow * effect;
                // 低伏毒雾保留透明间隙；局部排气更亮，形成前后层次。
                float2 fogUV = p * float2(3.0, 5.0) + float2(sin(p.y * 4.0 + time * 0.2) * 0.6, -time * 0.24);
                float fog = ChapterNoise(fogUV) * 0.65 + ChapterNoise(fogUV * 2.1 + time * 0.11) * 0.35;
                float banks = smoothstep(0.42, 0.78, fog);
                float gas = saturate((banks * 0.21 + vent.z * 0.85) * _GasStrength * effect);
                float3 poison = _GasColor.rgb * (0.42 + fog * 0.48);
                color = lerp(color, poison, min(gas, 0.68));
                color += _GasColor.rgb * vent.z * _GasStrength * effect * 0.12;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
