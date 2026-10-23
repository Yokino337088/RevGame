// 章节环境独立于棋子/UI Shader。每次绘制只执行当前主题，不用 GrabPass、RT 或额外噪声贴图。
// 原画局部 UV 和屏幕棋盘遮罩分开：不同屏幕比例下，保护区仍跟随真实棋盘。
Shader "RevGame/Background/ChapterAtmosphere"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite 贴图（几何绑定）", 2D) = "white" {}
        [PerRendererData] _ChapterTex ("章节原画（独立纹理）", 2D) = "white" {}
        [Header(Chapter Theme)]
        _Theme ("主题：2村落 3湖泊 4沼泽 5极地 6神殿", Float) = 2
        _Strength ("特效总强度（0=原画）", Range(0,1)) = 0.8
        _Speed ("运动速度", Range(0,2)) = 0.4
        _Distortion ("原画扰动幅度", Range(0,0.04)) = 0.008
        _Glow ("光束与反光强度", Range(0,1.5)) = 0.65
        _Particles ("粒子密度", Range(0,1)) = 0.5
        [Header(Theme Colors)]
        _Tint ("原画色调", Color) = (1,1,1,1)
        _Accent ("主特效颜色", Color) = (1,0.82,0.35,1)
        _Secondary ("辅特效颜色", Color) = (0.45,1,0.67,1)
        [Header(Board Readability)]
        _BoardProtection ("棋盘保护", Range(0,1)) = 0.82
        _BoardRect ("屏幕棋盘矩形 xy起点 zw尺寸", Vector) = (0.3,0.15,0.4,0.7)
        [HideInInspector] _Aspect ("原画宽高比", Float) = 1.777778
        [HideInInspector] _EffectTime ("场景时钟", Float) = 0
        [HideInInspector] _Transition ("入场进度", Float) = 1
        [HideInInspector] _PreviewTime ("固定预览时间（-1=正常）", Float) = -1
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Opaque" "IgnoreProjector"="True" "CanUseSpriteAtlas"="False" "PreviewType"="Plane" }
        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float4 screen : TEXCOORD1; fixed4 color : COLOR; };
            sampler2D _ChapterTex;
            float4 _ChapterTex_TexelSize, _BoardRect;
            float4 _Tint, _Accent, _Secondary;
            float _Theme, _Strength, _Speed, _Distortion, _Glow, _Particles;
            float _Aspect, _EffectTime, _Transition, _PreviewTime, _BoardProtection;

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.screen = ComputeScreenPos(output.position);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            float Hash(float2 p)
            {
                float3 q = frac(float3(p.xyx) * 0.1031);
                q += dot(q, q.yzx + 33.33);
                return frac((q.x + q.y) * q.z);
            }

            float Noise(float2 p)
            {
                float2 cell = floor(p);
                float2 f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(cell), Hash(cell + float2(1,0)), f.x), lerp(Hash(cell + float2(0,1)), Hash(cell + 1), f.x), f.y);
            }

            // 两轮低频三角扭曲形成液态卷曲；位移始终有界，不随时间累计把建筑拧成细丝。
            float2 Flow(float2 p, float time)
            {
                float2 q = p * 4.0;
                q += 0.7 * float2(sin(q.y * 1.4 + time * 0.55), cos(q.x * 1.3 - time * 0.47));
                q += 0.45 * float2(sin(q.y * 1.7 - time * 0.31), cos(q.x * 1.6 + time * 0.39));
                return float2(sin(q.y + q.x * 0.35 + time * 0.4), cos(q.x - q.y * 0.45 - time * 0.36));
            }

            float BoardVisibility(float2 screen)
            {
                float2 nearEdge = min(screen - _BoardRect.xy, _BoardRect.xy + _BoardRect.zw - screen);
                float inside = smoothstep(-0.025, 0.025, min(nearEdge.x, nearEdge.y));
                return 1.0 - inside * _BoardProtection;
            }

            // 散布在多个格子内的光点；两层雪花用不同网格/速度，避免像贴图整片滑动。
            float Motes(float2 p, float time, float density, float scale, float fall, float radius)
            {
                float2 drift = float2(sin(time * 0.7) * 0.18, time * fall);
                float2 q = p * scale + drift;
                float2 cell = floor(q);
                float h = Hash(cell);
                float2 center = float2(0.2 + Hash(cell + 7.1) * 0.6, 0.2 + Hash(cell + 13.7) * 0.6);
                float2 moteOffset = frac(q) - center;
                moteOffset.x += sin(time * 0.8 + h * 19.0) * 0.12;
                float spot = 1.0 - smoothstep(radius * 0.35, radius, length(moteOffset));
                float breathe = 0.65 + 0.35 * sin(time * 1.2 + h * 21.0);
                return spot * step(h, density) * breathe;
            }

            float SunRays(float2 uv, float time, float sway)
            {
                float x = uv.x + (1.0 - uv.y) * 0.38;
                float a = sin(x * 21.0 + sin(time * 0.32) * sway);
                float b = sin(x * 11.0 - time * 0.12);
                float narrow = pow(saturate(a * 0.5 + 0.5), 10.0);
                float broad = pow(saturate(b * 0.5 + 0.5), 4.0);
                return (narrow * 0.65 + broad * 0.3) * smoothstep(0.05, 0.85, uv.y);
            }

            float RingWave(float2 p, float2 center, float time, float offset)
            {
                float radius = length(p - center);
                float phase = radius * 65.0 - time * 3.5 + offset;
                return sin(phase) * exp(-radius * 3.2);
            }

            float MudBubble(float2 p, float time, out float rim, out float bulb)
            {
                float2 q = p * 5.2;
                float2 cell = floor(q);
                float h = Hash(cell + 1.8);
                float2 center = float2(0.25 + Hash(cell + 4.2) * 0.5, 0.25 + Hash(cell + 6.5) * 0.5);
                float phase = frac(time * 0.18 + h);
                float growth = smoothstep(0.03, 0.65, phase);
                float fade = 1.0 - smoothstep(0.75, 0.96, phase);
                float r = length(frac(q) - center);
                float size = 0.09 + growth * 0.2;
                float enabled = step(h, _Particles * 0.8);
                rim = exp(-abs(r - size) * 65.0) * fade * enabled;
                bulb = (1.0 - smoothstep(0.0, size, r)) * fade * enabled;
                return (rim * 0.6 + bulb * 0.4) * growth;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                float2 uv = input.uv;
                float2 gradX = ddx(uv), gradY = ddy(uv);
                float2 screen = input.screen.xy / max(input.screen.w, 0.0001);
                float visible = BoardVisibility(screen);
                float2 p = (uv - 0.5) * float2(_Aspect, 1.0);
                float time = (_PreviewTime >= 0 ? _PreviewTime : _EffectTime) * _Speed;
                float2 margin = min(uv, 1.0 - uv);
                float edgeSafe = smoothstep(0.0, 0.08, min(margin.x, margin.y));
                float2 warp = 0;
                float3 glow = 0;
                float shade = 1.0;
                float wash = 0;
                float3 washColor = _Accent.rgb;

                UNITY_BRANCH if (_Strength > 0.0001)
                {
                    UNITY_BRANCH if (_Theme < 2.5)
                    {
                        float2 flow = Flow(p, time);
                        warp = flow * _Distortion * 0.3;
                        float ribbon = pow(saturate(sin((p.x + flow.x * 0.25) * 7.0 + p.y * 4.0 - time * 0.35) * 0.5 + 0.5), 6.0);
                        float rays = SunRays(uv, time, 0.45);
                        float dust = Motes(p, time, _Particles, 15.0, -0.08, 0.065);
                        glow = (_Accent.rgb * rays * 0.16 + lerp(_Secondary.rgb, _Accent.rgb, ribbon) * ribbon * 0.045 + _Accent.rgb * dust * 0.6) * _Glow;
                        wash = rays * 0.035;
                    }
                    else if (_Theme < 3.5)
                    {
                        float wave = sin(p.x * 23.0 + p.y * 17.0 + time * 1.5);
                        wave += sin(p.x * -18.0 + p.y * 28.0 - time * 1.25) * 0.6;
                        float rings = RingWave(p, float2(-0.55,0.18), time, 0) + RingWave(p, float2(0.5,-0.22), time, 2.1);
                        warp = float2(sin(p.y * 26.0 + time * 1.4) + rings * 0.6, cos(p.x * 21.0 - time * 1.2) + wave * 0.25) * _Distortion;
                        float c1 = sin(p.x * 34.0 + sin(p.y * 19.0 + time * 0.9) * 2.0 - time);
                        float c2 = sin(p.y * 38.0 + sin(p.x * 23.0 - time * 0.7) * 2.0 + time * 1.1);
                        float caustic = pow(saturate(1.0 - abs(c1 + c2) * 1.7), 3.0);
                        float reflection = pow(saturate(sin(p.y * 85.0 + sin(p.x * 8.0 + time) * 1.5 - time * 1.4) * 0.5 + 0.5), 14.0);
                        reflection *= exp(-pow((uv.x - 0.35 + sin(time * 0.25) * 0.08) * 3.0, 2.0));
                        float ripple = pow(saturate(rings * 0.6), 5.0);
                        float sparkle = Motes(p, time, _Particles * 0.6, 22.0, 0.015, 0.035);
                        glow = (_Secondary.rgb * caustic * 0.075 + _Accent.rgb * (reflection * 0.27 + ripple * 0.12 + sparkle * 0.4)) * _Glow;
                        shade = 1.0 + wave * 0.028;
                    }
                    else if (_Theme < 4.5)
                    {
                        float2 flow = Flow(p * 0.75, time * 0.65);
                        warp = flow * _Distortion;
                        float fold = sin(p.x * 10.0 + p.y * 7.0 + flow.x * 2.8 + time * 0.18);
                        float crest = pow(saturate(fold * 0.5 + 0.5), 8.0);
                        float rim, bulb;
                        float bubble = MudBubble(p, time, rim, bulb);
                        float slick = pow(saturate(sin(p.y * 15.0 + flow.y * 3.0 - time * 0.3) * 0.5 + 0.5), 12.0);
                        shade = 1.0 - (1.0 - fold) * 0.045 - bulb * 0.12;
                        glow = (lerp(_Accent.rgb, _Secondary.rgb, sin(p.x * 4.0 + time * 0.4) * 0.5 + 0.5) * (crest * 0.10 + slick * 0.065)
                            + _Accent.rgb * (rim * 0.22 + bubble * 0.035)) * _Glow;
                        wash = bulb * 0.035;
                        washColor = _Secondary.rgb * 0.4;
                    }
                    else if (_Theme < 5.5)
                    {
                        float grain = Noise(p * 8.0 + float2(time * 0.1, 0));
                        float edge = min(margin.x, margin.y);
                        float frost = 1.0 - smoothstep(0.01, 0.10 + grain * 0.08, edge);
                        float sweep = exp(-pow((uv.x + uv.y * 0.6 - 0.8 - sin(time * 0.28) * 0.65) * 9.0, 2.0));
                        float snowFar = Motes(p, time * 0.6, _Particles * 0.85, 31.0, 0.65, 0.055);
                        float snowNear = Motes(p + 7.3, time, _Particles * 0.65, 16.0, 0.9, 0.08);
                        float fog = Noise(p * 3.0 + float2(time * 0.12, -time * 0.07)) * frost;
                        // 白色裂纹主要来自冰封原画本身，只扫过其高光，不给棋盘中心新增粗裂纹。
                        glow = (_Accent.rgb * (sweep * 0.15 + snowFar * 0.42 + snowNear * 0.75) + _Secondary.rgb * fog * 0.065) * _Glow;
                        wash = frost * 0.19 + fog * 0.055;
                        washColor = lerp(_Accent.rgb, float3(1,1,1), 0.6);
                        warp = float2(sin(time * 0.25 + p.y * 4), 0) * _Distortion * frost * 0.25;
                    }
                    else
                    {
                        float rays = SunRays(uv, time, 0.32);
                        float radius = length(p * float2(0.85, 1.0));
                        float halo = exp(-abs(radius - 0.39 - sin(time * 0.3) * 0.014) * 48.0);
                        halo += exp(-abs(radius - 0.46) * 65.0) * 0.4;
                        float aura = exp(-radius * 3.0) * (0.8 + 0.2 * sin(time * 0.45));
                        float dust = Motes(p, time, _Particles * 0.7, 20.0, -0.13, 0.045);
                        float edge = 1.0 - smoothstep(0.04, 0.14, min(margin.x, margin.y));
                        float rune = pow(saturate(sin((uv.x + uv.y) * 95.0 - time * 0.5) * 0.5 + 0.5), 14.0) * edge;
                        glow = (_Accent.rgb * (rays * 0.24 + halo * 0.09 + dust * 0.6 + rune * 0.065) + _Secondary.rgb * aura * 0.035) * _Glow;
                        wash = rays * 0.035;
                        // 建筑保持静止；神圣感来自光束与金粉，而不是让石柱旋转。
                    }
                }

                float effect = saturate(_Strength) * visible;
                float2 warped = uv + warp * effect * edgeSafe;
                float2 texel = max(_ChapterTex_TexelSize.xy * 0.5, float2(0.0001,0.0001));
                warped = clamp(warped, texel, 1.0 - texel);
                float3 art = tex2Dgrad(_ChapterTex, warped, gradX, gradY).rgb * _Tint.rgb;
                float3 color = art * lerp(1.0, shade, effect);
                color = lerp(color, washColor, saturate(wash * effect));
                color += glow * effect;
                // 很短的到达光晕，不整屏闪白，也不让画面先变黑。
                float arrival = (1.0 - saturate(_Transition)) * exp(-length(p) * 3.5) * 0.055 * effect;
                color += _Accent.rgb * arrival;
                return fixed4(saturate(color) * input.color.rgb, 1.0);
            }
            ENDCG
        }
    }
    Fallback Off
}
