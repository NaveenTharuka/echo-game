// Soft additive rim (fresnel) glow for the interactable under the crosshair. Drawn on a copy of the object's mesh
// (InteractionHighlight), so no level material is changed. Additive and unlit: it shows in the dark, but only on the
// silhouette edges, and it breathes slowly so it reads as "usable" without looking like a game pickup sparkle.
Shader "Echo/InteractRim"
{
    Properties
    {
        _Color ("Rim Colour", Color) = (1, 0.82, 0.55, 1)
        _Power ("Rim Width (higher = thinner)", Range(0.5, 8)) = 2.5
        _Strength ("Strength", Range(0, 2)) = 0.45
        _PulseSpeed ("Pulse Speed", Range(0, 6)) = 2.2
        _PulseDepth ("Pulse Depth", Range(0, 1)) = 0.3
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Pass
        {
            Name "Rim"
            Tags { "LightMode" = "UniversalForward" }
            Blend One One
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half _Power, _Strength, _PulseSpeed, _PulseDepth;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 normalWS : TEXCOORD0; float3 viewWS : TEXCOORD1; };

            Varyings vert(Attributes v)
            {
                Varyings o;
                VertexPositionInputs p = GetVertexPositionInputs(v.positionOS.xyz);
                o.positionCS = p.positionCS;
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.viewWS = GetWorldSpaceViewDir(p.positionWS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half facing = saturate(dot(normalize(i.normalWS), normalize(i.viewWS)));
                half rim = pow(1.0h - facing, _Power);
                half pulse = 1.0h - _PulseDepth * (0.5h + 0.5h * sin(_Time.y * _PulseSpeed));
                return half4(_Color.rgb * (rim * _Strength * pulse), 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
