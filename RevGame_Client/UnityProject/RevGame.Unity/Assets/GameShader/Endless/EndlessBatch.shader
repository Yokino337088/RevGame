Shader "RevGame/Endless/Horde"
{
    Properties
    {
        _MainTex("共享图集", 2D) = "white" {}
        [HideInInspector] _HordeBlend("敌潮本段插值比例", Float) = 1
    }
    SubShader
    {
        // 端点与四角偏移在竞技场局部坐标，阻止引擎预先变换 Position 破坏相同坐标约定。
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "DisableBatching"="True" }
        Blend SrcAlpha OneMinusSrcAlpha Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ REVGAME_HORDE_GPU_INTERPOLATION
            #include "UnityCG.cginc"

            // 身体共用的图集页，使用 CPU 或 GPU 时均从同一份 UV 采样。
            sampler2D _MainTex;
#ifdef REVGAME_HORDE_GPU_INTERPOLATION
            // 显示时钟已经限制到 0～1。由当前竞技场每呈现帧写入，同一段位置样本无需重复上传。
            float _HordeBlend;
#endif

            // 由池化网格按原透明顺序提供一张四边形顶点。
            struct app
            {
                // CPU 路径存四角坐标，GPU 路径存中心起点。Z 沿用原深度。
                float4 vertex : POSITION;
                // 敌人当前动画帧在共享图集页上的坐标。
                float2 uv : TEXCOORD0;
                // 颜色用于敌人类别和受击状态的外观。
                fixed4 color : COLOR;
#ifdef REVGAME_HORDE_GPU_INTERPOLATION
                // XY 是目标中心，ZW 是四角相对中心的偏移。各值均为竞技场局部棋格单位。
                float4 endpoint : TEXCOORD1;
#endif
            };

            // 交给原图集片元 Pass 的坐标、UV 和调色。
            struct v2f
            {
                // 实际提交给设备光栅化的裁剪坐标。
                float4 vertex : SV_POSITION;
                // 保持图集矩形的原插值。
                float2 uv : TEXCOORD0;
                // 将棋子的只读调色传给片元。
                fixed4 color : COLOR;
            };

            // 根据选定布局解析局部四角。结束帧直接使用目标坐标，与 CPU 路径的端点规则相同。
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

            // 按原图集与调色合成透明像素，插值方案只改变顶点的计算位置。
            fixed4 frag(v2f i) : SV_Target
            {
                return tex2D(_MainTex, i.uv) * i.color;
            }
            ENDCG
        }
    }
}
