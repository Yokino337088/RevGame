// 神圣神殿：穹顶降临光束、对称金色圣印、外扩光环与上升光尘。
// 建筑原位采样，光束为2D解析模拟；单Pass、一次原画采样，不增加Bloom、GrabPass或运行时RT。
Shader "RevGame/Background/Chapter06_Temple"
{
    Properties
    {
        [PerRendererData] _MainTex ("精灵贴图", 2D) = "white" {}
        _ChapterTex ("第六章 · 神圣神殿原画", 2D) = "white" {}
        _Strength ("神圣氛围总强度", Range(0,1)) = 0.95
        _Speed ("圣光与光尘速度", Range(0,2)) = 0.65
        _Distortion ("光环微扰（建筑不扭曲）", Range(0,0.01)) = 0.0015
        _Glow ("象牙金圣光亮度", Range(0,1.5)) = 1.15
        _Particles ("上升光尘密度", Range(0,1)) = 0.6
        _GodRays ("穹顶降临光束", Range(0,2)) = 1.35
        _RuneStrength ("庄严圣印与符文", Range(0,2)) = 1.1
        _HaloStrength ("圣域外扩光环", Range(0,2)) = 0.9
        _DustStrength ("悬浮光尘亮度", Range(0,2)) = 1.0
        _Focus ("原画法阵中心 xy", Vector) = (0.5,0.50,0,0)
        _LightOrigin ("穹顶光源 xy", Vector) = (0.5,1.18,0,0)
        _RingRadius ("圣印半径（画面高度）", Range(0.1,0.6)) = 0.29
        _RuneColor ("圣印金色", Color) = (1,0.76,0.28,1)
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("象牙金光辉", Color) = (1,0.80,0.32,1)
        _Secondary ("柔白圣光", Color) = (1,0.97,0.82,1)
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
            #pragma fragment TempleFrag
            #include "ChapterBackgroundCommon.cginc"
            float4 _Focus, _RuneColor, _LightOrigin;
            float _RingRadius, _GodRays, _RuneStrength, _HaloStrength, _DustStrength;

            // 宽度单位为画面高度；像素导数保证细金线在不同分辨率下不易断裂闪烁。
            float TempleLine(float distance, float width)
            {
                return 1.0 - smoothstep(width, width + max(fwidth(distance), 0.0005), abs(distance));
            }

            // 三角形边界的极坐标表示，每120度重复，跨atan2接缝连续。
            float TempleTriangle(float radius, float angle, float rotation)
            {
                float sector = fmod(angle - rotation + 12.566371, 2.094395) - 1.047198;
                float edge = _RingRadius * 0.40 / max(cos(sector), 0.48);
                return TempleLine(radius - edge, 0.0011);
            }

            fixed4 TempleFrag(ChapterFragment input) : SV_Target
            {
                float2 uv = input.uv;
                float2 p = (uv - _Focus.xy) * float2(_Aspect, 1.05);
                float radius = max(length(p), 0.0001);
                float angle = atan2(p.y, p.x);
                float time = ChapterClock();
                float amount = ChapterAmount(input);
                float3 art = ChapterArt(uv, float2(0, 0), amount);
                float breathe = 0.88 + 0.12 * sin(time * 0.55);
                // 五束宽窄不同的天光从同一穹顶展开，仅轻微摆动明暗，避免快速闪烁。
                float depth = max(_LightOrigin.y - uv.y, 0.08);
                float fan = (uv.x - _LightOrigin.x) * _Aspect / depth;
                float sway = sin(time * 0.22) * 0.014;
                float shafts = exp(-pow((fan - sway) / 0.10, 2.0)) * 0.85;
                shafts += exp(-pow((fan - 0.43 - sway) / 0.072, 2.0)) * 0.72;
                shafts += exp(-pow((fan + 0.43 - sway) / 0.072, 2.0)) * 0.72;
                shafts += exp(-pow((fan - 0.82) / 0.10, 2.0)) * 0.45;
                shafts += exp(-pow((fan + 0.82) / 0.10, 2.0)) * 0.45;
                float filament = pow(saturate(cos(fan * 88.0 + sin(time * 0.25 + uv.y * 5.0) * 0.6)), 10.0);
                float atmosphere = 0.72 + 0.28 * ChapterNoise(float2(fan * 8.0, uv.y * 5.0 - time * 0.18));
                float vertical = lerp(0.2, 1.0, smoothstep(0.05, 0.95, uv.y));
                float rays = shafts * atmosphere * vertical * (0.75 + filament * 0.25);
                float2 crownDelta = float2((uv.x - _LightOrigin.x) * 1.4, uv.y - 1.02);
                float crown = exp(-dot(crownDelta, crownDelta) * 18.0);
                // 双环、对称六芒圣印和分段符文组成稳定祭坛；慢旋只发生在外圈符文。
                float r = _RingRadius;
                float ring = TempleLine(radius - r, 0.0018) + TempleLine(radius - r * 1.13, 0.0010);
                float innerRing = TempleLine(radius - r * 0.80, 0.0010);
                float halo = exp(-abs(radius - r) * 55.0) * 0.28;
                float symbol = TempleTriangle(radius, angle, 0.0) + TempleTriangle(radius, angle, 3.141593);
                float runeAngle = angle + time * 0.035;
                float runeX = frac(runeAngle * (24.0 / 6.283185) + 0.5) - 0.5;
                float runeY = (radius - r * 1.065) / max(r * 0.05, 0.001);
                float runeStem = TempleLine(runeX, 0.045) * (1.0 - smoothstep(0.65, 0.90, abs(runeY)));
                float runeArms = TempleLine(abs(runeX) - (runeY * 0.16 + 0.13), 0.04);
                runeArms *= 1.0 - smoothstep(0.36, 0.70, abs(runeY));
                float runes = (runeStem + runeArms) * (1.0 - smoothstep(0.8, 1.0, abs(runeY)));
                float consecration = (ring + innerRing * 0.65 + symbol * 0.65 + runes * 0.75) * 0.50 + halo;
                // 光环向外扩散且首尾消隐，中心保留柔光池，不使用催眠螺旋。
                float haloAge = frac(time * 0.11);
                float haloRadius = r * 0.82 + haloAge * r * 1.8;
                float haloLife = sin(haloAge * 3.141593);
                float pulse = exp(-abs(radius - haloRadius - sin(angle * 6.0) * _Distortion) * 90.0) * haloLife;
                float sanctuary = exp(-dot(p, p) / max(r * r * 1.8, 0.01));
                float2 dustUV = ChapterPlane(uv) * float2(21.0, 14.0) + float2(sin(time * 0.24) * 0.35, -time * 0.48);
                float2 cell = floor(dustUV);
                float seed = ChapterHash(cell);
                float2 center = 0.2 + 0.6 * float2(ChapterHash(cell + 2), ChapterHash(cell + 12));
                float2 d = frac(dustUV) - center;
                float core = exp(-dot(d, d) * 550.0);
                float dustHalo = exp(-dot(d, d) * 70.0) * 0.13;
                float sparkle = exp(-abs(d.x) * 120.0 - abs(d.y) * 18.0) + exp(-abs(d.y) * 120.0 - abs(d.x) * 18.0);
                float life = 0.5 + 0.5 * sin(time * 0.8 + seed * 18.0);
                float dust = (core + dustHalo + sparkle * 0.22) * life * step(seed, _Particles * 0.65);
                // 局部柔光的亮部逐渐收敛，避免加到全白；外围略收光，使中心象牙金更突出。
                float3 color = art * (1.0 - amount * (1.0 - sanctuary) * 0.09);
                float3 illumination = (_Accent.rgb * rays * 0.58 + _Secondary.rgb * crown * 0.30) * _GodRays;
                illumination += _RuneColor.rgb * consecration * _RuneStrength * breathe;
                illumination += (_Accent.rgb * pulse * 0.26 + _Secondary.rgb * sanctuary * 0.15) * _HaloStrength * breathe;
                illumination += _Secondary.rgb * dust * _DustStrength * 0.85;
                color = 1.0 - (1.0 - color) * exp(-illumination * _Glow * amount);
                return ChapterOutput(color, input, amount);
            }
            ENDCG
        }
    }
    Fallback Off
}