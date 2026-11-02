// ============================================================
// EndlessSwordAura —— 近战棋的环绕剑气（新手先读这段）
//
// 所有近战棋的剑气画在同一张动态网格里，一枚棋子一个方块，整场只有 1 次绘制调用。
// 每个方块的顶点数据：
//   uv  (0~1)          方块内坐标，中心是 (0.5, 0.5)；
//   uv2.x              相位（0~1），让各棋子的剑刃不要整齐划一地同时转；
//   uv2.y              剑刃数量（星级：1★=2 刃，2★=3 刃，3★=4 刃）；
//   color.rgb          剑气主色（按棋种区分）；
//   color.a            切中敌人时的闪光强度（0~1，表现层衰减）。
// 画面由四层叠成：
//   1. 旋转剑刃：同一张弧形刃贴图（Kenney CC0 twirl_03）按刃数均匀旋转采样；
//   2. 残影拖尾：每片刃再往回采样两次、逐次变淡，形成高速旋转的拖影；
//   3. 外圈符文环：细光环上流动的短划线，标出“切割范围”，看到多大就切多大；
//   4. 切中闪光：命中瞬间整圈提亮并向外扩一下，让玩家感到“切到了”。
// 浅色象牙棋盘上纯加色会发白看不清，所以用普通透明混合，并给剑刃压一圈深色底边保证对比度。
// ============================================================
Shader "RevGame/Endless/SwordAura"
{
    Properties
    {
        _BladeTex ("剑刃贴图（非Sprite，钳制采样）", 2D) = "white" {}
        _Speed ("旋转速度（圈/秒）", Range(0, 4)) = 1.35
        _Trail ("拖尾长度（弧度）", Range(0, 1.2)) = .55
        _RingWidth ("外圈宽度", Range(.005, .08)) = .028
        _Strength ("整体强度", Range(0, 2)) = 1.15
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
            sampler2D _BladeTex;
            float _Speed, _Trail, _RingWidth, _Strength;
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
            // 把方块内坐标绕中心旋转 angle 弧度后，换回贴图坐标并采样；超出 0~1 的部分视为透明（贴图是钳制采样）。
            float blade(float2 p, float angle)
            {
                float s = sin(angle);
                float c = cos(angle);
                float2 q = float2(c * p.x - s * p.y, s * p.x + c * p.y) * .5 + .5;
                float inside = step(0, q.x) * step(q.x, 1) * step(0, q.y) * step(q.y, 1);
                return tex2D(_BladeTex, q).a * inside;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv * 2 - 1;
                float r = length(p);
                float angle = atan2(p.y, p.x);
                float flash = i.color.a;
                float count = max(1, i.data.y);
                // 闪光时剑气整体快转一点、外扩一点，命中反馈更有冲击力。
                float spin = (_Time.y * _Speed * (1 + flash * .6) + i.data.x) * 6.2831853;
                float2 scaled = p / (1 + flash * .08);
                float edge = 0;
                float trail = 0;
                // 刃数最多 4 片；固定循环次数便于移动端编译展开。
                [unroll] for (int k = 0; k < 4; k++)
                {
                    float on = step(k + .5, count);
                    float a = spin + k * 6.2831853 / count;
                    edge = max(edge, blade(scaled, a) * on);
                    trail += (blade(scaled, a - _Trail * .5) * .55 + blade(scaled, a - _Trail) * .28) * on;
                }
                trail = saturate(trail);
                // 外圈：细光环 + 沿圈流动的短划线（12 段），表示切割半径。
                float ring = 1 - smoothstep(_RingWidth * .4, _RingWidth, abs(r - .93));
                float dashes = pow(saturate(cos(angle * 12 - _Time.y * 9 - i.data.x * 6.28)), 6);
                ring *= .35 + .65 * dashes;
                // 中心淡淡的能量场，只在闪光时明显。
                float field = (1 - smoothstep(.2, .95, r)) * (.06 + flash * .22);
                float core = saturate(edge * 1.9);
                float alpha = saturate(core + trail * .8 + ring * (.6 + flash * .4) + field) * _Strength * step(r, 1);
                // 颜色：刃芯接近白色，拖尾与外圈用主色，底边压深主色保证浅色棋盘上的对比度。
                fixed3 tint = i.color.rgb;
                fixed3 deep = tint * .22;
                fixed3 bright = lerp(tint, fixed3(1, 1, 1), .55 + flash * .4);
                fixed3 color = lerp(deep, tint, saturate(trail * 1.2 + ring * .8));
                color = lerp(color, bright, core);
                return fixed4(color, saturate(alpha));
            }
            ENDCG
        }
    }
}
