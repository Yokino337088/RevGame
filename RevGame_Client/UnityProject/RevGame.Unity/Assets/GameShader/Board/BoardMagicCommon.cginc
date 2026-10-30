// 棋盘公共采样与坐标：只复用底层函数，各章的魔法图案在独立Shader内定义。
// 世界坐标转棋格坐标，不把Sprite Atlas UV当作0~1；图集采样仍使用Unity传入的原UV。
#include "UnityCG.cginc"
struct appdata { float4 vertex:POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; };
struct v2f { float4 vertex:SV_POSITION; float4 color:COLOR; float2 uv:TEXCOORD0; float2 world:TEXCOORD1; };
sampler2D _MainTex;
fixed4 _Color, _Tint, _Glow, _BossTint;
float _BossPulse;
float _Strength, _Speed, _CellSize, _Origin, _PreviewTime;
v2f vert(appdata v) { v2f o; o.vertex=UnityObjectToClipPos(v.vertex); o.world=mul(unity_ObjectToWorld,v.vertex).xy; o.uv=v.uv; o.color=v.color*_Color; return o; }
float clockTime() { return (_PreviewTime >= 0 ? _PreviewTime : _Time.y)*_Speed; }
float hash21(float2 p) { return frac(sin(dot(p,float2(127.1,311.7)))*43758.5453); }
// 平滑插值相邻随机值，得到没有固定条带方向的柔和云团；只供需要此形态的章节调用。
float softNoise(float2 p) {
 float2 cell=floor(p); float2 f=frac(p); f=f*f*(3-2*f);
 return lerp(lerp(hash21(cell),hash21(cell+float2(1,0)),f.x),lerp(hash21(cell+float2(0,1)),hash21(cell+1),f.x),f.y);
}
float thin(float d,float w) { return 1-smoothstep(w,w+0.012,abs(d)); }
float rim(float2 p) { return thin(max(abs(p.x),abs(p.y))-.435,.006); }
float mote(float2 p,float2 center,float radius) { return 1-smoothstep(radius*.3,radius,length(p-center)); }
float2 tileLocal(float2 world) { return frac((world-_Origin)/_CellSize)-.5; }
float2 tileId(float2 world) { return floor((world-_Origin)/_CellSize); }
float innerMask(float2 p) { return 1-smoothstep(.36,.48,max(abs(p.x),abs(p.y))); }
// 固定整数幂使用乘法，输入与原pow保持同一范围；共享给格面常亮纹，减少通用幂运算。
float power4(float x) { float square=x*x; return square*square; }
float power8(float x) { float fourth=power4(x); return fourth*fourth; }
// Boss命中时使用深色格面衬底和粗亮菱纹；独立于章节底色，因此冰雪盘不会吞掉浅色法纹。
// 只改变颜色，保留源alpha；每格仍共用章节材质，无新增Pass、贴图采样或全屏处理。
float3 bossImpactColor(float3 baseColor,float2 world) {
 [branch] if (_BossPulse <= 0.001) { return baseColor; }
 float2 p=tileLocal(world);
 float diamond=abs(p.x)+abs(p.y);
 float seal=1-smoothstep(.025,.065,abs(diamond-.35));
 float charge=pow(saturate(.5+.5*sin(diamond*35-_Time.y*11)),5);
 float border=1-smoothstep(.012,.045,abs(max(abs(p.x),abs(p.y))-.43));
 float3 dark=_BossTint.rgb*.085;
 float3 rune=_BossTint.rgb*(1.15+charge*.55)+float3(.3,.3,.3)*seal;
 float3 effect=lerp(dark,rune,saturate(seal+border+charge*.35));
 return lerp(baseColor,effect,saturate(_BossPulse)*.93);
}
fixed4 finishTile(v2f i,float light) {
 fixed4 base=tex2D(_MainTex,i.uv)*i.color;
 // 保留原图alpha和格线；低饱和底色承载细节，亮纹有上限，不使用叠加混合烫白棋盘。
 base.rgb=base.rgb*_Tint.rgb + _Glow.rgb*saturate(light)*_Strength;
 // 专属技能时格面亮起法纹，与常驻章节底色叠加，不覆盖原图透明度。
 base.rgb=bossImpactColor(base.rgb,i.world);
 return base;
}
// 明暗起伏只作用于底色的小范围，棋子与格线始终保留足够对比度。
fixed4 finishTileShaded(v2f i,float light,float shade) {
 fixed4 base=tex2D(_MainTex,i.uv)*i.color;
 base.rgb*= _Tint.rgb*(1+clamp(shade,-.7,1)*.18);
 base.rgb+=_Glow.rgb*saturate(light)*_Strength;
 // 专属技能时格面亮起法纹，与常驻章节底色叠加，不覆盖原图透明度。
 base.rgb=bossImpactColor(base.rgb,i.world);
 return base;
}
