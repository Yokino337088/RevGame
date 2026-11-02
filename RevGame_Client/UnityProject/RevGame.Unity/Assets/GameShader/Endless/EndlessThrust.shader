// ============================================================
// EndlessThrust —— 刺客直线突刺（新手先读这段）
//
// 参考常见的动作游戏“突刺/穿刺”表现：一个极亮的尖头沿直线飞速扎出，身后拖着被拉长的速度残影和细速度线，
// 尖头扎到线尾时爆一下闪光，然后整条残影迅速收细消失。整个过程约 0.4 秒。
// 所有刺客的突刺画在一张动态网格里，一次突刺一个沿攻击线摆放的长方块，整场 1 次绘制：
//   uv.x  从刺客（0）到线尾（1）；uv.y 横跨线宽（0~1，中线 0.5）；
//   uv2.x 进度（0~1，表现层按时间推进）；color.rgb 主色（紫影），color.a 整体强度。
// 残影形状用 Kenney CC0 的 trace_01 速度线贴图（已转成横向），尖头和闪光由 Shader 程序生成。
// ============================================================
Shader "RevGame/Endless/Thrust"
{
    Properties
    {
        _BladeTex ("长型剑气刃光（Kenney CC0）", 2D) = "white" {}
        _TrailTex ("速度残影（非Sprite，钳制采样）", 2D) = "white" {}
        _TipLength ("尖头长度（占整条线的比例）", Range(.03, .3)) = .11
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
            float _TipLength;
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
                // 前 55% 时间尖头从起点扎到线尾，之后停在线尾、残影收尾。
                float head = saturate(progress / .55);
                float fade = 1 - saturate((progress - .55) / .45);
                float x = i.uv.x;
                float y = abs(i.uv.y - .5) * 2;
                // 尖头：线尾方向变尖的三角形，最前端最亮。
                float behind = head - x;
                float tipT = saturate(behind / _TipLength);
                float tip = step(0, behind) * step(behind, _TipLength) * step(y, tipT * .9);
                // 残影：只在尖头身后，越靠近尖头越亮；收尾时整体变细。
                float trailMask = step(0, behind - _TipLength * .5) * step(x, head);
                float along = saturate(1 - (behind - _TipLength * .5) / max(.05, head));
                float thin = lerp(1, .35, 1 - fade);
                float trail = tex2D(_TrailTex, float2(saturate(along), saturate(.5 + (i.uv.y - .5) / thin))).a * trailMask * along;
                // 速度线：几条贴着中线的细亮线，随时间向前滚动。
                float lines = pow(saturate(1 - abs(frac(i.uv.y * 5 + .5) - .5) * 14), 2) * step(y, .8) * trailMask * along * .5;
                lines *= step(.5, frac(x * 6 - progress * 10));
                // 扎到线尾的闪光：尖头抵达后在线尾爆开再淡出。
                float arrive = saturate((progress - .45) / .15) * fade;
                float flash = (1 - smoothstep(0, .18, length(float2((x - 1) * 4, i.uv.y - .5)))) * arrive;
                float core = saturate(tip * 1.2 + flash);
                // 长型刃光沿整条攻击线展开，边缘透明，亮芯与三条细束构成真正的剑气。
                float reveal = 1-smoothstep(head-.06,head+.025,x);
                float blade = tex2D(_BladeTex,float2(x,i.uv.y)).a * reveal;
                float spine = (1-smoothstep(.015,.12,y))*smoothstep(0,.08,x)*(1-smoothstep(.92,1,x))*reveal;
                float alpha = saturate(tip + trail * .7 + lines + flash + blade*.8 + spine*.9) * i.color.a * (progress < 1 ? 1 : 0);
                fixed3 tint = i.color.rgb;
                fixed3 color = lerp(tint * .45, tint, saturate(trail * 1.5 + lines));
                color = lerp(color, fixed3(1, 1, 1), saturate(core+spine*.8));
                return fixed4(color, alpha * max(fade, tip));
            }
            ENDCG
        }
    }
}
