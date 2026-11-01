// 无尽棋盘：沿用第六章神殿棋盘的语言（象牙底、金色细线、菱形符文、法阵光环、圣辉流光），完全不透明。
//
// 【为什么单独写一个，而不是直接用 Chapter06_Temple】
// 六章棋盘是 81 个独立格子对象，每格一个 Sprite；无尽为了省绘制，整个棋盘只画一张面片，
// 所以这里把“当前在第几格”用 UV 算出来，再复用同样的图案公式。
//
// 【棋盘特效的数据从哪来】
// _CellFx[81]：每格亮度，C# 每帧上传。Boss 预警时格子持续发亮，命中时一闪再灭。
// _Shock：一道从命中点向外扩散的冲击波。两者都只影响画面，不参与任何伤害判定。
Shader "RevGame/Endless/StarGrid"
{
    Properties
    {
        [HideInInspector] _Clock("战斗时钟",Float)=0
        [HideInInspector] _Theme("巡回主题",Float)=0
        [HideInInspector] _WarningLine("预警行",Float)=-10
        [HideInInspector] _Heart("棋心比例",Float)=1
        [HideInInspector] _SkillTint("技能主色",Color)=(1,.3,.2,1)
        _Ivory("象牙底色",Color)=(.95,.91,.81,1)
        _Gold("金线颜色",Color)=(.70,.51,.24,1)
        _Glow("圣辉颜色",Color)=(1,.80,.36,1)
    }
    SubShader
    {
        // 不透明、写深度：背景不会透过棋盘，也不需要混合，比半透明便宜。
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Blend Off Cull Off ZWrite On
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            float _Clock, _Theme, _WarningLine, _Heart, _WarningMode;
            float4 _GateWarning, _BreakCell, _Shock;
            fixed4 _Ivory, _Gold, _Glow, _SkillTint;
            // 每格亮度，下标 = y * 9 + x，由 EndlessArena 写入。
            float _CellFx[81];
            struct v2f { float4 vertex:SV_POSITION; float2 uv:TEXCOORD0; };
            v2f vert(appdata_base v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.uv=v.texcoord; return o; }

            // 画一条抗锯齿细线：距离 d 越接近 0 越亮，fwidth 让线宽不随分辨率抖动。
            float Thin(float d, float width) { return 1-smoothstep(width, width+max(fwidth(d),.002), abs(d)); }

            fixed4 frag(v2f i):SV_Target
            {
                float2 cell=i.uv*9;                 // 0~9 的棋盘坐标
                float2 id=floor(cell);              // 第几格
                float2 p=frac(cell)-.5;             // 格内坐标，格子中心是 (0,0)
                float t=_Clock;
                float radius=length(p);
                float angle=atan2(p.y,p.x);

                // —— 以下图案公式和 Chapter06_Temple 一致，保证风格统一 ——
                float breath=.64+.36*sin(t*.85);                                              // 缓慢呼吸
                float ray=pow(saturate(.5+.5*cos(angle*8-t*.12)),14)*smoothstep(.12,.22,radius)*(1-smoothstep(.38,.49,radius));
                float halo=Thin(radius-.29,.006)*(.53+.47*sin(angle*8-t*.23));                 // 法阵光环
                float diamond=Thin(abs(p.x)+abs(p.y)-.36,.005);                                // 菱形符文
                float shaft=pow(saturate(.5+.5*sin(cell.x*.42-cell.y*.68-t*.3)),8);            // 斜向流光
                float edge=max(abs(p.x),abs(p.y));
                float grid=smoothstep(.465,.49,edge);                                          // 格线
                float rim=smoothstep(.38,.47,edge)*(1-grid);                                   // 格子边缘微光
                float light=ray*.34+halo*.30+diamond*.16+shaft*.20+rim*(.16+breath*.08);

                // 象牙大理石底：用两层正弦扰动做极淡的纹理，不用贴图，零额外内存。
                float marble=sin(cell.x*2.2+sin(cell.y*1.8))*.012+sin(cell.y*5.1+cell.x*.7)*.006;
                float3 color=_Ivory.rgb+marble;
                // 棋盘格明暗：相邻格差一点点，玩家更容易数格子。
                color*=fmod(id.x+id.y,2)<1?1:.965;
                color=lerp(color,_Gold.rgb,grid*.78);
                color+=_Glow.rgb*light*breath*.55;

                // 中央棋心格：金色聚光，受伤时转红提示危险。
                float core=pow(saturate(1-length(cell-4.5)*1.1),2);
                color=lerp(color,float3(1,.80,.36),core*(.42+.08*sin(t*2)));
                color+=float3(1,.08,.02)*core*(1-step(.3,_Heart));

                // 门点预警、破眼预警：沿用原有的朱红色。
                float warn=1-step(.5,abs(id.y-_WarningLine));
                warn=max(warn,step(1.5,_WarningMode)*(1-step(.5,abs(id.x-_WarningLine))));
                float4 border=float4(1-step(1,cell.x),step(8,cell.x),1-step(1,cell.y),step(8,cell.y));
                warn=max(warn,saturate(dot(border,_GateWarning)));
                warn=max(warn,_BreakCell.z*(1-step(.5,length(id-_BreakCell.xy))));
                color=lerp(color,float3(.68,.12,.14),warn*(.48+.16*sin(t*7)));

                // Boss 技能：按格取亮度，再按技能主色染色。格子中间实、边缘亮，像六章的格面脉冲。
                int index=(int)clamp(id.y*9+id.x,0,80);
                float fx=_CellFx[index];
                if(fx>.001)
                {
                    float pulse=fx*(.72+.28*rim+.5*diamond);
                    color=lerp(color,_SkillTint.rgb,saturate(pulse*.72));
                    color+=_SkillTint.rgb*fx*fx*.45;
                }
                // 冲击波：半径随时间增大，亮度随时间减弱，从命中点扫过整个棋盘。
                float age=t-_Shock.z;
                if(age>=0&&age<.9)
                {
                    float wave=Thin(length(cell-(_Shock.xy+.5))-age*11,.32);
                    color+=_SkillTint.rgb*wave*(1-age/.9)*_Shock.w*.9;
                }
                return float4(color,1);
            }
            ENDCG
        }
    }
}
