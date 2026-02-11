Shader "Custom/HandThermalURP"
{
    Properties
    {
        _BaseMap      ("Albedo Map", 2D) = "white" {}
        _BaseColor    ("Albedo Color", Color) = (1,1,1,1)

        _ColdColor    ("Cold Glow Color", Color) = (0.2,0.6,1.0,1)
        _HotColor     ("Hot Glow Color", Color)  = (1.0,0.3,0.2,1)

        _HeatCenterWS ("Heat Center (WS)", Vector) = (0,0,0,0)
        _HeatRadius   ("Heat Radius", Float) = 0.05
        _HeatIntensity("Heat Intensity", Float) = 0.0
        _Temp01       ("Temperature 0-1", Float) = 0.5

        // 0 = tiny core, 1 = full radius
        _GlowCoverage ("Glow Coverage (0-1)", Float) = 0.7

        _HotTintStrength  ("Hot Tint Strength", Float)  = 2.0
        _ColdTintStrength ("Cold Tint Strength", Float) = 1.0
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "ForwardBase"
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend One Zero

            CGPROGRAM
            #pragma target 3.0

            #pragma vertex   vert
            #pragma fragment frag

            #pragma multi_compile_instancing
            #pragma multi_compile _ UNITY_SINGLE_PASS_STEREO
            #pragma multi_compile _ UNITY_STEREO_INSTANCING_ENABLED
            #pragma multi_compile _ UNITY_STEREO_MULTIVIEW_ENABLED

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos     : SV_POSITION;
                float2 uv      : TEXCOORD0;
                float3 worldPos: TEXCOORD1;

                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _BaseMap;
            float4    _BaseMap_ST;

            float4 _BaseColor;
            float4 _ColdColor;
            float4 _HotColor;
            float4 _HeatCenterWS;
            float  _HeatRadius;
            float  _HeatIntensity;
            float  _Temp01;
            float  _GlowCoverage;

            float  _HotTintStrength;
            float  _ColdTintStrength;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.pos = UnityObjectToClipPos(v.vertex);

                float4 worldPos4 = mul(unity_ObjectToWorld, v.vertex);
                o.worldPos = worldPos4.xyz;

                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                fixed4 baseCol = tex2D(_BaseMap, i.uv) * _BaseColor;

                float3 centerWS = _HeatCenterWS.xyz;
                float  radius   = max(_HeatRadius, 1e-4);

                float cov01     = saturate(_GlowCoverage);
                float effRadius = lerp(radius * 0.2, radius, cov01);

                float dist = distance(i.worldPos, centerWS);
                float t    = saturate(1.0 - dist / effRadius);

                // keep bright near center
                t = pow(t, 0.7);

                float temp = saturate(_Temp01);
                float3 heatColor = lerp(_ColdColor.rgb, _HotColor.rgb, temp);
                float  tintStrength = lerp(_ColdTintStrength, _HotTintStrength, temp);

                // raw intensity from script (no saturate here)
                float strength = _HeatIntensity * t;

                // use 0..1 range of strength to tint the albedo
                float glow = saturate(strength);         // for tint factor
                float extra = max(strength - 1.0, 0.0);  // extra for emissive halo

                // strongly tint base skin toward hot/cold color
                float mixFactor = saturate(glow * tintStrength);
                float3 tinted = lerp(baseCol.rgb, heatColor, mixFactor);

                // add soft emissive halo on top when intensity is strong
                float3 finalRGB = tinted + heatColor * extra * 0.5;

                return fixed4(finalRGB, baseCol.a);
            }
            ENDCG
        }
    }

    FallBack "Diffuse"
}
