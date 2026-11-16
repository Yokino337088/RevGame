Shader "RevGame/YayaMemories/Illustration"
{
    Properties
    {
        _MainTex ("底景", 2D) = "black" {}
        _Atmosphere ("中景特效", 2D) = "black" {}
        _Contact ("接地阴影", 2D) = "black" {}
        _Cast ("角色与前景道具", 2D) = "black" {}
        _Foreground ("近景特效", 2D) = "black" {}
        _Layers ("激活图层", Vector) = (0,0,0,0)
        _Chapter ("章节", Float) = 1
        _EffectClock ("时间", Float) = 0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex, _Atmosphere, _Contact, _Cast, _Foreground;
            float4 _Layers;
            float _Chapter, _EffectClock;

            float Hash(float2 p)
            {
                return frac(sin(dot(p, float2(41.37, 289.19))) * 43758.5453);
            }

            half3 Over(half3 baseColor, half4 layer, half gain)
            {
                return lerp(baseColor, layer.rgb, saturate(layer.a * gain));
            }

            // 细小流星沿各自的直线掠过远空。尾端平滑衰减，不改变原稿轮廓。
            float Meteor(float2 uv, float2 origin, float duration, float offset, float size)
            {
                float age = frac(_EffectClock / duration + offset);
                float travel = saturate(age * 4.7);
                const float2 slope = float2(.89, -.456);
                float2 delta = uv - origin - slope * travel * .43;
                float along = dot(delta, slope);
                float across = dot(delta, float2(.456, .89));
                float spine = exp(-abs(across) / size);
                float trail = exp(-abs(along) / (size * 40.0)) * (1 - smoothstep(0, size * 2.5, along));
                return spine * trail * smoothstep(0, .11, travel) * (1 - smoothstep(.28, .86, travel));
            }

            // 将粒子限制在分散网格的单元内部，用时间扫过视野。
            float Motes(float2 uv, float2 drift, float scale, float radius)
            {
                float2 p = float2(uv.x * 1.77778, uv.y) * scale + drift;
                float2 tile = floor(p);
                float seed = Hash(tile);
                float2 center = lerp(.14, .86, float2(seed, Hash(tile + 19.7)));
                float d = length(frac(p) - center);
                return (1 - smoothstep(radius * .44, radius, d)) * lerp(.44, 1, seed);
            }

            // 光线从画外和窗框斜入，数条宽窄不同的光束略有起伏。
            float Shaft(float2 uv, float2 origin, float angle, float width)
            {
                float2 toPixel = uv - origin;
                float2 axis = normalize(float2(angle, -1));
                float along = dot(toPixel, axis);
                float sideways = dot(toPixel, float2(-axis.y, axis.x));
                float spread = width + max(0, along) * .045;
                float soft = exp(-abs(sideways) / spread * 1.8);
                return soft * smoothstep(-.08, .13, along) * (1 - smoothstep(.24, 1.24, along));
            }

            half4 Frag(v2f_img input) : SV_Target
            {
                float2 uv = input.uv;
                float time = _EffectClock;
                float2 backdrop = uv;
                float2 air = uv;
                float2 front = uv;
                half airGain = _Layers.x;
                half frontGain = _Layers.z;
                half3 lighting = 0;
                half3 floating = 0;

                [branch] if (_Chapter < 1.5)
                {
                    float phase = frac(time / 7.6);
                    air -= float2(.105, -.069) * phase;
                    airGain *= .57 + .43 * sin(phase * UNITY_PI);
                    float sparks = Meteor(uv, float2(.19,.88), 4.9, .08, .0014)
                        + .54 * Meteor(uv, float2(.54,.95), 7.4, .38, .0010)
                        + .45 * Meteor(uv, float2(.09,.74), 8.6, .72, .0010);
                    lighting += half3(.79,.91,1) * sparks * smoothstep(.54, .72, uv.y);
                }
                else if (_Chapter < 2.5)
                {
                    float rays = .58 * Shaft(uv, float2(.117,.98), 1.06 + .10 * sin(time * .29), .067);
                    rays += Shaft(uv, float2(.183,.93), 1.37 + .06 * sin(time * .43), .038) * .38;
                    rays += Shaft(uv, float2(.243,.91), .75, .034) * .3;
                    lighting += half3(.34,.23,.105) * rays * (.9 + .1 * sin(time * .8));
                    floating += half3(.91,.69,.32) * Motes(uv, float2(-time * .085, -time * .17), 23, .039) * rays * .54;
                    front += float2(sin(uv.y * 45 + time * 1.03) * .005, sin(time * .75) * .012);
                    frontGain *= .71 + .22 * sin(time * 1.24);
                }
                else if (_Chapter < 3.5)
                {
                    // 水线以上保持静止，水中倒影随两组相位错开的横向波轻微形变。
                    float water = (1 - smoothstep(.525, .549, uv.y)) * smoothstep(.045, .19, uv.y);
                    float wave = sin(uv.y * 162 + uv.x * 19 + time * 2.3);
                    float swell = sin(uv.y * 87 - uv.x * 23 - time * 1.45);
                    backdrop += float2(.0052 * wave + .0031 * swell, .0015 * swell) * water;
                    air += float2(.025 * sin(time * .39), .009 * sin(time * .87 + uv.x * 11));
                    airGain *= .68 + .30 * sin(time * 1.27);
                    lighting += half3(.045,.093,.128) * water * pow(saturate(wave * .75 + swell * .25), 8);
                }
                else if (_Chapter < 4.5)
                {
                    air += float2(.034 * sin(time * .42 + uv.y * 3), .012 * sin(time * .28 + uv.x * 6));
                    airGain *= .78 + .17 * sin(time * .73);
                    front += float2(-.088 * sin(time * .36 + uv.y * 7), .026 * sin(time * .56 + uv.x * 9));
                    frontGain *= .74 + .24 * sin(time * .62 + uv.x * 8);
                }
                else if (_Chapter < 5.5)
                {
                    float fire = sin(time * 13.3) * .45 + sin(time * 8.47 + 2) * .35 + sin(time * 19) * .2;
                    air.x += .0036 * sin(time * 6.37 + uv.y * 28);
                    airGain *= .89 + .108 * fire;
                    float2 fireDistance = (uv - float2(.263, .205)) * float2(1.8, 1.0);
                    lighting += half3(.23,.085,.02) * exp(-length(fireDistance) * 11.5) * (.7 + .3 * fire);
                    float snow = Motes(uv, float2(-time * .61, time * .85), 27, .064)
                        + .62 * Motes(uv, float2(-time * .83, time * 1.28), 13, .047)
                        + .44 * Motes(uv, float2(-time * .39, time * .72), 42, .066);
                    float faceClear = lerp(1, .32, smoothstep(.65, .83, uv.x) * (1 - smoothstep(.64, .76, uv.y)));
                    floating += half3(.70,.85,1) * snow * faceClear * .86;
                }
                else
                {
                    float rays = Shaft(uv, float2(-.017, .97), 1.01 + .1 * sin(time * .36), .07);
                    rays += .5 * Shaft(uv, float2(.034, 1.03), 1.55, .055);
                    rays += .46 * Shaft(uv, float2(.09, 1.01), 2.65, .036);
                    float2 gateDistance = (uv - float2(.422,.402)) * float2(17, 7);
                    float glow = exp(-dot(gateDistance, gateDistance));
                    float pulse = .74 + .19 * sin(time * 1.25) + .07 * sin(time * 2.71);
                    lighting += half3(.31,.158,.043) * (rays * pulse + glow * .34);
                    floating += half3(.94,.66,.24) * Motes(uv, float2(-time * .087, -time * .25), 27, .048) * (.22 + .62 * saturate(rays + glow)) * pulse;
                    frontGain *= pulse;
                }

                half3 color = tex2D(_MainTex, backdrop).rgb + lighting;
                color = Over(color, tex2D(_Atmosphere, air), airGain);
                color = Over(color, tex2D(_Contact, uv), _Layers.y);
                color = Over(color, tex2D(_Cast, uv), 1);
                color = Over(color, tex2D(_Foreground, front), frontGain);
                return half4(saturate(color + floating), 1);
            }
            ENDCG
        }
    }
    Fallback Off
}
