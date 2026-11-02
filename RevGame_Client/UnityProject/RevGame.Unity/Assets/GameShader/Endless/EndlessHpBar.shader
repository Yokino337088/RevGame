// ============================================================
// EndlessHpBar —— 外围敌潮的头顶血条（新手先读这段）
//
// 外围敌人是批量网格画的，没有独立的 PieceView 血条，所以另用一张动态网格画血条，整场 1 次绘制调用。
// 每只受伤敌人一个细长方块：uv.x 从左到右 0~1；color.rgb 是血条颜色（表现层按血量从绿到红插值），
// color.a 存“剩余血量比例”。满血的敌人不画（吸血鬼幸存者同样只在受伤后显示血条），屏幕更干净。
// 不采样贴图，纯程序绘制：深色描边 + 深色底槽 + 彩色血量 + 顶部高光。
// ============================================================
Shader "RevGame/Endless/HpBar"
{
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
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; fixed4 color:COLOR; };
            v2f vert(app v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }
            fixed4 frag(v2f i) : SV_Target
            {
                // 描边宽度：横向 6%、纵向 22%（方块很扁，纵向需要更大的比例才看得出描边）。
                float inner = step(.06, i.uv.x) * step(i.uv.x, .94) * step(.22, i.uv.y) * step(i.uv.y, .78);
                float ratio = i.color.a;
                float x = (i.uv.x - .06) / .88;
                float filled = inner * step(x, ratio);
                float highlight = step(.55, i.uv.y) * .25;
                fixed3 slot = fixed3(.12, .07, .06);
                fixed3 frame = fixed3(.05, .03, .02);
                fixed3 color = lerp(frame, slot, inner);
                color = lerp(color, i.color.rgb + highlight, filled);
                return fixed4(color, .92);
            }
            ENDCG
        }
    }
}
