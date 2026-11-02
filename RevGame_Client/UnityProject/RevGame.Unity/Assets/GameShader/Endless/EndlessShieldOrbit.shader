// ============================================================
// EndlessShieldOrbit —— 盾战身边环绕旋转的三面盾牌（新手先读这段）
//
// 所有盾战共用一张动态网格，一枚盾战一个方块，整场 1 次绘制。
//   uv   (0~1)  方块内坐标，中心 (0.5, 0.5)；
//   uv2.x       相位（0~1），让多枚盾战的盾牌不同步；
//   uv2.y       盾牌数量（1★=3，2★=4，3★=5）；
//   color.rgb   主色（钢蓝），color.a 整体强度。
// 每面盾牌：在半径 0.62 的圆上公转，并始终“盾面朝外”；用 Kenney CC0 盾牌剪影做形状，
// Shader 里叠出：钢蓝金属渐变 + 金色描边 + 斜向扫过的高光，再加一圈淡淡的公转轨迹。
// ============================================================
Shader "RevGame/Endless/ShieldOrbit"
{
    Properties
    {
        _ShieldTex ("盾牌剪影（非Sprite，钳制采样）", 2D) = "white" {}
        _Speed ("公转速度（圈/秒）", Range(0, 2)) = .45
        _Orbit ("公转半径（方块半宽的比例）", Range(.3, .9)) = .72
        _Scale ("盾牌大小", Range(.15, .6)) = .24
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ShieldTex;
            float _Speed, _Orbit, _Scale;
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; float2 data:TEXCOORD1; fixed4 color:COLOR; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; float2 data:TEXCOORD1; fixed4 color:COLOR; };
            v2f vert(app v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.data = v.data;
                o.color = v.color;
                return o;
            }
            // 在盾牌自己的坐标系里采样：local 的 y 轴指向圆外（盾尖朝里、盾顶朝外）。
            float shieldMask(float2 local)
            {
                float2 q = local * .5 + .5;
                float inside = step(0, q.x) * step(q.x, 1) * step(0, q.y) * step(q.y, 1);
                return tex2D(_ShieldTex, q).a * inside;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv * 2 - 1;
                float count = max(1, i.data.y);
                float spin = (_Time.y * _Speed + i.data.x) * 6.2831853;
                float4 result = 0;
                [unroll] for (int k = 0; k < 5; k++)
                {
                    float on = step(k + .5, count);
                    float a = spin + k * 6.2831853 / count;
                    float2 dir = float2(cos(a), sin(a));
                    float2 q = p - dir * _Orbit;
                    // 把 q 旋到“y 指向圆外”的盾牌坐标：x = 切向，y = 径向。
                    float2 local = float2(dot(q, float2(-dir.y, dir.x)), dot(q, dir)) / _Scale;
                    float body = shieldMask(local) * on;
                    // 内缩采样得到内部形状，body - inner 就是一圈描边。
                    float inner = shieldMask(local * 1.3) * on;
                    float rim = saturate(body - inner);
                    float glint = pow(saturate(1 - abs(frac(local.x * .5 + local.y * .5 - _Time.y * .6 + k * .3) - .5) * 6), 3) * inner;
                    // 钢蓝更深、金边更亮：浅色象牙棋盘上盾牌才有金属质感，不会糊成一团白。
                    fixed3 steel = lerp(fixed3(.16, .24, .4), fixed3(.6, .74, .95), saturate(local.y * .5 + .55));
                    fixed3 color = lerp(steel, fixed3(1, .78, .3), saturate(rim * 1.4)) + glint * .6;
                    float alpha = body;
                    // 后画的盾牌盖住先画的：按 alpha 叠加。
                    result.rgb = lerp(result.rgb, color, alpha);
                    result.a = max(result.a, alpha);
                }
                // 公转轨迹：一圈很淡的细环，让玩家看出“这是一圈护卫”。
                float r = length(p);
                float track = (1 - smoothstep(.006, .03, abs(r - _Orbit))) * .22;
                result.rgb = lerp(fixed3(.7, .85, 1), result.rgb, step(track, result.a));
                result.a = max(result.a, track) * i.color.a;
                return fixed4(result.rgb, result.a);
            }
            ENDCG
        }
    }
}
