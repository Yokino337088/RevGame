// 标准九路交叉点棋盘：沿用无尽的象牙大理石/金线语言，规则数据只由GoCore上传。
// 一个面片、共享材质、81点参数块；所有呼吸光都是表现，不参与提子或死活判定。
Shader "RevGame/Pvp/IvoryGoBoard"
{
    Properties
    {
        _Ivory("象牙白棋盘",Color)=(.95,.91,.81,1)
        _Gold("神殿金线",Color)=(.47,.33,.13,1)
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
            float4 _Ivory, _Gold;
            float _Clock, _ShowHints, _ShowTerritory, _Selected, _Last, _Turn;
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
            float Segment(float2 p,float2 a,float2 b)
            {
                float2 q=p-a;float2 v=b-a;
                return Line(length(q-v*saturate(dot(q,v)/dot(v,v))),.055);
            }
            // 气数使用七段小徽标，不依赖动态图集字形。十气以上显示9+，避免误写成精确9。
            float Digit(float2 p,float value)
            {
                int d=(int)min(9,value);float n=0;
                if(d!=1&&d!=4) n=max(n,Segment(p,float2(-.2,.37),float2(.2,.37)));
                if(d!=1&&d!=2&&d!=3&&d!=7) n=max(n,Segment(p,float2(-.25,.32),float2(-.25,.05)));
                if(d!=5&&d!=6) n=max(n,Segment(p,float2(.25,.32),float2(.25,.05)));
                if(d!=0&&d!=1&&d!=7) n=max(n,Segment(p,float2(-.2,0),float2(.2,0)));
                if(d==0||d==2||d==6||d==8) n=max(n,Segment(p,float2(-.25,-.05),float2(-.25,-.32)));
                if(d!=2) n=max(n,Segment(p,float2(.25,-.05),float2(.25,-.32)));
                if(d!=1&&d!=4&&d!=7) n=max(n,Segment(p,float2(-.2,-.37),float2(.2,-.37)));
                return n;
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
                // 边框和九条线使用交叉点坐标；不会把无尽的9格误当成10路围棋。
                float inside=step(0,boardPoint.x)*step(boardPoint.x,8)*step(0,boardPoint.y)*step(boardPoint.y,8);
                float grid=max(Line(p.x,.011)*step(0,boardPoint.y)*step(boardPoint.y,8),Line(p.y,.011)*step(0,boardPoint.x)*step(boardPoint.x,8));
                float border=Line(max(abs(boardPoint.x-4),abs(boardPoint.y-4))-4.43,.025);
                col=lerp(col,_Gold.rgb,max(grid*.82,border));
                bool star=(id.x==2||id.x==6)&&(id.y==2||id.y==6)||(id.x==4&&id.y==4);
                if(star) col=lerp(col,_Gold.rgb,Disk(r,.06));
                if(_ShowTerritory>.5&&cell.z>.5)
                {
                    float3 territory=cell.z<1.5?float3(.05,.37,.32):float3(.62,.74,1);
                    float edge=max(abs(p.x),abs(p.y));
                    float wave=.8+.2*sin(t*2-length(boardPoint-4)*1.5);
                    col=lerp(col,territory,(.25+.16*Line(edge-.44,.012))*wave);
                }
                if(_ShowHints>.5&&cell.x<.5)
                {
                    if(mark.x>.5)
                    {
                        float breath=.7+.3*sin(t*5);
                        float ring=Line(r-(.19+.025*sin(t*4)),.025);
                        col=lerp(col,float3(.02,.65,.67),saturate(ring*breath+Disk(r,.065)*.85));
                    }
                    if(cell.w>.5)
                    {
                        float angle=atan2(p.y,p.x);
                        float ring=Line(r-.30,.017);
                        float dash=cell.w>1.5?step(0,sin(angle*8)):1;
                        float center=cell.w>1.5?max(Line(p.x-p.y,.018),Line(p.x+p.y,.018))*Disk(r,.09):Disk(r,.045);
                        col=lerp(col,cell.w>1.5?float3(.85,.41,.09):float3(.04,.48,.64),max(ring*dash,center));
                    }
                    if(mark.z>.5)
                    {
                        float ring=Line(r-(mark.z<1.5?.36:.25),.025);
                        col=lerp(col,float3(.91,.51,.04),ring*(.72+.28*sin(t*4)));
                    }
                }
                float stone=cell.x;
                bool preview=abs(index-_Selected)<.1&&stone<.5;
                if(preview) stone=_Turn;
                if(stone>.5)
                {
                    float shadow=Disk(length(p-float2(.035,-.055)),.42)*.22;
                    col*=1-shadow;
                    float mask=Disk(r,.395);
                    float z=sqrt(saturate(1-dot(p/.4,p/.4)));
                    float3 normal=float3(p/.4,z);
                    float light=saturate(dot(normal,normalize(float3(-.45,.65,1))));
                    float spec=pow(saturate(dot(normal,normalize(float3(-.28,.38,1)))),32);
                    float3 body=stone<1.5?float3(.025,.035,.045)+light*.065+spec*.25:float3(.66,.68,.69)+light*.28+spec*.12;
                    float opacity=preview?.38:1;
                    if(mark.w>.5) opacity=.35;
                    col=lerp(col,body,mask*opacity);
                    if(_ShowHints>.5&&abs(cell.y)==1)
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
                        col=lerp(col,float3(.98,.63,.12),Line(r-.12,.018));
                    }
                    if(_ShowHints>.5&&cell.y>0)
                    {
                        float2 badge=(p-float2(.24,-.24))/.26;
                        float bg=Disk(length(badge),.57);
                        col=lerp(col,cell.y<=2?float3(.75,.08,.07):float3(.03,.28,.30),bg);
                        col=lerp(col,float3(1,.97,.85),Digit(badge,cell.y));
                        if(cell.y>9) col=lerp(col,float3(1,.97,.85),max(Line(badge.x-.43,.02)*step(abs(badge.y),.1),Line(badge.y,.02)*step(abs(badge.x-.43),.09)));
                    }
                }
                if(abs(index-_Selected)<.1)
                {
                    float edge=max(abs(p.x),abs(p.y));
                    col=lerp(col,float3(.1,.56,.61),Line(edge-.46,.018)*(.65+.25*sin(t*3)));
                }
                return float4(col,1);
            }
            ENDCG
        }
    }
}
