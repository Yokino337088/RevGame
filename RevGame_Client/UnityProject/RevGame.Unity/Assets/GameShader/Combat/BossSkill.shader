// ============================================================
// BossSkill —— Boss 技能特效的统一 Shader（9 种效果模式，单 Pass，兼容 SpriteRenderer 与图集）。
//
// 【为什么一个 Shader 做 9 种效果】
// Boss 每个技能的特效由若干“片”（SpriteRenderer）拼成：预警圈、冲击波、激光、火球……
// 用一个 Shader + 每片自己的 _Mode 参数，所有片共享同一个材质与同一份图集贴图，
// 不需要为每种技能克隆材质，也不会因为切换 Shader 造成大量 SetPass。
// _Mode 是“整片统一”的参数，GPU 上每片只走一个分支，开销与普通 Sprite 相近。
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
        [PerRendererData] _Mode ("效果模式 0~8", Float) = 4
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

            half _Mode;
            half _Progress;
            fixed4 _Tint;
            half _Shape;
            half _Additive;
            float4 _FxUV;

            // ---------------- 噪声工具（不依赖额外贴图，不增加采样） ----------------
            half Hash(float2 p)
            {
                return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
            }

            half Noise(float2 p)
            {
                float2 i = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                half a = Hash(i);
                half b = Hash(i + float2(1, 0));
                half c = Hash(i + float2(0, 1));
                half d = Hash(i + float2(1, 1));
                return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
            }

            // 两层叠加的分形噪声：低频决定大形状，高频补细节。只叠两层，手机上足够便宜。
            half Fbm(float2 p)
            {
                return Noise(p) * 0.65 + Noise(p * 2.7 + 13.1) * 0.35;
            }

            // ---------------- 0 预警区域 ----------------
            // 内部：低透明度填充 + 斜向流动警示纹；边缘：亮描边；进度越高，脉冲越快越亮。
            fixed4 Telegraph(float2 p, half t)
            {
                half dist = _Shape > 0.5 ? max(abs(p.x), abs(p.y)) : length(p);
                half inside = step(dist, 1.0);
                half edge = smoothstep(0.86, 0.97, dist) * inside;
                // 从中心向外“充能”的填充：t=1 时铺满整个区域，玩家一眼看出还剩多久。
                half charge = step(dist, t) * inside;
                half stripes = step(0.5, frac((p.x + p.y) * 3.0 - _Time.y * 1.6));
                half pulse = 0.55 + 0.45 * sin(_Time.y * lerp(6.0, 22.0, t));
                half alpha = inside * 0.16 + charge * 0.22 + stripes * inside * 0.08 + edge * (0.55 + 0.4 * pulse);
                half3 rgb = _Tint.rgb * (0.7 + edge * 0.8 * pulse) + charge * 0.15;
                return fixed4(rgb, saturate(alpha));
            }

            // ---------------- 1 冲击波 ----------------
            fixed4 Shockwave(float2 p, half t)
            {
                half dist = length(p) + (Noise(p * 6.0 + _Time.y * 3.0) - 0.5) * 0.06;
                half radius = lerp(0.1, 0.95, t);
                half width = lerp(0.22, 0.04, t);
                half ring = saturate(1.0 - abs(dist - radius) / width);
                ring *= ring;
                half fade = 1.0 - t;
                half3 rgb = lerp(_Tint.rgb, half3(1, 1, 1), ring * 0.6);
                return fixed4(rgb * (1.0 + ring), ring * fade);
            }

            // ---------------- 2 激光 ----------------
            // 局部坐标 x 沿光束方向（0 起点 → 1 终点），y 横穿光束。
            fixed4 Laser(float2 uv, half t, half sourceAlpha, half coreWhiten)
            {
                half across = abs(uv.y - 0.5) * 2.0;
                // 彩色光束用更饱满的光芯；外侧仍然柔和衰减，浅色格子上不会只剩一根淡线。
                half colored = step(coreWhiten, 0.5);
                half core = saturate(1.0 - across / lerp(0.18, 0.3, colored));
                half glow = saturate(1.0 - across);
                glow = glow * glow;
                half flow = Fbm(float2(uv.x * 8.0 - _Time.y * 9.0, uv.y * 3.0));
                // 开光与收光：进度两端变细变暗，中段最粗最亮。
                half envelope = sin(saturate(t) * 3.14159);
                half flicker = 0.85 + 0.15 * sin(_Time.y * 60.0);
                half energy = (core * 1.3 + glow * (0.45 + flow * 0.55)) * envelope * flicker;
                // 两端用原贴图的柔边收尾，光束不会切成生硬的长方形。
                half ends = smoothstep(0.0, 0.08, uv.x) * smoothstep(1.0, 0.92, uv.x);
                // 彩色模式只让光芯略微提亮；仍复用原来的柔边、流动与开收光，不增加贴图采样。
                half3 rgb = lerp(_Tint.rgb, half3(1, 1, 1), core * coreWhiten);
                // 彩色模式提高线身覆盖率并限制过曝；旧白芯模式保持原有亮度与透明度。
                half3 light = lerp(rgb * energy, rgb * (0.85 + core * 0.15), colored);
                half coverage = max(sourceAlpha, lerp(0.35, 0.85, colored));
                return fixed4(light, saturate(energy * ends * coverage));
            }

            // ---------------- 3 爆炸火球 ----------------
            fixed4 Explosion(float2 p, half t, half sourceAlpha)
            {
                half dist = length(p);
                // 噪声扰动边缘，火球不是完美的圆；随时间翻滚。
                half turbulence = Fbm(p * 3.5 + float2(0, -_Time.y * 2.5));
                half radius = lerp(0.25, 1.0, sqrt(t)) * (0.8 + turbulence * 0.35);
                half body = saturate(1.0 - dist / max(radius, 0.001));
                // 燃尽：后半段从内部开始出现“烧穿”的空洞。
                half burnout = smoothstep(t * 1.1 - 0.15, t * 1.1, turbulence * body);
                half heat = body * lerp(1.0, burnout, saturate(t * 1.6 - 0.4));
                half3 hot = half3(1.0, 0.96, 0.8);
                half3 rim = half3(0.35, 0.05, 0.02);
                half3 rgb = lerp(rim, lerp(_Tint.rgb, hot, smoothstep(0.55, 0.95, heat)), smoothstep(0.0, 0.45, heat));
                half alpha = smoothstep(0.02, 0.25, heat) * (1.0 - smoothstep(0.75, 1.0, t)) * max(sourceAlpha, 0.5);
                return fixed4(rgb * (0.8 + heat), alpha);
            }

            // ---------------- 5 传送门 / 6 汲取漩涡 ----------------
            // direction = +1 向外旋（传送门），-1 向内收（汲取）。
            fixed4 Vortex(float2 p, half t, half direction)
            {
                half dist = length(p);
                half angle = atan2(p.y, p.x);
                half open = direction > 0 ? sin(saturate(t) * 3.14159) : 1.0 - t * 0.6;
                half twist = angle * 5.0 + dist * 9.0 * direction - _Time.y * 6.0 * direction;
                half arms = saturate(sin(twist) * 0.5 + 0.5);
                arms = arms * arms;
                half disk = saturate(1.0 - dist / max(open, 0.001));
                half rim = saturate(1.0 - abs(dist - open * 0.85) / 0.12);
                half center = saturate(1.0 - dist / 0.25);
                half alpha = disk * (arms * 0.7 + 0.15) + rim * 0.6 + center * open * 0.5;
                half3 rgb = lerp(_Tint.rgb, half3(1, 1, 1), center * 0.7 + rim * 0.3);
                return fixed4(rgb * (1.0 + rim), saturate(alpha));
            }

            // ---------------- 8 暗红魔法球 ----------------
            // 圆图只承载 FullRect 网格：不乘源图的 RGB/Alpha，避免细图和透明区域把球体消掉。
            // 实心酒红球壳遮住浅色棋盘，亮红裂纹与双层流动光环提供冲击力；不依赖 Bloom。
            fixed4 MagicOrb(float2 p, half t)
            {
                half dist = length(p);
                half turbulence = Fbm(p * 5.0 + float2(_Time.y * 1.2, -_Time.y * 1.7));
                half body = 1.0 - smoothstep(0.53, 0.62, dist);
                half core = 1.0 - smoothstep(0.05, 0.44, length(p + float2(0.12, -0.1)));
                half veins = pow(saturate(1.0 - abs(turbulence - 0.52) * 11.0), 3.0) * body;
                half angle = atan2(p.y, p.x);
                half flow = 0.55 + 0.45 * sin(angle * 5.0 - _Time.y * 5.0 + turbulence * 5.0);
                half rim = pow(saturate(1.0 - abs(dist - 0.60) / 0.09), 2.0);
                half rune = pow(saturate(1.0 - abs(dist - 0.79) / 0.045), 2.0) * flow;
                half halo = pow(saturate(1.0 - dist), 3.0);
                half pulse = 0.88 + 0.12 * sin(_Time.y * 8.0);
                half3 shell = lerp(half3(0.035, 0.002, 0.009), _Tint.rgb * 0.52, turbulence);
                half3 rgb = shell + half3(1.0, 0.09, 0.035) * (veins * 0.85 + core * 0.55);
                rgb += half3(0.95, 0.035, 0.075) * (rim * 1.15 + rune * 0.8 + halo * 0.22) * pulse;
                half alpha = saturate(body * 0.98 + rim * 0.9 + rune * 0.75 + halo * 0.32);
                return fixed4(rgb, alpha * (1.0 - smoothstep(0.85, 1.0, t)));
            }

            fixed4 FragBossSkill(v2f input) : SV_Target
            {
                fixed4 source = SampleSpriteTexture(input.texcoord) * input.color;
                float2 uv = (input.texcoord - _FxUV.xy) / max(_FxUV.zw, float2(0.0001, 0.0001));
                float2 p = uv * 2.0 - 1.0;
                half t = saturate(_Progress);
                fixed4 fx;
                if (_Mode < 0.5)
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
                    half flicker = 0.9 + 0.1 * sin(_Time.y * 40.0 + uv.x * 6.0);
                    fx = fixed4(_Tint.rgb * source.rgb * flicker, source.a * (1.0 - t));
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
                    half scan = pow(saturate(1.0 - abs(frac(uv.y - _Time.y * 1.5) - 0.5) * 7.0), 2.0);
                    half crack = pow(saturate(1.0 - abs(Fbm(p * 5.0 + _Time.y) - 0.5) * 12.0), 3.0);
                    half3 shell = lerp(half3(0.055, 0.08, 0.13), _Tint.rgb, scan * 0.65 + crack * 0.45);
                    fx = fixed4(shell + _Tint.rgb * crack * 0.5, source.a);
                }
                else
                {
                    // 10至15为六章专属程序化场：无需散图Texture，所有图案在Sprite局部坐标生成。
                    half r=length(p), angle=atan2(p.y,p.x);
                    half envelope=smoothstep(0,.08,t)*(1-smoothstep(.82,1,t));
                    half edge=1-smoothstep(.85,1,r);
                    half energy=0;
                    if (_Mode < 10.5) // 星空：旋臂、星点、暗色视界与发亮吸积环。
                    {
                        half arms=pow(saturate(.5+.5*sin(angle*5+r*18-_Time.y*5)),5);
                        half disk=exp(-abs(r-.45)*18);
                        half stars=pow(Hash(floor(p*22)),35)*edge;
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
                    half3 dark=_Tint.rgb*.08;
                    fx=fixed4(lerp(dark,_Tint.rgb*1.8+energy*.3,saturate(energy)),saturate(energy*.8+edge*.22)*envelope);
                    // 黑洞独立暗核：遮住棋盘才能读成陷落，而不是另一团紫色光晕。
                    if (_Mode < 10.5 && _Shape > 1.5)
                    {
                        half horizon = 1-smoothstep(.28,.38,r);
                        fx.rgb = lerp(fx.rgb, half3(.006,.002,.018), horizon);
                        fx.a = max(fx.a, horizon * envelope);
                    }
                }
                fx.a *= _Tint.a * input.color.a;
                // 预乘：rgb 按 alpha 衰减；再按 _Additive 降低输出 alpha，让发光部分“加”在底图上而不是盖住它。
                fx.rgb *= fx.a;
                fx.a *= 1.0 - _Additive;
                return fx;
            }
            ENDCG
        }
    }
    Fallback "Sprites/Default"
}
