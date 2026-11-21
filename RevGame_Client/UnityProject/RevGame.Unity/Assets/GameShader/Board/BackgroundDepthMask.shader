// 只为同一相机提前写深度。现有章节背景通过 ZTest 在不透明格芯后面被提前淘汰。
Shader "Hidden/RevGame/Board/BackgroundDepthMask"
{
    Properties
    {
        _MainTex ("棋盘图集（A 通道为覆盖率）", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Cull Off
            ZTest LEqual
            ZWrite On
            ColorMask 0

            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            // 使用棋盘格的内芯 UV。此纹理由棋盘实际生效的 Sprite 提供。
            sampler2D _MainTex;

            // 合并后的顶点已位于背景前的深度平面，UV 仍对应原 Sliced 格芯。
            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            // 裁剪空间坐标与实际图集 UV，交给片元确认覆盖率。
            struct Varyings
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            // 与其他场景物体使用同一相机的投影，窗口和镜头缩放随相机变化。
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            // 严格要求实际采样 alpha 为 1。透明、柔边和图集压缩误差都不应该替背景挡写深度。
            fixed4 Frag(Varyings input) : SV_Target
            {
                float alpha = tex2D(_MainTex, input.uv).a;
                clip(alpha - 1.0);
                return 0;
            }
            ENDCG
        }
    }
    Fallback Off
}
