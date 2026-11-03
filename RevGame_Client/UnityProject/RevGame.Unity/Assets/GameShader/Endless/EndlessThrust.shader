// ============================================================
// EndlessThrust —— 刺客直线突刺（新手先读这段）
//
// 连续长剑气由亮芯、平行刃线、紫色外晕和细速度线组成；没有单独飞出去的三角子弹。
// 整条剑气先迅速展开，再在线尾闪光并收细消失，整个过程约0.4秒。
// 所有刺客的突刺画在一张动态网格里，一次突刺一个沿攻击线摆放的长方块，整场 1 次绘制：
//   uv.x  从刺客（0）到线尾（1）；uv.y 横跨线宽（0~1，中线 0.5）；
//   uv2.x 进度（0~1，表现层按时间推进）；color.rgb 主色（紫影），color.a 整体强度。
// 残影形状用Kenney CC0的速度线/刃光贴图，亮芯、外晕和闪光由Shader程序生成。
// ============================================================
Shader "RevGame/Endless/Thrust"
{
    Properties
    {
        _BladeTex ("长型剑气刃光（Kenney CC0）", 2D) = "white" {}
        _TrailTex ("速度残影（非Sprite，钳制采样）", 2D) = "white" {}
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
            sampler2D _TrailTex, _BladeTex;
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
            fixed4 frag(v2f i) : SV_Target
            {
                float progress = i.data.x;
                // 前28%时间展开整条剑气，之后保留长束并逐渐消失；不出现脱离起点的尖头。
                float head = saturate(progress / .28);
                float fade = 1 - smoothstep(.35, 1, progress);
                float x = i.uv.x;
                float y = abs(i.uv.y - .5) * 2;
                // 剑气始终是连续长束；移除三角尖头，避免看成一颗从刺客身上飞出的子弹。
                float behind = head - x;
                // 残影：只在尖头身后，越靠近尖头越亮；收尾时整体变细。
                float trailMask = step(0, behind) * step(x, head);
                float along = saturate(1 - behind / max(.05, head));
                float thin = lerp(1, .35, 1 - fade);
                float trail = tex2D(_TrailTex, float2(saturate(along), saturate(.5 + (i.uv.y - .5) / thin))).a * trailMask * along;
                // 速度线：几条贴着中线的细亮线，随时间向前滚动。
                float lines = pow(saturate(1 - abs(frac(i.uv.y * 5 + .5) - .5) * 14), 2) * step(y, .8) * trailMask * along * .5;
                lines *= step(.5, frac(x * 6 - progress * 10));
                // 扎到线尾的闪光：尖头抵达后在线尾爆开再淡出。
                float arrive = saturate((progress - .45) / .15) * fade;
                float flash = (1 - smoothstep(0, .18, length(float2((x - 1) * 4, i.uv.y - .5)))) * arrive;
                float core = saturate(flash);
                // 长型刃光沿整条攻击线展开，边缘透明，亮芯与三条细束构成真正的剑气。
                float reveal = 1-smoothstep(head-.06,head+.025,x);
                float blade = tex2D(_BladeTex,float2(x,i.uv.y)).a * reveal;
                float spine = (1-smoothstep(.025,.18,y))*smoothstep(0,.045,x)*(1-smoothstep(.94,1,x))*reveal;
                // 侧翼两条平行刃线和紫色外晕形成夸张速度感，仍限制在同一长方块，只有一次绘制。
                float wings = (1-smoothstep(.018,.07,abs(y-.35)))*reveal*smoothstep(0,.07,x)*(1-smoothstep(.9,1,x));
                float halo = (1-smoothstep(.15,.85,y))*reveal*.35;
                float alpha = saturate(trail * .65 + lines + flash + blade + spine + wings*.75 + halo) * i.color.a * (progress < 1 ? 1 : 0);
                fixed3 tint = i.color.rgb;
                fixed3 color = lerp(tint * .45, tint, saturate(trail * 1.5 + lines));
                color = lerp(color, fixed3(1, 1, 1), saturate(core+spine*.8));
                return fixed4(color, alpha * fade);
            }
            ENDCG
        }
    }
}
