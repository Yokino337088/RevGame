// ============================================================
// 第三章 · 湖上对弈：连续水流 + 多方向波面法线 + 扩散涟漪 + 随波破碎的天空/日光反射。
// 为什么重建波面：只平移图片或画亮线看起来是“滤镜”，水面的明暗/反光必须随局部斜率变化。
// 本项目是俯视2D原画，不提供真实3D深度/反射相机；这里模拟光学表现，不冒充屏幕空间反射或流体求解。
// 两次原画采样、固定四组波、三处环形涟漪；无GrabPass、无新增RT、无逐帧CPU网格/材质分配。
// ============================================================
Shader "RevGame/Background/Chapter03_Lake"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第三章 · 魔法湖泊原画", 2D) = "white" {}
        _ArtUVRect ("原画有效UV（去透明留边）", Vector) = (0,0.007751938,1,0.99224806)
        _Strength ("水面效果总强度", Range(0,1)) = 0.85
        _Speed ("流水与波纹速度", Range(0,2)) = 1.05
        _Distortion ("波面折射幅度（UV）", Range(0,0.04)) = 0.012
        _WaveHeight ("水波起伏与法线强度", Range(0,1.5)) = 0.9
        _FlowDistance ("持续流动幅度（UV）", Range(0,0.15)) = 0.055
        _RippleStrength ("扩散涟漪强度", Range(0,2)) = 1.1
        _SpecularStrength ("日光镜面高光", Range(0,2)) = 1.25
        _SpecularPower ("高光锐度", Range(24,180)) = 100
        _Caustics ("水下流动焦散", Range(0,1.5)) = 0.8
        _Glow ("波光与焦散亮度", Range(0,1.5)) = 1.1
        _Particles ("细碎闪光密度", Range(0,1)) = 0.35
        _ReflectionStrength ("天空环境反射", Range(0,0.65)) = 0.4
        _ReflectionStretch ("波光横向拉伸", Range(0.2,2)) = 0.85
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
            // 纯美术表现参数：不影响棋子规则/数值，保存在材质Inspector，不进入策划战斗Excel。
            float _WaveHeight, _FlowDistance, _RippleStrength, _SpecularStrength, _SpecularPower, _Caustics;
            float4 _FlowDirection, _WaterRegion, _ArtUVRect;

            // 累加一组方向波的“坡度”。正弦表示高度，cos×频率是其导数，因此斜率与折射/高光来自同一水波。
            // direction是单位方向；frequency是每画面单位的弧度数；amplitude是相对画面高度；speed是弧度/秒。
            void LakeWave(float2 p, float2 direction, float frequency, float amplitude, float speed, float time, inout float2 gradient)
            {
                float phase = dot(p, direction) * frequency - time * speed;
                gradient += direction * (cos(phase) * frequency * amplitude);
            }

            // 单个水滴的波包向外传播，而非整片同心圆同时闪烁。
            // age为0~7.14秒的周期，首尾振幅归零，重生时没有突然跳出的圆圈。
            // 返回xy=涟漪坡度，z=可见波峰，w=波谷；坡度驱动折射和高光，明暗细线让扩散形状更易识别。
            float4 LakeRipple(float2 p, float2 center, float time, float phaseOffset)
            {
                float age = frac(time * 0.14 + phaseOffset) / 0.14;
                float2 delta = (p - center) * float2(1.0, 1.25);
                float distance = max(length(delta), 0.001);
                float front = distance - age * 0.105;
                float life = smoothstep(0.0, 0.6, age) * (1.0 - smoothstep(5.0, 7.14, age));
                float envelope = exp(-front * front * 130.0) * life;
                float phase = front * 72.0;
                float wave = sin(phase) * envelope;
                float derivative = (72.0 * cos(phase) - 260.0 * front * sin(phase)) * envelope;
                float2 gradient = derivative * (delta / distance) * float2(1.0, 1.25) * 0.005;
                return float4(gradient, saturate(wave), saturate(-wave));
            }

            fixed4 LakeFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = ChapterPlane(uv);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float2 distanceToWaterEdge = min(uv - _WaterRegion.xy, _WaterRegion.xy + _WaterRegion.zw - uv);
                float water = smoothstep(0.0, 0.045, min(distanceToWaterEdge.x, distanceToWaterEdge.y));
                float effect = amount * water;

                // ① 远处长波、近处碎波交叉叠加。所有常量是表现用空间频率，不是玩法数值。
                float2 slope = 0;
                LakeWave(p, float2(0.94, 0.342), 13.0, 0.018, 1.10, time, slope);
                LakeWave(p, float2(-0.53, 0.848), 23.0, 0.009, 1.65, time, slope);
                LakeWave(p, float2(0.29, 0.957), 47.0, 0.0035, 2.1, time, slope);
                LakeWave(p, float2(-0.81, 0.586), 89.0, 0.0016, 2.7, time, slope);
                slope *= _WaveHeight;
                float4 ripple = LakeRipple(p, float2(-0.56, 0.23), time, 0.08)
                    + LakeRipple(p, float2(0.56, -0.23), time, 0.42)
                    + LakeRipple(p, float2(0.25, 0.34), time, 0.74);
                slope += ripple.xy * _RippleStrength;
                float3 normal = normalize(float3(-slope.x, -slope.y, 1.0));

                // ② 两套错开半周期的流动UV交叉淡入淡出；一套重置时另一套正好完全可见。
                // 比UV无限累加更适合整张原画：不会越来越偏、卡在边缘，也不会周期性瞬移。
                float phase = frac(time * 0.12);
                float phaseB = frac(phase + 0.5);
                float weightA = 1.0 - abs(phase * 2.0 - 1.0);
                float2 flowDirection = _FlowDirection.xy / max(length(_FlowDirection.xy), 1.0);
                float2 offsetA = flowDirection * ((phase - 0.5) * _FlowDistance);
                float2 offsetB = flowDirection * ((phaseB - 0.5) * _FlowDistance);
                float2 refractUV = normal.xy * _Distortion;
                float3 artA = ChapterArtRect(uv, (offsetA + refractUV) * water, amount, _ArtUVRect);
                float3 artB = ChapterArtRect(uv, (offsetB + refractUV) * water, amount, _ArtUVRect);
                float3 art = lerp(artB, artA, weightA);

                // ③ 用波面法线与半程向量计算镜面高光；高光会跟着波峰变亮/碎开，不是固定在屏幕上的白色横线。
                // 视线略倾斜形成湖面纵深；低角度增加环境反射（Fresnel），正俯视仍能看见水下原画。
                float3 view = normalize(float3(0, -0.38, 1));
                float3 light = normalize(float3(-0.32, 0.44, 1));
                float3 halfVector = normalize(view + light);
                float ndh = saturate(dot(normal, halfVector));
                float specular = pow(ndh, _SpecularPower);
                float softSpecular = pow(ndh, 16.0);
                // 高光限定为一片柔和反光带，并用细波打散，避免整个水面变成白色塑料。
                float sunLane = exp(-pow((p.x - 0.10 + p.y * 0.22) / max(_ReflectionStretch * 0.52, 0.1), 2.0));
                float fresnel = 0.035 + 0.965 * pow(1.0 - saturate(dot(normal, view)), 5.0);
                float sky = saturate(0.35 + normal.y * 0.9 + normal.x * 0.2);
                float3 skyColor = lerp(float3(0.10, 0.30, 0.46), float3(0.70, 0.92, 0.98), sky);
                float reflectAmount = _ReflectionStrength * (0.18 + fresnel * 0.8 + softSpecular * 0.15) * effect;
                float3 color = lerp(art, skyColor, reflectAmount);
                color *= 1.0 - effect * saturate(slope.y + 0.15) * 0.1;
                color += _Accent.rgb * (specular * 0.68 + softSpecular * 0.055) * sunLane * _SpecularStrength * effect;

                // ④ 焦散是移动的水下亮网，与表面高光分开；两层相位交错，避免规整静止的网格感。
                float2 q = p * float2(15.0, 24.0) - flowDirection * time * 0.55;
                float cells = sin(q.x + sin(q.y * 0.8 + time * 0.3) * 1.7)
                    + sin(q.y * 1.1 + cos(q.x * 0.65 - time * 0.4) * 1.6);
                float caustic = pow(saturate(1.0 - abs(cells) * 1.5), 5.0);
                color += _Secondary.rgb * caustic * _Caustics * _Glow * effect * 0.16;
                color += _Accent.rgb * ripple.z * _RippleStrength * _Glow * effect * 0.075;
                color *= 1.0 - ripple.w * _RippleStrength * effect * 0.06;
                color += _Accent.rgb * pow(saturate(caustic * specular), 2.0) * sunLane * _Particles * effect * 0.25;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
