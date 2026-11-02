// 火、水、圣光使用局部Sprite四边形的一次透明绘制，复用图集载体，不增加粒子系统、GrabPass或全屏后处理。
// 动画只读取离散_Progress，禁止使用_Time偷偷每帧推进；棋盘轮询提交哪一帧，GPU就画哪一帧。
Shader "RevGame/Combat/ElementalImpact"
{
    Properties
    {
        _FireBorder ("火焰范围方框", Range(0,1)) = 1
        _Opacity ("特效透明度", Range(0,1)) = 1
        [PerRendererData] _MainTex ("已有图集载体", 2D) = "white" {}
        [PerRendererData] _SpriteUV ("图集矩形", Vector) = (0,0,1,1)
        [PerRendererData] _Progress ("离散关键帧进度", Float) = 0
        [PerRendererData] _PatternCells ("真实爆区边长格数", Float) = 3
        _Element ("元素：0火焰 1水浪 2圣光", Float) = 0
        _Color ("精灵染色", Color) = (1,1,1,1)
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Cull Off ZWrite Off Lighting Off
        // 预乘透明保留深红/深蓝边缘，亮色棋盘上也能辨认轮廓；不依赖Bloom才能看见亮芯。
        Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment Frag
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            float4 _SpriteUV;
            float _Progress, _PatternCells, _Element, _FireBorder, _Opacity;

            // 这些常数仅是美术的宽度、颜色与衰减曲线，不参与伤害、治疗或攻击时钟，因此不进策划表。
            float Ring(float radius, float center, float width)
            {
                return 1-smoothstep(width*.45,width,abs(radius-center));
            }

            float Spark(float2 position, float2 center, float size)
            {
                float2 d=abs(position-center);
                return saturate(1-min(d.x*3+d.y,d.x+d.y*3)/size);
            }

            half4 Fire(float2 uv, float t)
            {
                float2 p=uv*2-1;
                float radius=length(p);
                float rise=saturate(t*5);
                float life=saturate((1-t)*1.65);
                // 两条不同频率的横向火舌推动顶缘；不用多层噪声纹理或逐像素循环。
                float tongues=sin(p.x*12+t*17)*.16+sin(p.x*25-t*11)*.075;
                float top=lerp(-.45,.88,rise)+tongues;
                float base=smoothstep(-1,-.72,p.y);
                float crown=1-smoothstep(top-.20,top,p.y);
                float taper=saturate((top-p.y)*.8+.15);
                float sway=sin(p.y*7-t*16)*.065;
                float body=(1-smoothstep(.48*taper,.90*taper,abs(p.x+sway)))*base*crown*life;
                float inner=(1-smoothstep(.10*taper,.47*taper,abs(p.x-sway)))*base*crown*life;
                float hot=inner*inner;
                float shock=Ring(radius,lerp(.12,.95,saturate(t*2.2)),.075)*saturate(1-t*1.3);
                float flash=saturate(1-radius*2.3)*saturate(1-t*4.5);
                float embers=Spark(p,float2(-.55+sin(t*7)*.08,lerp(-.3,.88,t)),.11);
                embers+=Spark(p,float2(.48+sin(t*5+1)*.1,lerp(-.5,.75,t)),.085);
                // 方形边界仍对应真实3×3；主体火焰与亮芯抢眼，细格线只辅助辨认爆区。
                float border=1-smoothstep(.018,.055,abs(max(abs(p.x),abs(p.y))-.95));
                float2 cell=abs(frac(uv*_PatternCells)-.5);
                float grid=smoothstep(.45,.49,max(cell.x,cell.y))*.16;
                float ground=(border*.7+grid)*life*_FireBorder;
                float alpha=saturate(body*.94+shock*.8+flash+embers*life+ground*.48);
                half3 color=lerp(half3(.55,.025,.005),half3(1,.22,.015),saturate(body*1.5));
                color=lerp(color,half3(1,.78,.08),saturate(inner+shock*.75));
                color=lerp(color,half3(1,1,.78),saturate(hot+flash));
                return half4(color,alpha);
            }

            half4 Water(float2 uv, float t)
            {
                float2 p=uv*2-1;
                float sway=sin(uv.x*14-t*13)*.2;
                float distance=abs(p.y-sway);
                float ends=smoothstep(0,.10,uv.x)*(1-smoothstep(.90,1,uv.x));
                float band=(1-smoothstep(.19,.58,distance))*ends;
                float core=(1-smoothstep(.045,.20,distance))*ends;
                float crest=1-smoothstep(.025,.10,abs(p.y-sway-.24));
                float foam=crest*ends*(.55+.45*sin(uv.x*25-t*18));
                float tail=1-smoothstep(.025,.10,abs(p.y+sway*.8+.22));
                float impact=Ring(length((p-float2(.65,0))*float2(.8,1.3)),lerp(.1,.6,t),.065);
                float drops=Spark(p,float2(.12,lerp(.1,.8,t)),.09)+Spark(p,float2(.65,lerp(.1,-.8,t)),.09);
                float alpha=saturate(band*.88+foam*.8+tail*ends*.4+impact*.55+drops);
                half3 color=lerp(half3(.015,.12,.48),half3(.04,.65,1),band);
                color=lerp(color,half3(.78,1,1),saturate(core*.8+foam+impact*.7));
                return half4(color,alpha*saturate((1-t)*3));
            }

            half4 Holy(float2 uv, float t)
            {
                float2 p=uv*2-1;
                float2 ground=(p-float2(0,-.35))*float2(1,2.2);
                float radius=length(ground);
                float ring=Ring(radius,.62,.06)+Ring(radius,.85,.035)*.7;
                float pulse=.75+.25*sin(t*12);
                float beam=(1-smoothstep(.13,.40,abs(p.x)))*smoothstep(-.65,-.30,p.y)*(1-smoothstep(.7,1,p.y));
                float pillar=(1-smoothstep(.025,.12,abs(p.x)))*smoothstep(-.55,-.30,p.y)*(1-smoothstep(.65,.95,p.y));
                float2 star=p-float2(0,.28);
                float cross=(1-smoothstep(.055,.11,min(abs(star.x),abs(star.y))))*(1-smoothstep(.20,.45,max(abs(star.x),abs(star.y))));
                float small=Spark(p,float2(-.55,lerp(-.45,.55,t)),.13)+Spark(p,float2(.55,lerp(-.20,.80,t)),.10);
                float alpha=saturate(ring*.85*pulse+beam*.55+pillar*.6+cross+small);
                half3 color=lerp(half3(.18,.40,.11),half3(1,.70,.12),saturate(ring+beam));
                color=lerp(color,half3(1,1,.86),saturate(pillar+cross+small));
                return half4(color,alpha*saturate(t*7)*saturate((1-t)*3));
            }

            fixed4 Frag(v2f input) : SV_Target
            {
                float2 uv=(input.texcoord-_SpriteUV.xy)/max(_SpriteUV.zw-_SpriteUV.xy,.00001);
                half4 color;
                if (_Element < .5)
                {
                    color=Fire(uv,_Progress);
                }
                else if (_Element < 1.5)
                {
                    color=Water(uv,_Progress);
                }
                else
                {
                    color=Holy(uv,_Progress);
                }
                // 材质、Sprite图集和对象颜色继续遵守SpriteRenderer契约；只在最终输出做一次预乘。
                color*=input.color;
                color.a*=_Opacity;
                color.rgb*=color.a;
                return color;
            }
            ENDCG
        }
    }
}
