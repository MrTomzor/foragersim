Shader "Custom/LinearDepthMeters"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            ZWrite On
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 viewSpacePos = mul(UNITY_MATRIX_V, float4(i.worldPos, 1.0)).xyz;
                float linearDepth = -viewSpacePos.z;
                return float4(linearDepth, 0, 0, 1); // store depth in meters in red channel
            }
            ENDCG
        }
    }
}