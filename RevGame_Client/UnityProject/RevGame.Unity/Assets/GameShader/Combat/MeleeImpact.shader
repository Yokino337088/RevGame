// Kenney透明斩弧/冲击环保留原轮廓；流光和盾面六边纹由共享Shader叠加，不生成每次命中的材质。
Shader "RevGame/Combat/MeleeImpact"
{
    Properties
    {
        [PerRendererData] _MainTex ("原画图集", 2D) = "white" {}
        [PerRendererData] _SpriteUV ("图集UV", Vector) = (0,0,1,1)
        [PerRendererData] _Progress ("本次动画进度", Float) = 0
        _Color ("染色", Color) = (1,1,1,1)
        _Glow ("职业光色", Color) = (0.3,0.8,1,1)
        _Shield ("盾击", Float) = 0
        [PerRendererData] _Pattern ("棋种范围模式，0保留原近战", Float) = 0
        [PerRendererData] _PatternCells ("范围边长格数", Float) = 1
        [HideInInspector] _RendererColor ("RendererColor", Color) = (1,1,1,1)
        [HideInInspector] _Flip ("Flip", Vector) = (1,1,1,1)
        [PerRendererData] _AlphaTex ("Alpha", 2D) = "white" {}
        [PerRendererData] _EnableExternalAlpha ("External Alpha", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "CanUseSpriteAtlas"="True" }
        Cull Off ZWrite Off Lighting Off Blend One OneMinusSrcAlpha
        Pass
        {
            CGPROGRAM
            #pragma vertex SpriteVert
            #pragma fragment Frag
            #pragma multi_compile _ ETC1_EXTERNAL_ALPHA
            #include "UnitySprites.cginc"
            float4 _SpriteUV; float _Progress, _Shield, _Pattern, _PatternCells; fixed4 _Glow;
            fixed4 Frag(v2f i) : SV_Target
            {
                float2 uv = (i.texcoord-_SpriteUV.xy)/max(_SpriteUV.zw-_SpriteUV.xy,.00001);
                float2 p = uv*2-1;
                if (_Pattern > .5)
                {
                    // 局部四边形内程序绘制，复用原项目亮芯、暗缘和衰减波纹风格，不增加全屏透明层。
                    float r = length(p);
                    float wave = 1-smoothstep(.035,.1,abs(r-lerp(.12,.9,_Progress)));
                    float core = saturate(1-r*3)*(1-_Progress);
                    float a = wave*.65+core;
                    half3 color = half3(.65,.85,1);
                    if (_Pattern > 6.5 && _Pattern < 7.5)
                    {
                        // 火法：方形外缘与逐格网线明确3×3边界，中心格更亮，对应规则中心倍率。
                        float edge = 1-smoothstep(.025,.06,abs(max(abs(p.x),abs(p.y))-.94));
                        float2 cell = abs(frac(uv*_PatternCells)-.5);
                        float grid = smoothstep(.43,.49,max(cell.x,cell.y));
                        a = edge*.75+grid*.25+wave*.5+core;
                        color = lerp(half3(1,.18,.025),half3(1,.85,.25),core);
                    }
                    else if (_Pattern > 5.5 && _Pattern < 6.5)
                    {
                        float ribbon = abs(p.y-sin(uv.x*18-_Progress*12)*.22);
                        a = 1-smoothstep(.05,.2,ribbon);
                        color = half3(.2,.8,1);
                    }
                    else if (_Pattern > 4.5 && _Pattern < 5.5)
                    {
                        a = (1-smoothstep(.03,.12,abs(p.y)))*(.55+.45*sin(uv.x*30-_Progress*20));
                        color = half3(1,.65,.15);
                    }
                    else if (_Pattern > 7.5 && _Pattern < 8.5)
                    {
                        a += (1-smoothstep(.07,.15,min(abs(p.x),abs(p.y))))*saturate(1-r);
                        color = half3(.45,1,.55);
                    }
                    else if (_Pattern > 8.5)
                    {
                        a = (1-smoothstep(.03,.12,min(abs(p.x-p.y),abs(p.x+p.y))))*saturate(1-r);
                        color = half3(.85,.3,1);
                    }
                    else if (_Pattern > 2.5 && _Pattern < 3.5)
                    {
                        a = wave*smoothstep(-.25,.2,p.x)+core;
                        color = half3(1,.55,.2);
                    }
                    else if (_Pattern > 1.5 && _Pattern < 2.5)
                    {
                        float hex = max(abs(p.y),abs(p.x)*.866+abs(p.y)*.5);
                        a = 1-smoothstep(.025,.08,abs(hex-.7));
                        color = half3(.35,.75,1);
                    }
                    else if (_Pattern > 3.5)
                    {
                        a = wave+(1-smoothstep(.025,.07,min(abs(p.x),abs(p.y))))*.4;
                        color = half3(1,.9,.35);
                    }
                    a = saturate(a)*i.color.a;
                    return fixed4(color*a,a);
                }
                float age = _Progress * .34;
                fixed4 art = SampleSpriteTexture(i.texcoord);
                float sweep = pow(saturate(sin(uv.y*15-age*35)),6);
                float ring = pow(saturate(1-abs(length(p)-frac(age*2.4))/.09),2);
                float flash = saturate(1-age*8);
                float alpha = art.a;
                if (_Shield > .5)
                {
                    float hex = max(abs(p.y),abs(p.x)*.866+abs(p.y)*.5);
                    float edge = 1-smoothstep(.025,.055,abs(hex-.65));
                    alpha = max(alpha, (edge*.9 + ring*.7 + flash*.45) * (1-smoothstep(.8,1,length(p))));
                }
                fixed4 c = fixed4(lerp(_Glow.rgb,half3(1,1,1),sweep*.7+flash*.4),alpha)*i.color;
                c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
