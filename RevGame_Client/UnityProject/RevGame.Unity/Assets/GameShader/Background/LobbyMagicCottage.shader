// 小屋原画的局部魔法动效：RGB遮罩分别限定天空/水面/晶石药瓶发光，屋体与人物站台保持稳定。
Shader "RevGame/Background/LobbyMagicCottage"
{
    Properties
    {
        _ChapterTex ("大厅小屋原画", 2D) = "white" {}
        _RegionMask ("R天空G水面B发光遮罩", 2D) = "black" {}
        _NatureMask ("R草木G小屋B烟囱遮罩", 2D) = "black" {}
        _WaterAmount ("水面折射幅度", Range(0,.015)) = .006
        _WaterSpeed ("水波速度", Range(0,3)) = .8
        _WaterLight ("水波亮纹强度", Range(0,1)) = .32
        _WindAmount ("草木摆动幅度", Range(0,.012)) = .0045
        _WindSpeed ("风速", Range(0,3)) = 1.2
        _MagicGlow ("魔法晶石柔光", Range(0,1)) = .55
        _CottageMagic ("小屋魔法扫光与光点", Range(0,1)) = .38
        _SmokeAmount ("烟囱烟雾摆动", Range(0,.01)) = .003
        _SkyGlow ("天空日照变化", Range(0,.15)) = .025
        _EffectTime ("时钟 -1跟随Unity", Float) = -1
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _ChapterTex, _RegionMask, _NatureMask;
            float _WaterAmount, _WaterSpeed, _WaterLight, _WindAmount, _WindSpeed;
            float _MagicGlow, _CottageMagic, _SmokeAmount, _SkyGlow, _EffectTime;
            struct app { float4 vertex:POSITION; float2 uv:TEXCOORD0; };
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(app v)
            {
                v2f o;
                o.vertex=UnityObjectToClipPos(v.vertex);
                o.uv=v.uv;
                return o;
            }
            fixed4 frag(v2f i):SV_Target
            {
                float t=_EffectTime>=0?_EffectTime:_Time.y;
                float3 mask=tex2D(_RegionMask,i.uv).rgb;
                float3 nature=tex2D(_NatureMask,i.uv).rgb;
                // 两个方向的周期波叠加：频率/速度不同才能看见交叉波纹，而不是整池平移。
                // 使用现有G遮罩，睡莲与岸线不随水采样偏移；参数为UV幅度，0关闭该效果。
                float waterTime=t*_WaterSpeed;
                float wave1=i.uv.x*85+i.uv.y*57-waterTime*2.1;
                float wave2=i.uv.x*-41+i.uv.y*103+waterTime*1.6;
                float2 wave=float2(cos(wave1)*.7+cos(wave2)*.3,sin(wave1)*.25+sin(wave2)*.4);
                float2 uv=i.uv;
                uv+=wave*_WaterAmount*mask.g;
                // 植物风动只偏移绿色区域，低频阵风+高频叶片细摆；空间相位使不同草丛不同时摇摆。
                float wind=t*_WindSpeed;
                float gust=sin(i.uv.x*19+i.uv.y*7-wind)*.65+sin(i.uv.y*71+wind*2.2)*.35;
                uv.x+=gust*_WindAmount*nature.r;
                uv.y+=sin(i.uv.x*33-wind*1.3)*_WindAmount*.18*nature.r;
                // 烟雾从烟囱向上漂，B遮罩限制屋顶保持稳定，不重绘整栋房子。
                uv.x+=sin(i.uv.y*49-t*.7)*_SmokeAmount*nature.b;
                fixed3 color=tex2D(_ChapterTex,saturate(uv)).rgb;
                // 用波形导数形成窄亮纹，加扩散圆波，提升水塘辨识度；避免只有透明UV偏移看不出动效。
                float ridge=pow(saturate(.5+.5*sin(wave1+sin(wave2)*.55)),12);
                float2 pond=(i.uv-float2(.80,.095))*float2(1.78,1);
                float rings=pow(saturate(.5+.5*cos(length(pond)*175-waterTime*2.4)),18);
                color+=fixed3(.45,.78,.92)*(ridge*.75+rings*.45)*mask.g*_WaterLight;
                color*=1+gust*nature.r*.025;
                // 原画晶石的轮廓保留，颜色沿遮罩呼吸；窗边周期扫光与稀疏光点构成魔法小屋氛围。
                color+=fixed3(.25,.9,.48)*mask.b*(.58+.42*sin(t*1.4))*_MagicGlow;
                float scan=pow(saturate(.5+.5*sin(i.uv.x*11+i.uv.y*13-t*.6)),16);
                color+=fixed3(.25,.62,.37)*nature.g*scan*_CottageMagic*.42;
                float2 grid=i.uv*float2(38,22);
                float2 cell=floor(grid);
                float seed=frac(sin(dot(cell,float2(127.1,311.7)))*43758.5453);
                float2 local=frac(grid)-float2(.5+sin(t*.35+seed*6)*.16,.5+cos(t*.4+seed*5)*.19);
                float spark=(1-smoothstep(.012,.075,length(local)))*step(.78,seed);
                spark*=pow(saturate(.5+.5*sin(t*1.3+seed*17)),3);
                color+=fixed3(.68,1,.58)*spark*nature.g*_CottageMagic;
                color+=fixed3(1,.88,.6)*mask.r*_SkyGlow*(.5+.5*sin(uv.x*7+t*.18));
                return fixed4(saturate(color),1);
            }
            ENDCG
        }
    }
}
