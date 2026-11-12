Shader "RevGame/UI/Duel Preparation Rift"
{
    Properties
    {
        [PerRendererData] _MainTex ("Backdrop sprite", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="True" }
        Stencil { Ref [_Stencil] Comp [_StencilComp] Pass [_StencilOp] ReadMask [_StencilReadMask] WriteMask [_StencilWriteMask] }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct Input { float4 position : POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; };
            struct Pixels { float4 position : SV_POSITION; fixed4 color : COLOR; float2 uv : TEXCOORD0; float2 local : TEXCOORD1; };
            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;

            // Sprite UV 只采样图集底图，法阵使用局部坐标，重打图集不会漂移。
            Pixels vert(Input input)
            {
                Pixels output;
                output.position = UnityObjectToClipPos(input.position);
                output.color = input.color * _Color;
                output.uv = input.uv;
                output.local = input.position.xy;
                return output;
            }

            // 软边线和符文外的淡光使用同一半径，以较少的数学运算替代单独粒子层。
            float InkRing(float distanceToCenter, float radius, float width)
            {
                return 1.0 - smoothstep(width * .32, width, abs(distanceToCenter - radius));
            }

            // 只给两翼加光，中央始终留给文字。左右法阵方向相反，色彩沿用席位标志。
            fixed4 frag(Pixels input) : SV_Target
            {
                fixed4 baseArt = tex2D(_MainTex, input.uv) * input.color;
                float2 xy = input.local / float2(960.0, 540.0);
                float signOfSeat = xy.x < 0.0 ? -1.0 : 1.0;
                float time = _Time.y;
                float2 circle = float2((abs(xy.x) - .61) * 1.68, xy.y + .025);
                float radius = length(circle);
                float angle = atan2(circle.y, circle.x);
                float spin = angle * .15915494 + time * .115 * signOfSeat;
                float arc = smoothstep(.18, .85, .5 + .5 * cos(spin * 12.566371));
                float spokes = smoothstep(.84, .99, .5 + .5 * sin(spin * 150.79645));
                float inner = InkRing(radius, .364, .008) * (.24 + .76 * arc);
                float outer = InkRing(radius, .424, .011) * (.16 + .84 * (1.0 - arc));
                float marks = InkRing(radius, .402, .019) * spokes;
                float aura = InkRing(radius, .394, .096) * (.22 + .26 * arc);
                float diagonal = frac(xy.x * .68 - xy.y * .39 - time * .105);
                float trail = InkRing(diagonal, .52, .035) * smoothstep(.45, .95, abs(xy.x));
                float2 grid = (xy + float2(0, time * .11)) * float2(23, 16);
                float scatter = frac(sin(dot(floor(grid), float2(91.27, 17.93))) * 43758.54);
                float2 particle = frac(grid) - .5;
                float spark = (1.0 - smoothstep(.035, .14, length(particle))) * step(.968, scatter);
                float edgeMask = smoothstep(.22, .48, abs(xy.x));
                float3 tint = lerp(float3(1.0, .26, .11), float3(.10, .91, 1.0), step(0.0, xy.x));
                float power = edgeMask * (.53 * inner + .65 * outer + .53 * marks + .20 * aura + .31 * trail + .75 * spark);
                baseArt.rgb = saturate(baseArt.rgb + tint * power * input.color.a);
                #ifdef UNITY_UI_CLIP_RECT
                baseArt.a *= UnityGet2DClipping(input.local, _ClipRect);
                #endif
                return baseArt;
            }
            ENDCG
        }
    }
}
