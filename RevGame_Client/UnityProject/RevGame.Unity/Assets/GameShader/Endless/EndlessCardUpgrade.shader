// 一份共享材质画拖牌锁定与升级爆发。uv2.x为时间/进度，uv2.y为模式；不采样棋子图集，不能污染其UV。
Shader "RevGame/Endless/CardUpgrade"
{
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            struct app
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 data : TEXCOORD1;
                fixed4 color : COLOR;
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 data : TEXCOORD1;
                fixed4 color : COLOR;
            };
            v2f vert(app v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.data = v.data;
                o.color = v.color;
                return o;
            }
            float ring(float radius, float at, float width)
            {
                return 1-smoothstep(width, width*2, abs(radius-at));
            }
            fixed4 frag(v2f i) : SV_Target
            {
                float2 p = i.uv*2-1;
                float radius = length(p);
                float angle = atan2(p.y,p.x);
                float t = i.data.x;
                float alpha;
                float white;
                if (i.data.y < .5)
                {
                    // 锁定双环与四角短光标，中心只留弱光，玩家仍能辨认棋子原画。
                    float pulse = .72+.28*sin(t*8);
                    float corners = pow(saturate(cos(angle*4)),12)*ring(radius,.80,.08);
                    alpha = (ring(radius,.66,.04)+ring(radius,.77,.024)*.7+corners)*pulse;
                    alpha += (1-smoothstep(.1,.55,radius))*.12;
                    white = corners;
                }
                else
                {
                    // 大爆发先扩散再淡出；光柱和星芒与环合成单Pass，既醒目也避免叠一堆透明渲染器。
                    float life = smoothstep(0,.06,t)*(1-smoothstep(.58,1,t));
                    float shock = ring(radius,lerp(.12,.9,saturate(t*2.2)),.025);
                    float inner = ring(radius,.34+.04*sin(t*17),.035);
                    float rays = pow(saturate(cos(angle*8+t*7)),20)*(1-smoothstep(.2,.92,radius));
                    float column = (1-smoothstep(.035,.14,abs(p.x)))*smoothstep(-.15,.1,p.y)*(1-smoothstep(.6,1,p.y));
                    float core = (1-smoothstep(.015,.24,radius))*(1-smoothstep(.08,.45,t));
                    alpha = (shock+inner*.8+rays*.7+column*.7+core)*life;
                    white = core+column*.6+rays*.4;
                }
                return fixed4(lerp(i.color.rgb,fixed3(1,1,.96),saturate(white)),saturate(alpha)*i.color.a);
            }
            ENDCG
        }
    }
}
