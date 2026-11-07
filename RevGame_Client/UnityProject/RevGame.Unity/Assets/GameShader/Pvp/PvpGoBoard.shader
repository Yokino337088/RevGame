// 九路围棋使用无尽同款象牙金格外观；每格中心对应一个规则落点，规则数据只由GoCore上传。
// 一个面片、共享材质、81点参数块；所有呼吸光都是表现，不参与提子或死活判定。
Shader "RevGame/Pvp/IvoryGoBoard"
{
    Properties
    {
        _Ivory("象牙白棋盘",Color)=(.95,.91,.81,1)
        _Gold("神殿金线",Color)=(.70,.51,.24,1)
        _EyeTex("双眼图集",2D)="white" {}
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Off ZWrite On Blend Off
        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float4 _Cells[81], _Marks[81];
            float4 _Ivory, _Gold, _EyeUv;
            sampler2D _EyeTex;
            float _Clock, _ShowHints, _ShowTerritory, _Selected, _Last, _Turn, _ScoreProgress;
            struct V { float4 pos:SV_POSITION; float2 uv:TEXCOORD0; };
            V vert(appdata_base v)
            {
                V o; o.pos=UnityObjectToClipPos(v.vertex); o.uv=v.texcoord; return o;
            }
            float Line(float d,float w)
            {
                // 最近交叉点坐标在半格处跳变，导数必须限宽，否则半格边界会画出额外的假棋线。
                return 1-smoothstep(w,w+max(min(fwidth(d),.02),.003),abs(d));
            }
            float Disk(float d,float r)
            {
                return 1-smoothstep(r,r+max(min(fwidth(d),.02),.003),d);
            }
            float _ComputerPreview, _ComputerLand, _ComputerDrop;

            // 流光铺在整格内，距离方形边缘决定亮边。数值只控制画面，不参与落子规则。
            float3 MagicTile(float3 baseColor, float2 p, float edge, float clock, bool incoming)
            {
                float wave = .5 + .5 * sin(p.x * 17 + p.y * 12 - clock * 7 + sin(p.y * 15 + clock * 3));
                float veins = pow(wave, 4);
                float rim = Line(edge - .43, .028);
                float3 glow = incoming ? float3(1,.86,.38) : float3(1,.085,.12);
                float3 tint = incoming ? float3(.85,.39,.075) : float3(.64,.025,.05);
                float inside = 1 - smoothstep(.45,.50,edge);
                return lerp(baseColor, lerp(tint, glow, saturate(veins*.8+rim)), inside*(.56+.36*rim));
            }

            fixed4 frag(V input):SV_Target
            {
                float2 boardPoint=input.uv*9-.5;
                float2 id=clamp(floor(boardPoint+.5),0,8);
                float2 p=boardPoint-id;
                int index=(int)(id.y*9+id.x);
                float4 cell=_Cells[index], mark=_Marks[index];
                float r=length(p), t=_Clock;
                float marble=sin(boardPoint.x*2.2+sin(boardPoint.y*1.8))*.012+sin(boardPoint.y*5.1+boardPoint.x*.7)*.006;
                float3 col=_Ivory.rgb+marble;
                // 与无尽StarGrid同一格面公式：金线围绕格子，棋子落在格心，不画经典交叉点与星位。
                float edge=max(abs(p.x),abs(p.y));
                float grid=smoothstep(.465,.49,edge);
                float rim=smoothstep(.38,.47,edge)*(1-grid);
                float angle=atan2(p.y,p.x);
                float breath=.64+.36*sin(t*.85);
                float ray=pow(saturate(.5+.5*cos(angle*8-t*.12)),14)*smoothstep(.12,.22,r)*(1-smoothstep(.38,.49,r));
                float halo=Line(r-.29,.006)*(.53+.47*sin(angle*8-t*.23));
                float diamond=Line(abs(p.x)+abs(p.y)-.36,.005);
                float shaft=pow(saturate(.5+.5*sin(boardPoint.x*.42-boardPoint.y*.68-t*.3)),8);
                float light=ray*.34+halo*.30+diamond*.16+shaft*.20+rim*(.16+breath*.08);
                col*=fmod(id.x+id.y,2)<1?1:.965;
                col=lerp(col,_Gold.rgb,grid*.78);
                col+=float3(1,.80,.36)*light*breath*.55;
                // 同色空点连成片，轮廓只沿所有权交界流光。终局用同一格序号驱动扫光和右栏计数。
                bool scoring=_ScoreProgress>=0;
                float counted=step(cell.y,_ScoreProgress);
                float sweep=counted*saturate(1-(_ScoreProgress-cell.y)/4);
                float3 teal=float3(.035,.37,.40);
                float3 amber=float3(.95,.54,.11);
                if((_ShowTerritory>.5&&!scoring||scoring&&counted>.5)&&cell.z>.5)
                {
                    float3 territory=cell.z<1.5?teal:amber;
                    float2 adjacent=id+step(abs(p.yx),abs(p))*sign(p);
                    int neighbor=(int)(clamp(adjacent.y,0,8)*9+clamp(adjacent.x,0,8));
                    float border=step(.1,abs(_Cells[neighbor].z-cell.z));
                    border=max(border,step(.1,length(adjacent-clamp(adjacent,0,8))));
                    float contour=Line(edge-.44,.032)*border;
                    float hatch=.5+.5*sin((boardPoint.x+boardPoint.y*(cell.z<1.5?1:-1))*13-t*.9);
                    float flow=.65+.35*sin(t*3-boardPoint.x*1.5-boardPoint.y);
                    col=lerp(col,territory,.57+hatch*.09);
                    col=lerp(col,cell.z<1.5?float3(.25,1,.91):float3(1,.94,.54),contour*flow);
                }
                if(_ShowHints>.5&&cell.x<.5)
                {
                    if(mark.z>.5)
                    {
                        // 流光先铺底，再画气口与眼位。共用格上的黑白标记保持自己的颜色。
                        col=MagicTile(col,p,edge,t,false);
                    }
                    if(mark.x>.5)
                    {
                        // 同格兼属两边时画外黑内白；浅底上的白圈用深描边保持可读。
                        float radius=.22+.012*sin(t*4);
                        if(mark.x<1.5||mark.x>2.5)
                        {
                            float blackRing=r-radius;
                            col=lerp(col,float3(.83,.85,.87),Line(blackRing,.034));
                            col=lerp(col,float3(.04,.045,.055),Line(blackRing,.024));
                        }
                        if(mark.x>1.5)
                        {
                            float whiteRing=r-(mark.x>2.5?radius-.095:radius);
                            col=lerp(col,float3(.28,.25,.22),Line(whiteRing,.035));
                            col=lerp(col,float3(1,1,1),Line(whiteRing,.023));
                        }
                    }
                    if(cell.w>.5&&!scoring&&abs(p.x)<.33&&abs(p.y)<.22)
                    {
                        float2 localUv=p/float2(.66,.44)+.5;
                        float4 eyes=tex2D(_EyeTex,lerp(_EyeUv.xy,_EyeUv.zw,localUv));
                        col=lerp(col,eyes.rgb,eyes.a);
                    }
                }
                if(!scoring&&abs(index-_ComputerPreview)<.1)
                {
                    col=MagicTile(col,p,edge,t,true);
                }
                float stone=cell.x;
                bool preview=abs(index-_Selected)<.1&&stone<.5;
                if(preview) stone=_Turn;
                if(stone>.5&&(!scoring||mark.w<.5))
                {
                    // 棋子已经通过规则提交。这里仅把新白子从格子上方压到中心并放大，棋谱仍只有一手。
                    float drop = abs(index-_ComputerLand)<.1 ? _ComputerDrop : 1;
                    float2 stonePoint = p - float2(0,(1-drop)*.17);
                    float stoneRadius = lerp(.27,.395,drop);
                    float shadow=Disk(length(p-float2(.035,-.055)),.42)*.22*drop;
                    col*=1-shadow;
                    float mask=Disk(length(stonePoint),stoneRadius);
                    float2 normalXY=stonePoint/(stoneRadius+.005);
                    float z=sqrt(saturate(1-dot(normalXY,normalXY)));
                    float3 normal=float3(normalXY,z);
                    float light=saturate(dot(normal,normalize(float3(-.45,.65,1))));
                    float spec=pow(saturate(dot(normal,normalize(float3(-.28,.38,1)))),32);
                    float3 body=stone<1.5?float3(.025,.035,.045)+light*.065+spec*.25:float3(.66,.68,.69)+light*.28+spec*.12;
                    float opacity=preview?.38:1;
                    if(mark.w>.5) opacity=.35;
                    col=lerp(col,body,mask*opacity);
                    if(_ShowHints>.5&&!scoring&&cell.y==1)
                    {
                        float pulse=.58+.42*sin(t*10.472);
                        col=lerp(col,float3(1,.10,.06),Line(r-.41,.027)*pulse);
                    }
                    if(_ShowHints>.5&&mark.y>.5)
                    {
                        col=lerp(col,float3(.2,.85,1),mask*(.25+.25*sin(t*4)));
                        col=lerp(col,float3(.04,.5,.7),Line(r-.35,.018));
                    }
                    if(mark.w>.5)
                    {
                        col=lerp(col,float3(.75,.11,.13),max(Line(p.x-p.y,.025),Line(p.x+p.y,.025))*Disk(r,.23));
                    }
                    if(abs(index-_Last)<.1)
                    {
                        col=lerp(col,float3(.95,.18,.16),Line(edge-.44,.014)*(.65+.35*sin(t*4)));
                    }
                    if(scoring&&counted>.5)
                    {
                        float3 tally=stone<1.5?float3(.32,1,.94):float3(1,.75,.27);
                        col=lerp(col,tally,Line(r-.39,.025)*(.6+.4*sweep));
                    }
                }
                if(scoring)
                {
                    if(counted>.5&&cell.z<.5&&(cell.x<.5||mark.w>.5))
                    {
                        col=lerp(col,float3(.46,.53,.57),.32);
                    }
                    col=lerp(col,float3(1,.94,.68),sweep*(.26+.74*Line(edge-.42,.043)));
                }
                if(abs(index-_Selected)<.1)
                {
                    col=lerp(col,float3(.1,.56,.61),Line(edge-.46,.018)*(.65+.25*sin(t*3)));
                }
                return float4(col,1);
            }
            ENDCG
        }
    }
}
