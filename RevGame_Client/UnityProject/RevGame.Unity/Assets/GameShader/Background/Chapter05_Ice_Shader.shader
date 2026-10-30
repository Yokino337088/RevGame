// ============================================================
// 第五章 · 极地封冻：保留原画，在其上叠加厚冰折射、冰晶裂隙、失温寒潮与分层风雪。
// 为什么不堆48层视差采样：这是手机全屏背景，每个像素都要执行；用一次冰胞查询和两次原画采样模拟厚度。
// 所有动画只读ChapterClock，所有视觉变化受ChapterAmount约束；不新增CPU Update、粒子对象或材质实例。
// ============================================================
Shader "RevGame/Background/Chapter05_Ice"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第五章 · 冰封极地原画", 2D) = "white" {}
        _ArtUVRect ("原画有效UV（去透明留边）", Vector) = (0,0.007751938,1,0.99224806)
        _Strength ("冰雪总强度", Range(0,1)) = 0.85
        _Speed ("冰下流动与风雪速度", Range(0,2)) = 0.8
        _Distortion ("冰下折射幅度（UV）", Range(0,0.01)) = 0.0055
        _IceField ("预计算静态冰胞（线性数据）", 2D) = "black" {}
        [HideInInspector] _UseIceField ("冰胞纹理已绑定", Float) = 0
        _IceFlow ("冰下微流动强度", Range(0,2)) = 1.15
        _IceDepth ("冰层厚度与内层视差", Range(0,1)) = 0.75
        _IceCracks ("蓝色冰晶裂隙强度", Range(0,1.5)) = 1.05
        _IceScale ("冰晶分块密度", Range(3,12)) = 6.5
        _IceCoverage ("封冻冰壳覆盖", Range(0,1)) = 0.72
        _ColdBreath ("失温寒潮与流雾", Range(0,1.5)) = 1.05
        _Glow ("冰裂反光与雪亮度", Range(0,1.5)) = 1.05
        _Particles ("飘雪密度", Range(0,1)) = 0.7
        _SnowWind ("雪花横向风速", Range(-1,1)) = 0.35
        _Frost ("边缘冰霜厚度", Range(0,1)) = 0.9
        _ColdTone ("极地深蓝冷色", Range(0,1)) = 0.9
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
            // 全部为美术表现参数，单位/范围见Properties；不影响攻防、AI或战斗时间，不放策划数值表。
            float _IceFlow, _IceDepth, _IceCracks, _IceScale, _IceCoverage, _ColdBreath;
            float4 _ArtUVRect;
            // 预计算冰胞数据RGBA：R=裂隙距离，G/B=编码斜面，A=固定种子。512²像素并非冰原原画副本。
            sampler2D _IceField;
            // 构建成功绑定数据场才设1；旧材质或未绑定时0走IceCell原计算，不把默认黑贴图当有效几何。
            float _UseIceField;

            // 返回x=冰胞边界距离，yz=冰胞斜面方向，w=该块固定种子。
            // 最近/次近的随机中心像两块相邻冰晶；距离相近处就是接缝，距离差越小越靠近裂隙。
            // 只查周围3×3格，固定上限且不采纹理；冰胞中心不随时间改变，避免整片冰块像水一样漂走。
            float4 IceCell(float2 p)
            {
                float2 cell = floor(p);
                float2 local = frac(p);
                float nearest = 9.0;
                float second = 9.0;
                float2 slope = 0;
                float seed = 0;
                [unroll] for (int y = -1; y <= 1; y++)
                {
                    [unroll] for (int x = -1; x <= 1; x++)
                    {
                        float2 neighbor = float2(x, y);
                        float2 id = cell + neighbor;
                        float2 jitter = float2(ChapterHash(id + 3.1), ChapterHash(id + 17.7));
                        float2 offset = neighbor + 0.15 + jitter * 0.7 - local;
                        float distanceSquared = dot(offset, offset);
                        if (distanceSquared < nearest)
                        {
                            second = nearest;
                            nearest = distanceSquared;
                            slope = offset;
                            seed = jitter.x;
                        }
                        else { second = min(second, distanceSquared); }
                    }
                }
                return float4(max(0, sqrt(second) - sqrt(nearest)), slope, seed);
            }

            // 单层雪：格子种子固定、时间只平移坐标，所以不会每帧随机闪烁。三次调用模拟远/中/近景。
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

                // ① 固定表层，流动内层。低频两轴偏移让原画像隔着厚冰缓慢呼吸；幅度是UV比例，不是世界米数。
                // ChapterArt会在屏幕边缘把偏移收敛到0并限制采样范围，横屏顶满时不会拉出黑边/留白。
                float grain = ChapterNoise(p * 9.0);
                float fog = ChapterNoise(p * float2(3.0, 5.5) + float2(-time * 0.12, time * 0.055));
                float2 flow = float2(sin(p.y * 5.2 + time * 0.65) + sin(p.x * 3.7 - time * 0.41) * 0.45,
                    cos(p.x * 4.3 + time * 0.48) + sin(p.y * 3.3 - time * 0.37) * 0.4);
                // 固定冰胞不随时间变化，每像素九次中心查询属于重复工作。
                // 场纹理由项目编辑器按原算法预算；范围内一次采样，超范围/旧AB缺贴图仍使用原算法。
                float2 icePosition = p * _IceScale + grain * 0.22;
                float4 ice;
                // 线性数据场只覆盖[-12,12]，11.95内留双线性边界余量。范围内一次采样替代每像素九邻中心计算。
                // 范围外仍原算法，雪/寒潮/原画流动继续按时间更新；省的是静态几何重复数学。
                [branch] if (_UseIceField > 0.5 && max(abs(icePosition.x), abs(icePosition.y)) < 11.95)
                {
                    ice = tex2D(_IceField, (icePosition + 12.0) / 24.0);
                    // PNG只能存0～1，生成器把负方向编码成0.5附近；这里还原到-1～1。
                    // 8位量化和双线性采样是精度/性能权衡，不是与原函数逐像素完全相同。
                    ice.yz = (ice.yz - 0.5) * 2.0;
                }
                else { ice = IceCell(icePosition); }
                float2 warp = flow * _Distortion * _IceFlow;
                float2 refraction = ice.yz * (_IceDepth * 0.0045);
                float3 art = ChapterArtRect(uv, warp + refraction, amount, _ArtUVRect);
                // 第二层与表面产生不同方向/距离的位移，模拟冰下重影和折射深度；并非真正的体积/48层视差。
                float3 underIce = ChapterArtRect(uv, warp * 0.4 - refraction * 1.7, amount, _ArtUVRect);
                art = lerp(art, underIce, amount * _IceDepth * 0.12);

                // ② 冰胞边界 = 暗缝 + 青蓝内芯 + 外侧光晕。fwidth保留至少一个像素的过渡，缩放时不会锐利闪烁。
                float aa = max(fwidth(ice.x), 0.002);
                float seam = 1.0 - smoothstep(0.012, 0.045 + aa, ice.x);
                float rim = 1.0 - smoothstep(0.035, 0.12 + aa, ice.x);
                float core = 1.0 - smoothstep(0.002, 0.014 + aa, ice.x);
                float crystalShade = saturate(dot(ice.yz, float2(-0.65, 0.76)) + 0.45);
                float frostEdge = 1.0 - smoothstep(0.015, 0.19 + grain * 0.12, edgeDistance);
                float frost = frostEdge * _Frost;
                // 寒潮使用宽缓的波带，持续移动而非突然闪白；表面仍保留原画手绘轮廓。
                float breath = 0.5 + 0.5 * sin(time * 0.72 - p.x * 3.1 + p.y * 2.2 + fog * 2.0);
                float coldWave = smoothstep(0.4, 0.95, breath) * _ColdBreath;
                float sweep = pow(saturate(0.5 + 0.5 * sin(p.x * 2.8 + p.y * 1.5 - time * 0.55)), 6.0);
                float originalCracks = smoothstep(0.62, 0.94, min(art.r, min(art.g, art.b)));

                // ③ 先压低暖色并加深冰层，再叠加冷色反光；不要靠全屏加白制造“冰封”，否则雪地会过曝。
                float3 color = lerp(art, art * float3(0.62, 0.86, 1.08), amount * _ColdTone);
                float shell = _IceCoverage * amount;
                color *= 1.0 - shell * (0.07 + crystalShade * 0.10);
                color = lerp(color, color * float3(0.77, 0.94, 1.04) + _Secondary.rgb * 0.055, shell);
                color *= 1.0 - seam * _IceCracks * amount * 0.25;
                color += (_Secondary.rgb * rim * 0.12 + _Accent.rgb * core * 0.20) * _IceCracks * amount;
                color += _Secondary.rgb * crystalShade * shell * sweep * 0.09;
                color = lerp(color, _Secondary.rgb * 0.5, amount * coldWave * (0.10 + fog * 0.12));

                // ④ 窗边霜晶沿冰胞接缝蔓延、斜向寒雾扫过原画；棋盘保护在amount中统一生效。
                float frostCrystal = frost * (0.25 + rim * 0.55 + grain * 0.2);
                color = lerp(color, _Accent.rgb * 0.88, frostCrystal * amount * 0.42);
                color += _Secondary.rgb * fog * coldWave * amount * 0.075;
                float farSnow = IceSnow(p, time * 0.55, 36.0, 0.65, 0.055, 2.1);
                float middleSnow = IceSnow(p, time, 23.0, 0.9, 0.068, 7.7);
                float nearSnow = IceSnow(p + 3.7, time, 13.0, 1.15, 0.085, 19.2);
                color += _Accent.rgb * (farSnow * 0.25 + middleSnow * 0.45 + nearSnow * 0.65
                    + sweep * originalCracks * 0.12) * _Glow * amount;
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}
