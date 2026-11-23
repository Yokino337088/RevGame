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
    Properties
    {
        [HideInInspector] _HordeBlend("敌潮本段插值比例", Float) = 1
    }
    SubShader
    {
        // 两份轨迹均使用竞技场局部坐标，保持身体和血条同一坐标空间。
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ REVGAME_HORDE_GPU_INTERPOLATION
            #include "UnityCG.cginc"
#ifdef REVGAME_HORDE_GPU_INTERPOLATION
            // 由竞技场按当前呈现时间送来的 0～1 进度，身体与血条共用。
            float _HordeBlend;
#endif

            // 网格提供的血条四边形。颜色 Alpha 始终存血量比例。
            struct app
            {
                // CPU 的四角坐标或 GPU 的中心起点。Z 仍为血条原层位。
                float4 vertex : POSITION;
                // 0～1 的矩形坐标，用于绘制描边和生命槽。
                float2 uv : TEXCOORD0;
                // RGB 为血条填色，Alpha 为实际剩余生命比例。
                fixed4 color : COLOR;
#ifdef REVGAME_HORDE_GPU_INTERPOLATION
                // XY 为目标中心，ZW 为相对中心的角偏移，均用竞技场局部棋格。
                float4 endpoint : TEXCOORD1;
#endif
            };

            // 光栅化并绘制健康条需要的逐顶点数据。
            struct v2f
            {
                // 实际绘制的裁剪坐标。
                float4 vertex : SV_POSITION;
                // 血槽内部位置。
                float2 uv : TEXCOORD0;
                // 生命比例和血槽色。
                fixed4 color : COLOR;
            };

            // 先沿已提交的轨迹插出中心，再展开四角，避免血条在身体移动时漂移。
            v2f vert(app v)
            {
#ifdef REVGAME_HORDE_GPU_INTERPOLATION
                float2 center = _HordeBlend < 1 ? lerp(v.vertex.xy, v.endpoint.xy, _HordeBlend) : v.endpoint.xy;
                v.vertex.xy = center + v.endpoint.zw;
#endif
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }
            // 按输入的血量比例保留已有描边、槽底和高光。
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
