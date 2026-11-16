Shader "RevGame/YayaMemories/PaperCurl"
{
    Properties
    {
        [PerRendererData] _MainTex ("书本纸张或封面", 2D) = "white" {}
        _Current ("当前插画", 2D) = "black" {}
        _Next ("目标插画", 2D) = "black" {}
        _HasCurrent ("当前插画已就绪", Float) = 0
        _HasNext ("目标插画已就绪", Float) = 0
        _Progress ("翻页进度", Range(0,1)) = 0
        _Direction ("翻页方向", Float) = 1
        _Tilt ("翻页倾斜", Float) = 0
        _CoverMode ("封面", Float) = 0
        _PhotoRect ("插画区域", Vector) = (.137,.169,.863,.895)
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "CanUseSpriteAtlas"="False" }
        Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex, _Current, _Next;
            float4 _PhotoRect;
            float _HasCurrent, _HasNext, _Progress, _Direction, _Tilt, _CoverMode;

            struct Attributes
            {
                float4 position : POSITION;
                float2 uv : TEXCOORD0;
                half4 tint : COLOR;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 tint : COLOR;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.position = UnityObjectToClipPos(v.position);
                o.uv = v.uv;
                o.tint = v.tint;
                return o;
            }

            float2 OriginalUV(float2 folded)
            {
                if (_Direction < 0)
                {
                    folded.x = 1 - folded.x;
                }
                return folded;
            }

            float LeafEdge(float2 uv)
            {
                float2 inset = lerp(float2(.043,.033), float2(0,0), _CoverMode);
                float2 edge = min(uv, 1 - uv) - inset;
                return min(edge.x, edge.y);
            }

            // 照片框的宽高占比一致，换页及适配后原画始终保持16:9。
            half3 Printed(float2 uv, sampler2D illustration, float enabled)
            {
                half3 parchment = tex2D(_MainTex, uv).rgb;
                float2 pictureMin = _PhotoRect.xy;
                float2 pictureMax = _PhotoRect.zw;
                float2 edge = min(uv - pictureMin, pictureMax - uv);
                float d = min(edge.x, edge.y * .5625);
                float mount = smoothstep(-.009, -.008, d);
                float rule = smoothstep(-.007, -.005, d) - smoothstep(-.0035, -.003, d);
                half3 baseColor = lerp(parchment, half3(.93,.785,.533), mount * lerp(.11, .6, enabled));
                baseColor *= 1 - rule * lerp(.13, .36, enabled);
                [branch] if (enabled > .5 && d > -.001)
                {
                    float2 imageUV = (uv - pictureMin) / (pictureMax - pictureMin);
                    half3 photo = tex2D(illustration, imageUV).rgb;
                    return lerp(baseColor, photo, smoothstep(-.0005, .0006, d));
                }
                return baseColor;
            }

            half4 Front(float2 uv)
            {
                if (_CoverMode > .5)
                {
                    return half4(tex2D(_MainTex, uv).rgb, 1);
                }
                return half4(Printed(uv, _Current, _HasCurrent), 1);
            }

            half4 Revealed(float2 uv, float shadow)
            {
                if (_CoverMode > .5)
                {
                    return half4(.11,.052,.028, shadow);
                }
                return half4(Printed(uv, _Next, _HasNext) * (1 - shadow), 1);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.uv;
                if (_Progress <= .0001 || LeafEdge(uv) < 0)
                {
                    return Front(uv) * input.tint;
                }
                if (_Progress >= .9999)
                {
                    return Revealed(uv, 0) * input.tint;
                }

                // 把纸张视为绕圆柱折起的薄片。r*sin(angle) 是投影，r*angle 是展开后的纸面坐标。
                // 同一像素的正面和背面有两个解，有背面时须优先绘制距离镜头最近的纸面。
                float2 oriented = OriginalUV(uv);
                float2 normal = normalize(float2(1, _Tilt));
                float spine = lerp(1.10, -.25, _Progress);
                float distanceToFold = dot(oriented - float2(spine,.5), normal);
                float radius = lerp(.043, .102, sin(_Progress * UNITY_PI));
                float shadow = .39 * exp(-max(0, distanceToFold - radius) * 14) * sin(_Progress * UNITY_PI);

                if (distanceToFold <= 0)
                {
                    half4 flat = Front(uv);
                    flat.rgb *= 1 - exp(distanceToFold * 32) * .14 * sin(_Progress * UNITY_PI);
                    return flat * input.tint;
                }
                if (distanceToFold >= radius)
                {
                    return Revealed(uv, shadow) * input.tint;
                }

                float angle = asin(saturate(distanceToFold / radius));
                float2 frontUV = OriginalUV(oriented + (angle * radius - distanceToFold) * normal);
                float2 reverseUV = OriginalUV(oriented + ((UNITY_PI - angle) * radius - distanceToFold) * normal);
                float backEdge = LeafEdge(reverseUV);
                float frontEdge = LeafEdge(frontUV);
                if (frontEdge < -.0007 && backEdge < -.0007)
                {
                    return Revealed(uv, shadow) * input.tint;
                }

                half4 visible = Revealed(uv, shadow);
                half edgeLight = pow(saturate(distanceToFold / radius), 11);
                half frontCoverage = smoothstep(-.0007,.0007,frontEdge);
                if (frontCoverage > 0)
                {
                    half4 sheet = Front(saturate(frontUV));
                    sheet.rgb *= .64 + .36 * cos(angle);
                    sheet.rgb += edgeLight * half3(.14,.106,.057);
                    visible = lerp(visible, sheet, frontCoverage);
                }
                if (backEdge > -.0007)
                {
                    // 小幅透墨与纸纤维，折脊高光和遮蔽随翻页方向移动。
                    float2 grainUV = lerp(float2(.55,.17), float2(.91,.89), saturate(reverseUV));
                    half3 grain = tex2D(_MainTex, grainUV).rgb;
                    half3 underside = lerp(half3(.92,.79,.58), grain, .32);
                    if (_CoverMode < .5)
                    {
                        underside = lerp(underside, Printed(saturate(reverseUV), _Current, _HasCurrent), .14);
                    }
                    underside *= .69 + .31 * sin(angle);
                    underside += edgeLight * half3(.18,.14,.084);
                    underside += (1 - smoothstep(.0005,.0042,backEdge)) * half3(.13,.098,.047);
                    visible = lerp(visible, half4(underside,1), smoothstep(-.0007,.0007,backEdge));
                }
                return visible * input.tint;
            }
            ENDCG
        }
    }
    Fallback Off
}
