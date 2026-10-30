// ============================================================
// BossSkill —— Boss 技能特效的共享 Shader（25种表现模式，单 Pass，兼容 SpriteRenderer 与图集）。
//
// 【为什么这些效果共用一个 Shader】
// Boss 每个技能的特效由若干“片”（SpriteRenderer）拼成：预警圈、冲击波、激光、火球……
// 用一个 Shader + 每片自己的 _Mode 参数，所有片共享同一个材质与同一份图集贴图，
// 不需要为每种技能克隆材质，也不会因为切换 Shader 造成大量 SetPass。
// _Mode 是“整片统一”的参数，GPU每片只走一个分支；真实成本仍需测量，不能把单Pass当作无额外GPU开销。
//
// 【模式一览】（C# 侧见 BossSkillFxView.FxMode，数值必须一致）
//   0 预警区域  ：半透明填充 + 流动警示斜纹 + 发亮描边；_Progress 0→1 表示“快要命中了”，越接近 1 闪得越急
//   1 冲击波    ：从中心向外扩散的圆环，扩散越远越细越淡
//   2 激光      ：横向光束，白色内芯 + 彩色外辉 + 沿光束流动的能量噪声
//   3 爆炸火球  ：噪声扰动的火球，白芯→主色→暗红边，随进度膨胀并燃尽
//   4 贴图发光  ：直接显示贴图（子弹、刀光、炸弹、准星、焦痕），带轻微闪烁
//   5 传送门    ：多臂旋涡，_Progress 控制开合（0 闭合 → 0.5 全开 → 1 闭合）
//   6 汲取漩涡  ：向内收缩的螺旋条纹，表现“生命被吸走”
//   7 彩色激光  ：保留主色光芯，避免红/黄激光在浅色棋盘上被白芯冲淡
//   8 暗红魔法球：实心球壳、能量裂纹与流动符文光环，圆图只提供网格
//
// 【坐标】图集里的 UV 不是 0~1：每片的 _FxUV 记录当前 Sprite 在图集中的矩形，
// 先换算回“这张图自己的 0~1 局部坐标”，程序化效果才不会采到隔壁的图。
// 【混合】预乘透明度（与项目其他 Sprite Shader 一致）；_Additive=1 时 alpha 输出 0，表现为纯叠加发光。
// ============================================================
Shader "RevGame/Combat/BossSkill"
{
    Properties
    {
        [PerRendererData] _MainTex ("特效贴图（图集页）", 2D) = "white" {}
        _Color ("整体颜色", Color) = (1,1,1,1)
        [PerRendererData] _Mode ("效果模式 0~24", Float) = 4
        _Intensity ("能量亮度（不依赖Bloom）", Range(0.5,3)) = 1.65
        _Contrast ("暗色轮廓强度（浅色棋盘可读性）", Range(0,1)) = 0.8
        _OutlineWidth ("法纹与冲击环粗度", Range(0.05,0.24)) = 0.13
        [PerRendererData] _Progress ("效果进度 0~1", Range(0,1)) = 0
        [PerRendererData] _Tint ("主色", Color) = (1,0.35,0.25,1)
        [PerRendererData] _Shape ("预警形状 0圆 1方", Float) = 0
        [PerRendererData] _Additive ("叠加程度 0正常 1纯发光", Range(0,1)) = 0.6
        [PerRendererData] _FxUV ("当前Sprite在图集中的UV矩形", Vector) = (0,0,1,1)
        [MaterialToggle] PixelSnap ("Pixel snap", Float) = 0
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("External Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("Enable External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Cull Off
        Lighting Off
        ZWrite Off
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex SpriteVert
            #pragma fragment FragBossSkill
            #pragma multi_compile_instancing
            #pragma multi_compile _ PIXELSNAP_ON
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"

            // Android交叉编译先经DX字节码；half/fixed的条件结果被优化进整数比较时，
            // Unity 2022.3可能生成非法min-precision指令（X8000，GLES3/Vulkan）。
            // 本Shader分支、噪声与颜色计算统一float，显式避免混合精度；不能只凭PC编译判定移动端可用。
            float _Mode;
            float _Progress;
            float4 _Tint;
            float _Shape;
            float _Additive;
            float _Intensity, _Contrast, _OutlineWidth;
            float4 _FxUV;

            // ---------------- 噪声工具（不依赖额外贴图，不增加采样） ----------------
            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            float Noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                float a = Hash(i);
                float b = Hash(i + float2(1, 0));
                float c = Hash(i + float2(0, 1));
                float d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 两层叠加的分形噪声：低频决定大形状，高频补细节。只叠两层，手机上足够便宜。
            float Fbm(float2 p)
            {
                return Noise(p) * 0.65 + Noise(p * 2.7 + 13.1) * 0.35;
            }

            // ---------------- 0 预警区域 ----------------
            // 内部：低透明度填充 + 斜向流动警示纹；边缘：亮描边；进度越高，脉冲越快越亮。
            float4 Telegraph(float2 p, float t)
            {
                float dist = _Shape > 0.5 ? max(abs(p.x), abs(p.y)) : length(p);
                float inside = step(dist, 1.0);
                float edge = smoothstep(1.0 - _OutlineWidth * 1.5, 0.96, dist) * inside;
                // 从中心向外“充能”的填充：t=1 时铺满整个区域，玩家一眼看出还剩多久。
                float charge = step(dist, t) * inside;
                float stripes = step(0.5, frac((p.x + p.y) * 3.0 - _Time.y * 1.6));
                float pulse = 0.55 + 0.45 * sin(_Time.y * lerp(6.0, 22.0, t));
                float alpha = inside * 0.3 + charge * 0.14 + stripes * inside * 0.1 + edge * (0.65 + 0.3 * pulse);
                // 深色铺底保持棋子仍可见，粗亮边负责远读；冰雪/金色/星空棋盘都有清晰轮廓。
                float3 rgb = lerp(_Tint.rgb * 0.16, _Tint.rgb * (1.2 + pulse * 0.45), saturate(edge + stripes * 0.26));
                rgb += charge * _Tint.rgb * 0.12;
                return float4(rgb, saturate(alpha));
            }

            // ---------------- 1 冲击波 ----------------
            float4 Shockwave(float2 p, float t)
            {
                float radius = lerp(0.1, 0.95, t);
                float width = lerp(_OutlineWidth * 2.3, _OutlineWidth * .8, t);
                float baseDist = length(p);
                // 两圈以外原输出严格透明。Noise属于[0,1]，扰动最多0.03，留足这个边界后才能跳过噪声/角度。
                // 全屏炸弹冲击波仍有原来的尺寸和两层圆环，只省掉大片透明区域的昂贵片元数学。
                [branch] if (abs(baseDist - radius) > width + .03001 && abs(baseDist - radius * .72) > width * .55 + .03001)
                {
                    return 0;
                }
                float dist = baseDist + (Noise(p * 6.0 + _Time.y * 3.0) - 0.5) * 0.06;
                float ring = saturate(1.0 - abs(dist - radius) / width);
                ring *= ring;
                float fade = 1.0 - t;
                float second = saturate(1.0 - abs(dist - radius * .72) / (width * .55)) * .65;
                float rays = pow(saturate(.5 + .5 * sin(atan2(p.y,p.x) * 16.0)), 10.0) * ring;
                float3 rgb = lerp(_Tint.rgb * .1, _Tint.rgb + float3(.7,.7,.7) * ring, saturate(ring + second));
                return float4(rgb * (1.0 + rays * .6), saturate((ring + second) * fade * 1.45));
            }

            // ---------------- 2 激光 ----------------
            // 局部坐标 x 沿光束方向（0 起点 → 1 终点），y 横穿光束。
            float4 Laser(float2 uv, float t, float sourceAlpha, float coreWhiten)
            {
                float across = abs(uv.y - 0.5) * 2.0;
                // 彩色光束用更饱满的光芯；外侧仍然柔和衰减，浅色格子上不会只剩一根淡线。
                float colored = step(coreWhiten, 0.5);
                float core = saturate(1.0 - across / lerp(0.25, 0.38, colored));
                float glow = saturate(1.0 - across);
                glow = glow * glow;
                float flow = Fbm(float2(uv.x * 8.0 - _Time.y * 9.0, uv.y * 3.0));
                // 开光与收光：进度两端变细变暗，中段最粗最亮。
                float envelope = sin(saturate(t) * 3.14159);
                float flicker = 0.85 + 0.15 * sin(_Time.y * 60.0);
                float energy = (core * 1.3 + glow * (0.45 + flow * 0.55)) * envelope * flicker;
                // 两端用原贴图的柔边收尾，光束不会切成生硬的长方形。
                float ends = smoothstep(0.0, 0.08, uv.x) * smoothstep(1.0, 0.92, uv.x);
                // 彩色模式只让光芯略微提亮；仍复用原来的柔边、流动与开收光，不增加贴图采样。
                float3 rgb = lerp(_Tint.rgb, float3(1, 1, 1), core * coreWhiten);
                // 彩色模式提高线身覆盖率并限制过曝；旧白芯模式保持原有亮度与透明度。
                float contour = smoothstep(.46,.72,across) * (1.0-smoothstep(.8,1.0,across));
                float3 light = lerp(rgb * energy, rgb * (1.0 + core * 0.35 + flow * .22), colored);
                light = lerp(light, _Tint.rgb*.065, contour * _Contrast);
                float coverage = max(sourceAlpha, lerp(0.35, 0.85, colored));
                return float4(light, saturate(max(energy,contour*.8*envelope) * ends * coverage));
            }

            // ---------------- 3 爆炸火球 ----------------
            float4 Explosion(float2 p, float t, float sourceAlpha)
            {
                float dist = length(p);
                // 两层噪声权重之和为1，最大半径不超过此上界；原公式在界外heat/alpha均为0。
                [branch] if (dist > lerp(0.25, 1.0, sqrt(t)) * 1.15001 || t >= 1.0)
                {
                    return 0;
                }
                // 噪声扰动边缘，火球不是完美的圆；随时间翻滚。
                float turbulence = Fbm(p * 3.5 + float2(0, -_Time.y * 2.5));
                float radius = lerp(0.25, 1.0, sqrt(t)) * (0.8 + turbulence * 0.35);
                float body = saturate(1.0 - dist / max(radius, 0.001));
                // 燃尽：后半段从内部开始出现“烧穿”的空洞。
                float burnout = smoothstep(t * 1.1 - 0.15, t * 1.1, turbulence * body);
                float heat = body * lerp(1.0, burnout, saturate(t * 1.6 - 0.4));
                float3 hot = float3(1.0, 0.96, 0.8);
                float3 rim = float3(0.35, 0.05, 0.02);
                float3 rgb = lerp(rim, lerp(_Tint.rgb, hot, smoothstep(0.55, 0.95, heat)), smoothstep(0.0, 0.45, heat));
                float alpha = smoothstep(0.02, 0.25, heat) * (1.0 - smoothstep(0.75, 1.0, t)) * max(sourceAlpha, 0.5);
                return float4(rgb * (0.8 + heat), alpha);
            }

            // ---------------- 5 传送门 / 6 汲取漩涡 ----------------
            // direction = +1 向外旋（传送门），-1 向内收（汲取）。
            float4 Vortex(float2 p, float t, float direction)
            {
                float dist = length(p);
                float angle = atan2(p.y, p.x);
                float open = direction > 0 ? sin(saturate(t) * 3.14159) : 1.0 - t * 0.6;
                float twist = angle * 5.0 + dist * 9.0 * direction - _Time.y * 6.0 * direction;
                float arms = saturate(sin(twist) * 0.5 + 0.5);
                arms = arms * arms;
                float disk = saturate(1.0 - dist / max(open, 0.001));
                float rim = saturate(1.0 - abs(dist - open * 0.85) / 0.12);
                float center = saturate(1.0 - dist / 0.25);
                float alpha = disk * (arms * 0.7 + 0.15) + rim * 0.6 + center * open * 0.5;
                float3 rgb = lerp(_Tint.rgb, float3(1, 1, 1), center * 0.7 + rim * 0.3);
                return float4(rgb * (1.0 + rim), saturate(alpha));
            }

            // ---------------- 8 暗红魔法球 ----------------
            // 圆图只承载 FullRect 网格：不乘源图的 RGB/Alpha，避免细图和透明区域把球体消掉。
            // 实心酒红球壳遮住浅色棋盘，亮红裂纹与双层流动光环提供冲击力；不依赖 Bloom。
            float4 MagicOrb(float2 p, float t)
            {
                float dist = length(p);
                float turbulence = Fbm(p * 5.0 + float2(_Time.y * 1.2, -_Time.y * 1.7));
                float body = 1.0 - smoothstep(0.53, 0.62, dist);
                float core = 1.0 - smoothstep(0.05, 0.44, length(p + float2(0.12, -0.1)));
                float veins = pow(saturate(1.0 - abs(turbulence - 0.52) * 11.0), 3.0) * body;
                float angle = atan2(p.y, p.x);
                float flow = 0.55 + 0.45 * sin(angle * 5.0 - _Time.y * 5.0 + turbulence * 5.0);
                float rim = pow(saturate(1.0 - abs(dist - 0.60) / 0.09), 2.0);
                float rune = pow(saturate(1.0 - abs(dist - 0.79) / 0.045), 2.0) * flow;
                float halo = pow(saturate(1.0 - dist), 3.0);
                float pulse = 0.88 + 0.12 * sin(_Time.y * 8.0);
                float3 shell = lerp(float3(0.035, 0.002, 0.009), _Tint.rgb * 0.52, turbulence);
                float3 rgb = shell + float3(1.0, 0.09, 0.035) * (veins * 0.85 + core * 0.55);
                rgb += float3(0.95, 0.035, 0.075) * (rim * 1.15 + rune * 0.8 + halo * 0.22) * pulse;
                float alpha = saturate(body * 0.98 + rim * 0.9 + rune * 0.75 + halo * 0.32);
                return float4(rgb, alpha * (1.0 - smoothstep(0.85, 1.0, t)));
            }

            // 16~24：九种通用技能独立形态。每个受影响格仍只使用一片，不靠无上限堆粒子制造冲击。
            // 深色能量壳+高亮内纹在亮冰盘和暗星盘都可读；所有遮罩保持透明外边缘。
            float4 CommonPattern(float2 p, float t)
            {
                float r = length(p), a = atan2(p.y,p.x);
                float edge = 1.0-smoothstep(.82,1.0,r);
                float energy = 0, body = 0;
                float envelope = smoothstep(0,.065,t)*(1.0-smoothstep(.63,1.0,t));
                [branch] if (_Mode < 16.5) // 陨星：翻涌火核与裂纹冲击壳。
                {
                    float cloud = Fbm(p*4.0 + float2(_Time.y*1.2,-_Time.y*3.0));
                    body = (1.0-smoothstep(.5,.87,r+cloud*.18))*edge;
                    energy = pow(saturate(1.0-r),2.0)*2.0 + pow(saturate(1.0-abs(cloud-.5)*8.0),3.0)*body;
                }
                else if (_Mode < 17.5) // 十字激光：四臂光核和外侧电弧。
                {
                    float cross = 1.0-smoothstep(.1,.29,min(abs(p.x),abs(p.y)));
                    energy = cross*edge + pow(saturate(.5+.5*sin(r*31.0-_Time.y*19.0)),8.0)*edge*.45;
                    body = (1.0-smoothstep(.24,.39,min(abs(p.x),abs(p.y))))*edge;
                }
                else if (_Mode < 18.5) // 冰霜新星：六角晶枝从内向外爆开。
                {
                    float crystal = pow(saturate(.5+.5*cos(a*6.0)),12.0);
                    float shell = 1.0-smoothstep(_OutlineWidth*.4,_OutlineWidth,abs(r-(.28+t*.63)));
                    energy = (crystal*.8+shell)*edge;
                    body = (crystal*.35+shell*.8)*edge;
                }
                else if (_Mode < 19.5) // 连锁雷电：分叉闪电，随时间迅速摆动。
                {
                    float zig = sin(p.y*15.0+_Time.y*36.0)*.14 + sin(p.y*29.0)*.055;
                    float bolt = 1.0-smoothstep(.045,.14,abs(p.x-zig));
                    float branch = 1.0-smoothstep(.025,.075,abs(p.x+p.y*.6-zig));
                    energy = (bolt+branch*.65)*edge;
                    body = (1.0-smoothstep(.15,.27,abs(p.x-zig)))*edge;
                }
                else if (_Mode < 20.5) // 毒沼：深绿色浆体、发亮泡沫与不规则外沿。
                {
                    float n = Fbm(p*5.0+float2(_Time.y*.5,_Time.y*.2));
                    body = (1.0-smoothstep(.65,.95,r+(n-.5)*.18))*edge;
                    float bubbles = pow(saturate(1.0-abs(n-.56)*12.0),3.0);
                    energy = (bubbles*.95 + pow(saturate(1.0-r),3.0)*.35)*body;
                }
                else if (_Mode < 21.5) // 烈焰墙：火舌向上奔涌，不是普通静态火苗图。
                {
                    float n = Fbm(float2(p.x*4.0,p.y*3.0-_Time.y*5.0));
                    float flame = saturate(1.0-abs(p.x)*(1.4+p.y*.45)+n*.45-p.y*.35);
                    body = smoothstep(.26,.48,flame)*edge;
                    energy = pow(saturate(flame-.2),2.0)*2.0*edge;
                }
                else if (_Mode < 22.5) // 冰枪：粗长棱晶与侧向冰屑。
                {
                    float diamond = abs(p.x)*.72+abs(p.y)*2.7;
                    body = (1.0-smoothstep(.69,.88,diamond))*edge;
                    float facets = pow(saturate(1.0-abs(p.y+p.x*.12)*14.0),2.0);
                    energy = (facets + pow(saturate(1.0-diamond),2.0))*body;
                }
                else if (_Mode < 23.5) // 重力井：深色核心、向内螺旋及发亮视界。
                {
                    float spiral = pow(saturate(.5+.5*sin(a*4.0+r*23.0+_Time.y*10.0)),5.0);
                    body = edge*.8;
                    energy = (spiral*.7+exp(-abs(r-.45)*24.0))*smoothstep(.17,.27,r)*edge;
                }
                else // 末日法印：菱形禁制、旋转刻度与中央裂纹。
                {
                    float diamond = abs(p.x)+abs(p.y);
                    float seal = 1.0-smoothstep(.04,_OutlineWidth,abs(diamond-.71));
                    float rune = pow(saturate(cos(a*12.0-_Time.y*4.0)),12.0)*exp(-abs(r-.61)*20.0);
                    body = (1.0-smoothstep(.75,.98,diamond))*.7;
                    energy = seal+rune+pow(saturate(1.0-min(abs(p.x),abs(p.y))*18.0),4.0)*edge*.65;
                }
                float3 rgb = lerp(_Tint.rgb*.055, _Tint.rgb*1.45+float3(.38,.38,.38)*saturate(energy),saturate(energy));
                return float4(rgb,saturate(max(body*.82,energy))*envelope);
            }

            float4 FragBossSkill(v2f input) : SV_Target
            {
                // 程序化圆环/场不使用原图像素；仅需要素材alpha/颜色的模式才采图。
                float4 source = 0;
                [branch] if ((_Mode > 1.5 && _Mode < 4.5) || (_Mode > 6.5 && _Mode < 7.5) || (_Mode > 8.5 && _Mode < 9.5))
                {
                    source = (float4)SampleSpriteTexture(input.texcoord) * (float4)input.color;
                }
                float2 uv = (input.texcoord - _FxUV.xy) / max(_FxUV.zw, float2(0.0001, 0.0001));
                float2 p = uv * 2.0 - 1.0;
                float t = saturate(_Progress);
                float4 fx;
                [branch] if (_Mode < 0.5)
                {
                    fx = Telegraph(p, t);
                }
                else if (_Mode < 1.5)
                {
                    fx = Shockwave(p, t);
                }
                else if (_Mode < 2.5)
                {
                    fx = Laser(uv, t, source.a, 1.0);
                }
                else if (_Mode < 3.5)
                {
                    fx = Explosion(p, t, source.a);
                }
                else if (_Mode < 4.5)
                {
                    // 贴图本身是白色发光图：用主色染色并加一点闪烁；焦痕这类暗色片用 _Additive=0 正常混合。
                    float flicker = 0.9 + 0.1 * sin(_Time.y * 40.0 + uv.x * 6.0);
                    fx = float4(_Tint.rgb * source.rgb * flicker, source.a * (1.0 - t));
                }
                else if (_Mode < 5.5)
                {
                    fx = Vortex(p, t, 1.0);
                }
                else if (_Mode < 6.5)
                {
                    fx = Vortex(p, t, -1.0);
                }
                else if (_Mode < 7.5)
                {
                    fx = Laser(uv, t, source.a, 0.06);
                }
                else if (_Mode < 8.5)
                {
                    fx = MagicOrb(p, t);
                }
                else if (_Mode < 9.5)
                {
                    // 9 能量炸弹：保留素材alpha，流动扫描与能量裂纹用本地UV生成，不会采到相邻图集。
                    float scan = pow(saturate(1.0 - abs(frac(uv.y - _Time.y * 1.5) - 0.5) * 7.0), 2.0);
                    float crack = pow(saturate(1.0 - abs(Fbm(p * 5.0 + _Time.y) - 0.5) * 12.0), 3.0);
                    float3 shell = lerp(float3(0.055, 0.08, 0.13), _Tint.rgb, scan * 0.65 + crack * 0.45);
                    fx = float4(shell + _Tint.rgb * crack * 0.5, source.a);
                }
                else if (_Mode < 15.5)
                {
                    // 10至15为六章专属程序化场：无需散图Texture，所有图案在Sprite局部坐标生成。
                    float r=length(p), angle=atan2(p.y,p.x);
                    float envelope=smoothstep(0,.08,t)*(1-smoothstep(.82,1,t));
                    float edge=1-smoothstep(.85,1,r);
                    float energy=0;
                    [branch] if (_Mode < 10.5) // 星空：旋臂、星点、暗色视界与发亮吸积环。
                    {
                        float arms=pow(saturate(.5+.5*sin(angle*5+r*18-_Time.y*5)),5);
                        float disk=exp(-abs(r-.45)*18);
                        float stars=pow(Hash(floor(p*22)),35)*edge;
                        energy=(arms*.65+disk+stars)*smoothstep(.18,.29,r);
                    }
                    else if (_Mode < 11.5) // 太阳：扭曲日冕、放射性火舌。
                    { energy=pow(saturate(1-r),2)+pow(saturate(.5+.5*sin(angle*14+Fbm(p*5+_Time.y)*6)),3)*exp(-abs(r-.64)*8); }
                    else if (_Mode < 12.5) // 水流：连续螺旋泡沫与外缘浪花。
                    { energy=pow(saturate(.5+.5*sin(r*29-angle*3+_Time.y*7)),6)*edge+exp(-abs(r-.8)*35)*.7; }
                    else if (_Mode < 13.5) // 沼泽：黏稠涡纹和冒泡裂隙，中心保持深色。
                    { energy=(pow(Fbm(p*7+_Time.y*.5),3)*1.8+exp(-abs(r-.7)*18)) * smoothstep(.12,.4,r)*edge; }
                    else if (_Mode < 14.5) // 冰晶：六向晶枝和破裂冰壳。
                    { energy=pow(saturate(.5+.5*cos(angle*6)),18)*edge+exp(-abs(r-.7)*30)+pow(saturate(1-abs(Fbm(p*12)-.5)*20),3)*edge*.45; }
                    else // 圣域：同心法印、十字圣纹、角向刻度。
                    { energy=exp(-abs(r-.75)*45)+exp(-abs(r-.5)*50)+pow(saturate(1-min(abs(p.x),abs(p.y))*14),3)*edge+pow(saturate(cos(angle*24)),16)*exp(-abs(r-.68)*35); }
                    float3 dark=_Tint.rgb*.08;
                    fx=float4(lerp(dark,_Tint.rgb*1.8+energy*.3,saturate(energy)),saturate(energy*.8+edge*.22)*envelope);
                    // 黑洞独立暗核：遮住棋盘才能读成陷落，而不是另一团紫色光晕。
                    if (_Mode < 10.5 && _Shape > 1.5)
                    {
                        float horizon = 1-smoothstep(.28,.38,r);
                        fx.rgb = lerp(fx.rgb, float3(.006,.002,.018), horizon);
                        fx.a = max(fx.a, horizon * envelope);
                    }
                }
                else
                {
                    fx = CommonPattern(p,t);
                }
                fx.a *= _Tint.a * input.color.a;
                // 高亮芯与深轮廓在同一个Pass内合成；降低浅色棋盘透过量，避免“亮色相加后完全看不见”。
                // 不采屏幕、不做GrabPass/Bloom，也不新建材质；暗色焦痕只保留自身色彩。
                float luminance = max(fx.r,max(fx.g,fx.b));
                fx.rgb *= lerp(1.0,_Intensity,smoothstep(.15,.65,luminance));
                // 预乘：rgb 按 alpha 衰减；再按 _Additive 降低输出 alpha，让发光部分“加”在底图上而不是盖住它。
                fx.rgb *= fx.a;
                fx.a *= 1.0 - min(_Additive,1.0-_Contrast*.7);
                return fx;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}
